using MySqlConnector;

namespace SmartClassAC.Services;

public sealed record AcIrAdminSnapshot(string DeviceId, long ClassroomId, int Target, int? Assumed,
    string State, string Boot, bool RecentlySeen);
public sealed record AcIrTeacherSnapshot(long ClassroomId, int Target, int? Assumed, string? State, bool RecentlySeen);

public sealed class AcIrAdministration(IConfiguration configuration)
{
    public async Task<IReadOnlyList<AcIrAdminSnapshot>> GetAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before using AC IR.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.device_id, c.id, c.target_temperature, s.assumed_temperature, s.state, s.boot_id,
                   s.last_seen_utc >= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 2 MINUTE)
            FROM ac_ir_state s
            INNER JOIN biometric_devices d ON d.id = s.device_id AND d.is_active = TRUE
                AND d.classroom_id = s.classroom_id AND d.paired_at = s.paired_at
            INNER JOIN classrooms c ON c.id = s.classroom_id AND c.is_active = TRUE;
            """;
        var results = new List<AcIrAdminSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new AcIrAdminSnapshot(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetString(4),
                Convert.ToHexString(reader.GetFieldValue<byte[]>(5)).ToLowerInvariant(), reader.GetBoolean(6)));
        return results;
    }

    public Task<bool> ReconcileAsync(long adminId, AcIrAdminSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (!FacultyOtpProtocol.TryHex(snapshot.Boot, out var boot)) return Task.FromResult(false);
        return new AcIrStateStore(configuration).ReconcileAsync(
            adminId, snapshot.DeviceId, boot, snapshot.ClassroomId, snapshot.Target, cancellationToken);
    }

    public async Task<AcIrTeacherSnapshot?> GetForTeacherAsync(long teacherId, CancellationToken cancellationToken = default)
    {
        if (teacherId <= 0) return null;
        var connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before using AC IR.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.target_temperature, s.assumed_temperature, s.state,
                   s.last_seen_utc >= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 2 MINUTE)
            FROM user_accounts u
            INNER JOIN attendance_logs a ON a.teacher_account_id = u.id AND a.status = 'Active' AND a.time_out_utc IS NULL
            INNER JOIN classrooms c ON c.id = a.classroom_id AND c.is_active = TRUE
            LEFT JOIN biometric_devices d ON d.classroom_id = c.id AND d.is_active = TRUE AND d.paired_at IS NOT NULL
            LEFT JOIN ac_ir_state s ON s.device_id = d.id AND s.classroom_id = c.id AND s.paired_at = d.paired_at
            WHERE u.id = @teacher AND u.role = 'Teacher' AND u.is_active = TRUE LIMIT 1;
            """;
        command.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AcIrTeacherSnapshot(reader.GetInt64(0), reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                !reader.IsDBNull(4) && reader.GetBoolean(4))
            : null;
    }
}
