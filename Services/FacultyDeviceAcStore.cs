using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed record FacultyDeviceAcInfo(long ClassroomId, int Target, int Minimum, bool HasActiveClass);
internal enum FacultyDeviceTargetResult { Updated, Denied, NoActiveClass, BelowMinimum, StaleTarget, InvalidTarget }

internal sealed class FacultyDeviceAcStore(IConfiguration configuration)
{
    public async Task<FacultyDeviceAcInfo?> GetAsync(
        string deviceId, string sessionToken, byte[] secret, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before using the Faculty menu.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var teacherId = await FacultyOtpStore.LockSessionAsync(connection, transaction, deviceId, sessionToken, secret, cancellationToken);
        if (teacherId is null) return null;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.id, c.target_temperature, c.minimum_temperature,
                   EXISTS (SELECT 1 FROM attendance_logs a
                           WHERE a.teacher_account_id = @teacher AND a.classroom_id = c.id
                             AND a.status = 'Active' AND a.time_out_utc IS NULL)
            FROM biometric_devices d
            INNER JOIN classrooms c ON c.id = d.classroom_id AND c.is_active = TRUE
            WHERE d.device_id = @device AND d.is_active = TRUE AND d.paired_at IS NOT NULL;
            """;
        command.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId.Value;
        command.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        FacultyDeviceAcInfo? info;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            info = await reader.ReadAsync(cancellationToken)
                ? new FacultyDeviceAcInfo(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetBoolean(3))
                : null;
        await transaction.CommitAsync(cancellationToken);
        return info;
    }

    internal async Task<FacultyDeviceTargetResult> SetTargetAsync(
        string deviceId, string sessionToken, byte[] secret, long expectedRoom, int expectedTarget, int target,
        SchoolTimeOptions schoolTime, CancellationToken cancellationToken = default)
    {
        if (expectedRoom <= 0 || expectedTarget is < 16 or > 30 || target is < 16 or > 30 ||
            Math.Abs(target - expectedTarget) != 1)
            return FacultyDeviceTargetResult.InvalidTarget;

        var connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before using the Faculty menu.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var teacherId = await FacultyOtpStore.LockSessionAsync(connection, transaction, deviceId, sessionToken, secret, cancellationToken);
        if (teacherId is null) return FacultyDeviceTargetResult.Denied;

        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT classroom_id FROM biometric_devices WHERE device_id = @device FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var room = await device.ExecuteScalarAsync(cancellationToken);
        if (room is null or DBNull) return FacultyDeviceTargetResult.Denied;
        var roomId = Convert.ToInt64(room);
        if (roomId != expectedRoom) return FacultyDeviceTargetResult.Denied;

        await using var attendance = connection.CreateCommand();
        attendance.Transaction = transaction;
        attendance.CommandText = """
            SELECT id FROM attendance_logs
            WHERE teacher_account_id = @teacher AND classroom_id = @room
              AND status = 'Active' AND time_out_utc IS NULL LIMIT 1 FOR UPDATE;
            """;
        attendance.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId.Value;
        attendance.Parameters.Add("@room", MySqlDbType.Int64).Value = roomId;
        if (await attendance.ExecuteScalarAsync(cancellationToken) is null)
            return FacultyDeviceTargetResult.NoActiveClass;

        await using var classroom = connection.CreateCommand();
        classroom.Transaction = transaction;
        classroom.CommandText = "SELECT target_temperature, minimum_temperature FROM classrooms WHERE id = @room AND is_active = TRUE FOR UPDATE;";
        classroom.Parameters.Add("@room", MySqlDbType.Int64).Value = roomId;
        int current, minimum;
        await using (var reader = await classroom.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return FacultyDeviceTargetResult.Denied;
            current = reader.GetInt32(0);
            minimum = reader.GetInt32(1);
        }
        if (current != expectedTarget) return FacultyDeviceTargetResult.StaleTarget;
        if (target < minimum) return FacultyDeviceTargetResult.BelowMinimum;

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE classrooms SET target_temperature = @target WHERE id = @room;";
        update.Parameters.Add("@target", MySqlDbType.Int32).Value = target;
        update.Parameters.Add("@room", MySqlDbType.Int64).Value = roomId;
        await update.ExecuteNonQueryAsync(cancellationToken);

        await using var log = connection.CreateCommand();
        log.Transaction = transaction;
        log.CommandText = """
            INSERT INTO temperature_logs (classroom_id, actor_account_id, target_temperature, event_type, notes, recorded_utc)
            VALUES (@room, @teacher, @target, 'TargetSet', 'Scanner target requested; physical AC not verified.', @recorded);
            """;
        log.Parameters.Add("@room", MySqlDbType.Int64).Value = roomId;
        log.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId.Value;
        log.Parameters.Add("@target", MySqlDbType.Int32).Value = target;
        log.Parameters.Add("@recorded", MySqlDbType.DateTime).Value = SchoolClock.GetNow(schoolTime);
        await log.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return FacultyDeviceTargetResult.Updated;
    }
}
