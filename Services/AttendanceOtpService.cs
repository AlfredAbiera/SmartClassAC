using System.Globalization;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed class AttendanceOtpService(
    IConfiguration configuration, IOptions<SchoolTimeOptions> schoolTime,
    PortalMobileOtp otp, MariaDbUserAccountRepository accounts)
{
    private string ConnectionString => configuration.GetConnectionString("SmartClassMariaDb")
        ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb for attendance verification.");

    private sealed record Candidate(long DeviceRowId, string DeviceId, DateTime PairedAt, DateTime DeviceUpdated, long TeacherId, DateTime TeacherUpdated,
        long ScheduleId, long RoomId, DateOnly Date, TimeOnly Start, TimeOnly End, DateTime Updated,
        long SubstitutionVersion, string Phone, long? AttendanceId, long? Generation, bool IsOpen,
        string? Verification, string? Outcome, int ExtensionMinutes)
    {
        public string Binding(string nonce, string action) => FormattableString.Invariant(
            $"{nonce}:{action}:{DeviceRowId}:{DeviceId}:{PairedAt.Ticks}:{DeviceUpdated.Ticks}:{TeacherId}:{TeacherUpdated.Ticks}:{ScheduleId}:{RoomId}:{Date.DayNumber}:{Start.Ticks}:{End.Ticks}:{Updated.Ticks}:{SubstitutionVersion}:{AttendanceId}:{Generation}:{IsOpen}:{ExtensionMinutes}");
    }

    public async Task<OperationResult> RequestAsync(long adminId, long scheduleId, string action)
    {
        if (adminId <= 0 || scheduleId <= 0 || action is not ("in" or "out"))
            return OperationResult.Failure("Choose a class and attendance action.");
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var admin = connection.CreateCommand())
        {
            admin.Transaction = transaction;
            admin.CommandText = "SELECT id FROM user_accounts WHERE id=@id AND role='Admin' AND is_active=TRUE FOR UPDATE;";
            admin.Parameters.Add("@id", MySqlDbType.Int64).Value = adminId;
            if (await admin.ExecuteScalarAsync() is null)
                return OperationResult.Failure("You are not authorized to request attendance verification.");
        }
        var candidate = await FindCandidateAsync(connection, transaction, scheduleId, null, action);
        if (candidate is null)
            return OperationResult.Failure("This class is not eligible for that action, or its assigned scanner is unavailable.");
        var binding = candidate.Binding(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), action);
        await using (var save = connection.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = """
                INSERT INTO attendance_otp_requests (device_id, admin_id, schedule_id, action_name, binding)
                VALUES (@device, @admin, @schedule, @action, @binding)
                ON DUPLICATE KEY UPDATE admin_id=VALUES(admin_id), schedule_id=VALUES(schedule_id),
                    action_name=VALUES(action_name), binding=VALUES(binding);
                """;
            save.Parameters.Add("@device", MySqlDbType.Int64).Value = candidate.DeviceRowId;
            save.Parameters.Add("@admin", MySqlDbType.Int64).Value = adminId;
            save.Parameters.Add("@schedule", MySqlDbType.Int64).Value = scheduleId;
            save.Parameters.Add("@action", MySqlDbType.VarChar).Value = action;
            save.Parameters.Add("@binding", MySqlDbType.VarChar).Value = binding;
            await save.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        var result = await otp.IssueAsync(adminId, "attendance_" + action, scheduleId, candidate.Phone,
            binding, candidate.Date, candidate.Start);
        if (!result.Succeeded) return result;
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT binding FROM attendance_otp_requests WHERE device_id=@device;";
        check.Parameters.Add("@device", MySqlDbType.Int64).Value = candidate.DeviceRowId;
        return (await check.ExecuteScalarAsync() as string) == binding
            ? OperationResult.Success()
            : OperationResult.Failure("This request was replaced by another attendance code. Request a new code.");
    }

    public async Task<bool> RedeemAsync(string deviceId, string code, CancellationToken cancellationToken)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || !MobileVerificationCodes.IsValid(code))
            return false;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long adminId, scheduleId, deviceRowId;
        string action, binding;
        await using (var request = connection.CreateCommand())
        {
            request.Transaction = transaction;
            request.CommandText = """
                SELECT r.admin_id, r.schedule_id, r.device_id, r.action_name, r.binding
                FROM attendance_otp_requests r JOIN biometric_devices d ON d.id=r.device_id
                WHERE d.device_id=@device AND d.is_active=TRUE AND d.paired_at IS NOT NULL
                FOR UPDATE;
                """;
            request.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
            await using var reader = await request.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            adminId = reader.GetInt64(0);
            scheduleId = reader.GetInt64(1);
            deviceRowId = reader.GetInt64(2);
            action = reader.GetString(3);
            binding = reader.GetString(4);
        }
        if (action is not ("in" or "out") || binding.Length < 33 || binding[32] != ':')
            return false;
        var candidate = await FindCandidateAsync(connection, transaction, scheduleId, deviceId, action, cancellationToken);
        if (candidate is null || candidate.Binding(binding[..32], action) != binding)
            return false;
        if (!await PortalMobileOtp.ConsumeAsync(connection, transaction, configuration, adminId,
                "attendance_" + action, scheduleId, candidate.Phone, code, cancellationToken, binding))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        var now = SchoolClock.GetNow(schoolTime.Value);
        var date = DateOnly.FromDateTime(now);
        var time = TimeOnly.FromDateTime(now);
        bool succeeded;
        if (action == "out")
            succeeded = (await accounts.EndClassInTransactionAsync(connection, transaction, candidate.TeacherId,
                scheduleId, candidate.RoomId, cancellationToken)).Succeeded;
        else if (candidate.AttendanceId is not null)
            succeeded = await accounts.TryInWindowAcReentryInTransactionAsync(connection, transaction,
                candidate.TeacherId, scheduleId, candidate.RoomId, date, time, cancellationToken) is not null;
        else
            succeeded = (await accounts.StartClassInTransactionAsync(connection, transaction, candidate.TeacherId,
                scheduleId, date, time, "Admin-approved SMS OTP", true, cancellationToken)).Succeeded;
        if (!succeeded) return false;
        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO attendance_otp_events (admin_id, teacher_id, schedule_id, device_id, action_name)
                VALUES (@admin, @teacher, @schedule, @device, @action);
                DELETE FROM attendance_otp_requests WHERE device_id=@device;
                """;
            audit.Parameters.Add("@admin", MySqlDbType.Int64).Value = adminId;
            audit.Parameters.Add("@teacher", MySqlDbType.Int64).Value = candidate.TeacherId;
            audit.Parameters.Add("@schedule", MySqlDbType.Int64).Value = scheduleId;
            audit.Parameters.Add("@device", MySqlDbType.Int64).Value = deviceRowId;
            audit.Parameters.Add("@action", MySqlDbType.VarChar).Value = action;
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<Candidate?> FindCandidateAsync(MySqlConnection connection, MySqlTransaction transaction,
        long scheduleId, string? deviceId, string action, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT d.id, d.device_id, d.paired_at, s.teacher_account_id, s.id, s.classroom_id,
                s.schedule_date, s.start_time, s.end_time, s.updated_utc, s.substitution_version, u.phone,
                a.id, a.session_generation, a.time_out_utc IS NULL, a.verification_method, a.outcome,
                COALESCE(a.class_extension_minutes, 0), d.updated_utc, u.updated_utc
            FROM class_schedules s
            JOIN semesters sem ON sem.id=s.semester_id AND sem.is_active=TRUE
            JOIN teacher_semesters e ON e.semester_id=s.semester_id AND e.teacher_account_id=s.teacher_account_id
            JOIN user_accounts u ON u.id=s.teacher_account_id AND u.role='Teacher' AND u.is_active=TRUE AND u.phone_verified=TRUE
            JOIN classrooms c ON c.id=s.classroom_id AND c.is_active=TRUE
            JOIN biometric_devices d ON d.classroom_id=s.classroom_id AND d.is_active=TRUE
                AND d.attendance_scan_enabled=TRUE AND d.paired_at IS NOT NULL
            LEFT JOIN attendance_logs a ON a.class_schedule_id=s.id AND a.teacher_account_id=s.teacher_account_id
            WHERE s.id=@schedule AND (@device IS NULL OR d.device_id=@device)
              AND s.schedule_date BETWEEN sem.start_date AND sem.end_date
              AND (COALESCE(s.original_teacher_account_id,s.teacher_account_id)=s.teacher_account_id OR s.substitution_verified=TRUE)
              AND NOT EXISTS (SELECT 1 FROM class_schedules replacement WHERE replacement.replaces_schedule_id=s.id)
            FOR UPDATE;
            """;
        command.Parameters.Add("@schedule", MySqlDbType.Int64).Value = scheduleId;
        command.Parameters.Add("@device", MySqlDbType.VarChar).Value = (object?)deviceId ?? DBNull.Value;
        Candidate candidate;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(11)) return null;
            candidate = new Candidate(reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2), reader.GetDateTime(18),
                reader.GetInt64(3), reader.GetDateTime(19), reader.GetInt64(4), reader.GetInt64(5), reader.GetFieldValue<DateOnly>(6),
                TimeOnly.FromTimeSpan(reader.GetTimeSpan(7)), TimeOnly.FromTimeSpan(reader.GetTimeSpan(8)),
                reader.GetDateTime(9), reader.GetInt64(10), reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12), reader.IsDBNull(13) ? null : reader.GetInt64(13),
                !reader.IsDBNull(12) && reader.GetBoolean(14), reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16), reader.GetInt32(17));
        }
        if (!GsmSmsTransport.IsAsciiMobile(candidate.Phone)) return null;
        var now = SchoolClock.GetNow(schoolTime.Value);
        var start = candidate.Date.ToDateTime(candidate.Start);
        var end = candidate.Date.ToDateTime(candidate.End);
        if (action == "out")
            return candidate.IsOpen && now >= start.AddMinutes(-15) &&
                now.Date <= start.Date &&
                now < end.AddMinutes(candidate.ExtensionMinutes + Math.Clamp(schoolTime.Value.MissingTimeOutGraceMinutes, 0, 29))
                ? candidate : null;
        if (now < start.AddMinutes(-15) || now >= end || candidate.IsOpen ||
            candidate.AttendanceId is not null && (candidate.Verification == "System" || candidate.Outcome == "Absent"))
            return null;
        await using var active = connection.CreateCommand();
        active.Transaction = transaction;
        active.CommandText = "SELECT id FROM attendance_logs WHERE teacher_account_id=@teacher AND time_out_utc IS NULL LIMIT 1 FOR UPDATE;";
        active.Parameters.Add("@teacher", MySqlDbType.Int64).Value = candidate.TeacherId;
        return await active.ExecuteScalarAsync(cancellationToken) is null ? candidate : null;
    }
}
