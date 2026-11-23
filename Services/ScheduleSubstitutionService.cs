using System.Globalization;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed class ScheduleSubstitutionService(
    IConfiguration configuration, IOptions<SchoolTimeOptions> schoolTime, PortalMobileOtp otp)
{
    private string ConnectionString => configuration.GetConnectionString("SmartClassMariaDb")
        ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb for schedule substitutions.");

    private sealed record Candidate(long ScheduleId, long SubstituteId, long TeacherId, long OriginalTeacherId,
        long ClassroomId, long SemesterId, DateOnly Date, TimeOnly Start, TimeOnly End, long Version, string Phone)
    {
        public string Binding => FormattableString.Invariant(
            $"{ScheduleId}:{SubstituteId}:{TeacherId}:{OriginalTeacherId}:{ClassroomId}:{SemesterId}:{Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}:{Start.Ticks}:{End.Ticks}:{Version}");
    }

    public async Task<OperationResult> RequestAsync(long adminId, long scheduleId, long substituteId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var candidate = await FindCandidateAsync(connection, null, scheduleId, substituteId);
        if (candidate is null || HasEnded(candidate))
            return OperationResult.Failure("Select an available class and a different active teacher enrolled in its semester. The class must not have ended or have attendance.");
        if (await HasConflictAsync(connection, null, candidate.ScheduleId, substituteId,
                candidate.Date, candidate.Start, candidate.End))
            return OperationResult.Failure("The substitute has an overlapping class schedule.");

        return await otp.IssueAsync(adminId, "substitute", scheduleId, candidate.Phone,
            candidate.Binding, candidate.Date, candidate.Start);
    }

    public async Task<OperationResult> ConfirmAsync(long adminId, long scheduleId, long substituteId, string code)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var candidate = await FindCandidateAsync(connection, transaction, scheduleId, substituteId);
        if (candidate is null || HasEnded(candidate))
            return OperationResult.Failure("The class or substitute is no longer available. Review the schedule and request a new code.");

        if (!await PortalMobileOtp.ConsumeAsync(connection, transaction, configuration, adminId, "substitute",
                scheduleId, candidate.Phone, code, CancellationToken.None, candidate.Binding))
        {
            await transaction.CommitAsync();
            return OperationResult.Failure("Invalid, expired, or unavailable verification code. Request a new code if needed.");
        }

        if (await HasConflictAsync(connection, transaction, scheduleId, substituteId,
                candidate.Date, candidate.Start, candidate.End))
        {
            await transaction.RollbackAsync();
            return OperationResult.Failure("The substitute has an overlapping class schedule.");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE class_schedules
            SET original_teacher_account_id = @original, teacher_account_id = @teacher, substitution_verified = TRUE,
                substitution_version = substitution_version + 1
            WHERE id = @schedule;
            """;
        update.Parameters.Add("@original", MySqlDbType.Int64).Value = candidate.OriginalTeacherId;
        update.Parameters.Add("@teacher", MySqlDbType.Int64).Value = substituteId;
        update.Parameters.Add("@schedule", MySqlDbType.Int64).Value = scheduleId;
        if (await update.ExecuteNonQueryAsync() != 1)
            throw new InvalidOperationException("The substitution could not be saved.");
        await transaction.CommitAsync();
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveAsync(long adminId, long scheduleId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var admin = connection.CreateCommand();
        admin.Transaction = transaction;
        admin.CommandText = "SELECT id FROM user_accounts WHERE id = @id AND role = 'Admin' AND is_active = TRUE FOR UPDATE;";
        admin.Parameters.Add("@id", MySqlDbType.Int64).Value = adminId;
        if (await admin.ExecuteScalarAsync() is null)
            return OperationResult.Failure("You are not authorized to remove a substitution.");

        await using var schedule = connection.CreateCommand();
        schedule.Transaction = transaction;
        schedule.CommandText = """
            SELECT s.original_teacher_account_id, s.schedule_date, s.start_time, s.end_time,
                   EXISTS(SELECT 1 FROM attendance_logs a WHERE a.class_schedule_id = s.id)
            FROM class_schedules s
            WHERE s.id = @id AND s.original_teacher_account_id IS NOT NULL
              AND s.original_teacher_account_id <> s.teacher_account_id FOR UPDATE;
            """;
        schedule.Parameters.Add("@id", MySqlDbType.Int64).Value = scheduleId;
        long originalId;
        DateOnly date;
        TimeSpan start, end;
        await using (var reader = await schedule.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync() || reader.GetBoolean(4))
                return OperationResult.Failure("The substitution cannot be removed after attendance has been recorded.");
            originalId = reader.GetInt64(0);
            date = reader.GetFieldValue<DateOnly>(1);
            start = reader.GetTimeSpan(2);
            end = reader.GetTimeSpan(3);
        }
        var now = SchoolClock.GetNow(schoolTime.Value);
        if (date < DateOnly.FromDateTime(now) || date == DateOnly.FromDateTime(now) && end <= now.TimeOfDay)
            return OperationResult.Failure("The class has already ended.");

        if (await HasConflictAsync(connection, transaction, scheduleId, originalId,
                date, TimeOnly.FromTimeSpan(start), TimeOnly.FromTimeSpan(end)))
            return OperationResult.Failure("The original teacher now has an overlapping class schedule.");

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE class_schedules SET teacher_account_id = @teacher, substitution_verified = FALSE,
                substitution_version = substitution_version + 1 WHERE id = @id;
            """;
        update.Parameters.Add("@teacher", MySqlDbType.Int64).Value = originalId;
        update.Parameters.Add("@id", MySqlDbType.Int64).Value = scheduleId;
        if (await update.ExecuteNonQueryAsync() != 1)
            throw new InvalidOperationException("The substitution could not be removed.");
        await transaction.CommitAsync();
        return OperationResult.Success();
    }

    private bool HasEnded(Candidate candidate)
    {
        var now = SchoolClock.GetNow(schoolTime.Value);
        var today = DateOnly.FromDateTime(now);
        return candidate.Date < today || candidate.Date == today && candidate.End <= TimeOnly.FromDateTime(now);
    }

    private static async Task<bool> HasConflictAsync(MySqlConnection connection, MySqlTransaction? transaction,
        long scheduleId, long teacherId, DateOnly date, TimeOnly start, TimeOnly end)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.id FROM class_schedules s
            WHERE s.id <> @schedule AND s.teacher_account_id = @teacher AND s.schedule_date = @date
              AND s.start_time < @end
              AND ADDTIME(s.end_time, SEC_TO_TIME(COALESCE(
                    (SELECT MAX(a.class_extension_minutes) FROM attendance_logs a WHERE a.class_schedule_id = s.id), 0) * 60)) > @start
              AND NOT EXISTS (SELECT 1 FROM class_schedules replacement WHERE replacement.replaces_schedule_id = s.id)
            LIMIT 1
            """ + (transaction is null ? ";" : " FOR UPDATE;");
        command.Parameters.Add("@schedule", MySqlDbType.Int64).Value = scheduleId;
        command.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
        command.Parameters.Add("@date", MySqlDbType.Date).Value = date;
        command.Parameters.Add("@start", MySqlDbType.Time).Value = start.ToTimeSpan();
        command.Parameters.Add("@end", MySqlDbType.Time).Value = end.ToTimeSpan();
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<Candidate?> FindCandidateAsync(
        MySqlConnection connection, MySqlTransaction? transaction, long scheduleId, long substituteId)
    {
        if (scheduleId <= 0 || substituteId <= 0) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.id, s.teacher_account_id, COALESCE(s.original_teacher_account_id, s.teacher_account_id),
                   s.classroom_id, s.semester_id, s.schedule_date, s.start_time, s.end_time,
                   teacher.phone, s.substitution_version
            FROM class_schedules s
            INNER JOIN semesters sem ON sem.id = s.semester_id AND sem.is_active = TRUE
            INNER JOIN classrooms room ON room.id = s.classroom_id AND room.is_active = TRUE
            INNER JOIN user_accounts teacher ON teacher.id = @teacher AND teacher.role = 'Teacher'
                AND teacher.is_active = TRUE AND teacher.phone_verified = TRUE
            INNER JOIN teacher_semesters enrollment ON enrollment.teacher_account_id = teacher.id
                AND enrollment.semester_id = s.semester_id
            WHERE s.id = @schedule
              AND s.schedule_date BETWEEN sem.start_date AND sem.end_date
              AND (@teacher <> s.teacher_account_id OR s.substitution_verified = FALSE)
              AND @teacher <> COALESCE(s.original_teacher_account_id, s.teacher_account_id)
              AND NOT EXISTS (SELECT 1 FROM attendance_logs a WHERE a.class_schedule_id = s.id)
              AND NOT EXISTS (SELECT 1 FROM class_schedules replacement WHERE replacement.replaces_schedule_id = s.id)
            """ + (transaction is null ? ";" : " FOR UPDATE;");
        command.Parameters.Add("@teacher", MySqlDbType.Int64).Value = substituteId;
        command.Parameters.Add("@schedule", MySqlDbType.Int64).Value = scheduleId;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(8)) return null;
        var phone = reader.GetString(8);
        if (!GsmSmsTransport.IsAsciiMobile(phone)) return null;
        return new Candidate(reader.GetInt64(0), substituteId, reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetFieldValue<DateOnly>(5),
            TimeOnly.FromTimeSpan(reader.GetTimeSpan(6)), TimeOnly.FromTimeSpan(reader.GetTimeSpan(7)), reader.GetInt64(9), phone);
    }
}
