using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed record FacultyDeviceAcInfo(long ClassroomId, int Target, int Minimum, bool HasActiveClass);
internal enum FacultyDeviceTargetResult { Updated, Denied, NoActiveClass, BelowMinimum, StaleTarget, InvalidTarget }
internal enum FacultyClassExtensionResult { Extended, Denied, NoActiveClass, AlreadyExtended, ScheduleConflict, InvalidWindow }

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
                           LEFT JOIN class_schedules s ON s.id = a.class_schedule_id
                           WHERE a.teacher_account_id = @teacher AND a.classroom_id = c.id
                             AND a.status = 'Active' AND a.time_out_utc IS NULL
                             AND (a.class_schedule_id IS NULL OR
                                  (s.id IS NOT NULL AND (COALESCE(s.original_teacher_account_id, s.teacher_account_id) = s.teacher_account_id OR s.substitution_verified = TRUE))))
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

    internal sealed class FacultyDeviceClassExtensionStore(IConfiguration configuration)
    {
        public async Task<FacultyClassExtensionResult> ExtendAsync(
            string deviceId, string sessionToken, byte[] secret, DateOnly schoolDate, TimeOnly schoolTime,
            CancellationToken cancellationToken = default)
        {
            var connectionString = configuration.GetConnectionString("SmartClassMariaDb")
                ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before using the Faculty menu.");
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var teacherId = await FacultyOtpStore.LockSessionAsync(
                    connection, transaction, deviceId, sessionToken, secret, cancellationToken);
                if (teacherId is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return FacultyClassExtensionResult.Denied;
                }

                await using var attendance = connection.CreateCommand();
                attendance.Transaction = transaction;
                attendance.CommandText = """
                    SELECT a.id, a.class_schedule_id, a.classroom_id, a.class_extension_minutes,
                           schedule.end_time, schedule.schedule_date
                    FROM attendance_logs a
                    INNER JOIN class_schedules schedule ON schedule.id = a.class_schedule_id
                    INNER JOIN biometric_devices device ON device.classroom_id = a.classroom_id
                        AND device.device_id = @device AND device.is_active = TRUE AND device.paired_at IS NOT NULL
                    WHERE a.teacher_account_id = @teacher AND a.status = 'Active' AND a.time_out_utc IS NULL
                      AND schedule.teacher_account_id = a.teacher_account_id
                      AND (COALESCE(schedule.original_teacher_account_id, schedule.teacher_account_id) = schedule.teacher_account_id OR schedule.substitution_verified = TRUE)
                      AND schedule.classroom_id = a.classroom_id
                      AND schedule.schedule_date = @date
                      AND @time >= schedule.start_time AND @time < schedule.end_time
                    LIMIT 1
                    FOR UPDATE;
                    """;
                attendance.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
                attendance.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId.Value;
                attendance.Parameters.Add("@date", MySqlDbType.Date).Value = schoolDate;
                attendance.Parameters.Add("@time", MySqlDbType.Time).Value = schoolTime.ToTimeSpan();

                long attendanceId, scheduleId, classroomId;
                int extensionMinutes;
                TimeOnly scheduleEnd;
                await using (var reader = await attendance.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return FacultyClassExtensionResult.NoActiveClass;
                    }

                    attendanceId = reader.GetInt64(0);
                    scheduleId = reader.GetInt64(1);
                    classroomId = reader.GetInt64(2);
                    extensionMinutes = reader.GetInt32(3);
                    scheduleEnd = TimeOnly.FromTimeSpan(reader.GetTimeSpan(4));
                }

                if (extensionMinutes != 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return FacultyClassExtensionResult.AlreadyExtended;
                }

                if (scheduleEnd > TimeOnly.MaxValue.AddMinutes(-30))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return FacultyClassExtensionResult.InvalidWindow;
                }

                var extendedEnd = scheduleEnd.AddMinutes(30);
                await using (var conflict = connection.CreateCommand())
                {
                    conflict.Transaction = transaction;
                    conflict.CommandText = """
                        SELECT id
                        FROM class_schedules
                        WHERE id <> @schedule AND schedule_date = @date
                          AND (teacher_account_id = @teacher OR classroom_id = @classroom)
                          AND start_time < @extendedEnd AND end_time > @scheduleEnd
                        LIMIT 1
                        FOR UPDATE;
                        """;
                    conflict.Parameters.Add("@schedule", MySqlDbType.Int64).Value = scheduleId;
                    conflict.Parameters.Add("@date", MySqlDbType.Date).Value = schoolDate;
                    conflict.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId.Value;
                    conflict.Parameters.Add("@classroom", MySqlDbType.Int64).Value = classroomId;
                    conflict.Parameters.Add("@extendedEnd", MySqlDbType.Time).Value = extendedEnd.ToTimeSpan();
                    conflict.Parameters.Add("@scheduleEnd", MySqlDbType.Time).Value = scheduleEnd.ToTimeSpan();
                    if (await conflict.ExecuteScalarAsync(cancellationToken) is not null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return FacultyClassExtensionResult.ScheduleConflict;
                    }
                }

                await using var extend = connection.CreateCommand();
                extend.Transaction = transaction;
                extend.CommandText = """
                    UPDATE attendance_logs
                    SET class_extension_minutes = 30
                    WHERE id = @attendance AND status = 'Active' AND time_out_utc IS NULL
                      AND class_extension_minutes = 0;
                    """;
                extend.Parameters.Add("@attendance", MySqlDbType.Int64).Value = attendanceId;
                if (await extend.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return FacultyClassExtensionResult.AlreadyExtended;
                }

                await transaction.CommitAsync(cancellationToken);
                return FacultyClassExtensionResult.Extended;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
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
            SELECT a.id FROM attendance_logs a
            LEFT JOIN class_schedules s ON s.id = a.class_schedule_id
            WHERE a.teacher_account_id = @teacher AND a.classroom_id = @room
              AND a.status = 'Active' AND a.time_out_utc IS NULL
              AND (a.class_schedule_id IS NULL OR
                   (s.id IS NOT NULL AND (COALESCE(s.original_teacher_account_id, s.teacher_account_id) = s.teacher_account_id OR s.substitution_verified = TRUE)))
            LIMIT 1 FOR UPDATE;
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
