using System.Security.Cryptography;
using MySqlConnector;

namespace SmartClassAC.Services;

internal enum AcIrStatus { Unavailable, Reconcile, Ready, InFlight }
internal sealed record AcIrSnapshot(AcIrStatus Status, long ClassroomId, int Target, int? Assumed, byte[] BootId,
    bool? AssumedPower, bool DesiredPower, int ProtocolVersion);
internal sealed record AcIrCommand(byte[] Id, int From, int To, bool IsPower = false);

/// <summary>Durable bookkeeping for relative IR; callers must authenticate scanner requests before invoking it.</summary>
internal sealed class AcIrStateStore(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("SmartClassMariaDb")
        ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before using AC IR.");

    internal async Task<bool> RegisterBootAsync(string deviceId, byte[] bootId, CancellationToken cancellationToken = default,
        bool powerEnabled = false)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || bootId is not { Length: 32 }) return false;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var device = await LockDeviceAsync(connection, transaction, deviceId, cancellationToken);
        if (device is null) return false;

        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT boot_id FROM ac_ir_state WHERE device_id = @id AND protocol_version = @version FOR UPDATE;";
        existing.Parameters.Add("@id", MySqlDbType.Int64).Value = device.Value.Id;
        existing.Parameters.Add("@version", MySqlDbType.Int32).Value = powerEnabled ? 2 : 1;
        var current = await existing.ExecuteScalarAsync(cancellationToken) as byte[];
        if (current is not null && CryptographicOperations.FixedTimeEquals(current, bootId))
        {
            if (await LockStateAsync(connection, transaction, device.Value, bootId, cancellationToken) is null)
                return false;
            await MarkSeenAsync(connection, transaction, device.Value.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        await using var seen = connection.CreateCommand();
        seen.Transaction = transaction;
        seen.CommandText = "SELECT COUNT(*) FROM ac_ir_boots WHERE device_id = @id AND boot_id = @boot;";
        seen.Parameters.Add("@id", MySqlDbType.Int64).Value = device.Value.Id;
        seen.Parameters.Add("@boot", MySqlDbType.Binary, 32).Value = bootId;
        if (Convert.ToInt64(await seen.ExecuteScalarAsync(cancellationToken)) != 0) return false;

        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = """
            INSERT INTO ac_ir_boots (device_id, boot_id) VALUES (@id, @boot);
            INSERT INTO ac_ir_state (device_id, classroom_id, paired_at, boot_id, state, protocol_version)
            VALUES (@id, @room, @paired, @boot, 'reconcile', @version)
            ON DUPLICATE KEY UPDATE classroom_id = VALUES(classroom_id), paired_at = VALUES(paired_at),
                boot_id = VALUES(boot_id), state = 'reconcile', assumed_temperature = NULL,
                command_id = NULL, command_temperature = NULL, assumed_power = NULL, command_power = NULL,
                protocol_version = VALUES(protocol_version), last_seen_utc = UTC_TIMESTAMP(6);
            """;
        write.Parameters.Add("@id", MySqlDbType.Int64).Value = device.Value.Id;
        write.Parameters.Add("@room", MySqlDbType.Int64).Value = device.Value.Room;
        write.Parameters.Add("@paired", MySqlDbType.DateTime).Value = device.Value.Paired;
        write.Parameters.Add("@boot", MySqlDbType.Binary, 32).Value = bootId;
        write.Parameters.Add("@version", MySqlDbType.Int32).Value = powerEnabled ? 2 : 1;
        await write.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal async Task<AcIrSnapshot?> GetAsync(string deviceId, byte[] bootId, CancellationToken cancellationToken = default)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || bootId is not { Length: 32 }) return null;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var device = await LockDeviceAsync(connection, transaction, deviceId, cancellationToken);
        if (device is null) return null;
        var snapshot = await LockStateAsync(connection, transaction, device.Value, bootId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    internal async Task<bool> ReconcileAsync(long adminId, string deviceId, byte[] bootId, long roomId, int target,
        CancellationToken cancellationToken = default, bool? assumedPower = null)
    {
        if (adminId <= 0 || !EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) ||
            bootId is not { Length: 32 } || roomId <= 0 || target is < 16 or > 30) return false;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var device = await LockDeviceAsync(connection, transaction, deviceId, cancellationToken);
        if (device is null || device.Value.Room != roomId) return false;
        await using var admin = connection.CreateCommand();
        admin.Transaction = transaction;
        admin.CommandText = "SELECT id FROM user_accounts WHERE id = @id AND role = 'Admin' AND is_active = TRUE FOR UPDATE;";
        admin.Parameters.Add("@id", MySqlDbType.Int64).Value = adminId;
        if (await admin.ExecuteScalarAsync(cancellationToken) is null) return false;
        var snapshot = await LockStateAsync(connection, transaction, device.Value, bootId, cancellationToken);
        if (snapshot is null || snapshot.Status != AcIrStatus.Reconcile || snapshot.Target != target ||
            snapshot.ProtocolVersion != (assumedPower.HasValue ? 2 : 1)) return false;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE ac_ir_state SET state = 'ready', assumed_temperature = @target, assumed_power = @power WHERE device_id = @id;
            INSERT INTO ac_ir_reconciliations (device_id, classroom_id, admin_account_id, boot_id, target_temperature, assumed_power)
            VALUES (@id, @room, @admin, @boot, @target, @power);
            """;
        update.Parameters.Add("@target", MySqlDbType.Int32).Value = target;
        update.Parameters.Add("@id", MySqlDbType.Int64).Value = device.Value.Id;
        update.Parameters.Add("@room", MySqlDbType.Int64).Value = roomId;
        update.Parameters.Add("@admin", MySqlDbType.Int64).Value = adminId;
        update.Parameters.Add("@boot", MySqlDbType.Binary, 32).Value = bootId;
        update.Parameters.Add("@power", MySqlDbType.Bool).Value = (object?)assumedPower ?? DBNull.Value;
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal async Task<AcIrCommand?> ClaimAsync(string deviceId, byte[] bootId, CancellationToken cancellationToken = default,
        bool powerEnabled = false)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || bootId is not { Length: 32 }) return null;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var device = await LockDeviceAsync(connection, transaction, deviceId, cancellationToken);
        if (device is null) return null;
        var snapshot = await LockStateAsync(connection, transaction, device.Value, bootId, cancellationToken);
        if (snapshot is null || snapshot.ProtocolVersion != (powerEnabled ? 2 : 1)) return null;
        await MarkSeenAsync(connection, transaction, device.Value.Id, cancellationToken);
        if (snapshot.Status != AcIrStatus.Ready || snapshot.Assumed is not int assumed ||
            (powerEnabled && snapshot.AssumedPower is null))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var powerCommand = powerEnabled && snapshot.AssumedPower != snapshot.DesiredPower;
        if (!powerCommand && (assumed == snapshot.Target || (powerEnabled && !snapshot.DesiredPower)))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (powerCommand)
        {
            var powerId = RandomNumberGenerator.GetBytes(16);
            await using var power = connection.CreateCommand();
            power.Transaction = transaction;
            power.CommandText = "UPDATE ac_ir_state SET state = 'inflight', command_id = @command, command_power = @next WHERE device_id = @id;";
            power.Parameters.Add("@id", MySqlDbType.Int64).Value = device.Value.Id;
            power.Parameters.Add("@command", MySqlDbType.Binary, 16).Value = powerId;
            power.Parameters.Add("@next", MySqlDbType.Bool).Value = snapshot.DesiredPower;
            await power.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AcIrCommand(powerId, snapshot.AssumedPower!.Value ? 1 : 0, snapshot.DesiredPower ? 1 : 0, true);
        }
        var next = assumed + Math.Sign(snapshot.Target - assumed);
        var id = RandomNumberGenerator.GetBytes(16);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE ac_ir_state SET state = 'inflight', command_id = @command, command_temperature = @next WHERE device_id = @id;";
        update.Parameters.Add("@id", MySqlDbType.Int64).Value = device.Value.Id;
        update.Parameters.Add("@command", MySqlDbType.Binary, 16).Value = id;
        update.Parameters.Add("@next", MySqlDbType.Int32).Value = next;
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AcIrCommand(id, assumed, next);
    }

    internal async Task<bool> CompleteAsync(string deviceId, byte[] bootId, byte[] commandId, CancellationToken cancellationToken = default,
        bool powerEnabled = false)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || bootId is not { Length: 32 } ||
            commandId is not { Length: 16 }) return false;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var device = await LockDeviceAsync(connection, transaction, deviceId, cancellationToken);
        if (device is null) return false;
        var snapshot = await LockStateAsync(connection, transaction, device.Value, bootId, cancellationToken);
        if (snapshot is null || snapshot.ProtocolVersion != (powerEnabled ? 2 : 1) || snapshot.Status != AcIrStatus.InFlight) return false;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE ac_ir_state SET state = 'ready',
                assumed_temperature = COALESCE(command_temperature, assumed_temperature),
                assumed_power = COALESCE(command_power, assumed_power),
                command_id = NULL, command_temperature = NULL, command_power = NULL
            WHERE device_id = @id AND command_id = @command
              AND ((command_temperature IS NOT NULL AND command_power IS NULL)
                   OR (@powerEnabled AND command_power IS NOT NULL AND command_temperature IS NULL));
            """;
        update.Parameters.Add("@id", MySqlDbType.Int64).Value = device.Value.Id;
        update.Parameters.Add("@command", MySqlDbType.Binary, 16).Value = commandId;
        update.Parameters.Add("@powerEnabled", MySqlDbType.Bool).Value = powerEnabled;
        var changed = await update.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private readonly record struct Device(long Id, long Room, DateTime Paired);

    private static async Task MarkSeenAsync(MySqlConnection connection, MySqlTransaction transaction, long deviceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE ac_ir_state SET last_seen_utc = UTC_TIMESTAMP(6) WHERE device_id = @id;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = deviceId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Device?> LockDeviceAsync(MySqlConnection connection, MySqlTransaction transaction,
        string deviceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT d.id, d.classroom_id, d.paired_at FROM biometric_devices d
            WHERE d.device_id = @device AND d.is_active = TRUE AND d.paired_at IS NOT NULL AND d.classroom_id IS NOT NULL
            FOR UPDATE;
            """;
        command.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new Device(reader.GetInt64(0), reader.GetInt64(1), reader.GetDateTime(2)) : null;
    }

    private static async Task<AcIrSnapshot?> LockStateAsync(MySqlConnection connection, MySqlTransaction transaction,
        Device device, byte[] bootId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.state, c.target_temperature, s.assumed_temperature, s.boot_id, s.assumed_power,
                   c.ir_attendance_power, s.protocol_version
            FROM ac_ir_state s INNER JOIN classrooms c ON c.id = s.classroom_id AND c.is_active = TRUE
            WHERE s.device_id = @id AND s.classroom_id = @room AND s.paired_at = @paired AND s.boot_id = @boot
            FOR UPDATE;
            """;
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = device.Id;
        command.Parameters.Add("@room", MySqlDbType.Int64).Value = device.Room;
        command.Parameters.Add("@paired", MySqlDbType.DateTime).Value = device.Paired;
        command.Parameters.Add("@boot", MySqlDbType.Binary, 32).Value = bootId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var state = reader.GetString(0) switch
        {
            "reconcile" => AcIrStatus.Reconcile,
            "ready" => AcIrStatus.Ready,
            "inflight" => AcIrStatus.InFlight,
            _ => throw new InvalidOperationException("Invalid AC IR state.")
        };
        return new AcIrSnapshot(state, device.Room, reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetFieldValue<byte[]>(3),
            reader.IsDBNull(4) ? null : reader.GetBoolean(4), reader.GetBoolean(5), reader.GetInt32(6));
    }
}
