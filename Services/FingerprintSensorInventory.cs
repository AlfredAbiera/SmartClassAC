using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed record FingerprintInventoryRequest(string? DeviceId, int Capacity, string? Bitmap, string? Challenge, string? Tag);

internal sealed class FingerprintSensorInventory
{
    internal const int MaximumCapacity = 2048;
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;

    public FingerprintSensorInventory(IConfiguration configuration)
    {
        _configuration = configuration;
        _connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before recording sensor inventory.");
    }

    internal async Task<bool> RecordAsync(FingerprintInventoryRequest request, CancellationToken cancellationToken = default)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            request.Capacity is < 2 or > MaximumCapacity ||
            !TryHex(request.Bitmap, (request.Capacity + 7) / 8, out var bitmap) ||
            !TryHex(request.Challenge, EnrollmentMessageAuthentication.ChallengeSize, out var challenge) ||
            !TryHex(request.Tag, EnrollmentMessageAuthentication.TagSize, out var tag))
            return false;

        var configured = _configuration["EnrollmentSecurity:MasterKey"];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Set EnrollmentSecurity:MasterKey before recording sensor inventory.");
        byte[] master;
        try { master = Convert.FromBase64String(configured); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("EnrollmentSecurity:MasterKey must be a Base64-encoded 32-byte key.", exception);
        }
        try
        {
            if (master.Length != EnrollmentMessageAuthentication.KeySize)
                throw new InvalidOperationException("EnrollmentSecurity:MasterKey must decode to 32 bytes.");
            var key = await new EnrollmentDeviceCredentialStore(_configuration).LoadAsync(request.DeviceId!, master, cancellationToken);
            if (key is null) return false;
            try
            {
                var body = Encoding.ASCII.GetBytes(FormattableString.Invariant($"{request.Capacity}\n{request.Bitmap}\n"));
                if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, "inventory", challenge, body, tag))
                    return false;
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { CryptographicOperations.ZeroMemory(master); }

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = @"SELECT d.id FROM biometric_devices d
            WHERE d.device_id = @device AND d.is_active = TRUE AND d.paired_at IS NOT NULL
              AND d.pending_clear_fingerprints = FALSE
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_preparations p
                  INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id
                  WHERE p.device_id = d.id)
            FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = request.DeviceId!;
        var deviceRow = await device.ExecuteScalarAsync(cancellationToken);
        if (deviceRow is null) return false;

        await using var challengeCommand = connection.CreateCommand();
        challengeCommand.Transaction = transaction;
        challengeCommand.CommandText = @"SELECT DATE_SUB(expires_utc, INTERVAL 120 SECOND)
            FROM fingerprint_enrollment_challenges
            WHERE challenge_hash = @hash AND device_id = @device AND operation_name = 'inventory'
              AND expires_utc > UTC_TIMESTAMP(6) FOR UPDATE;";
        challengeCommand.Parameters.Add("@hash", MySqlDbType.Binary, 32).Value = SHA256.HashData(challenge);
        challengeCommand.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
        var issued = await challengeCommand.ExecuteScalarAsync(cancellationToken);
        if (issued is not DateTime observedAfter) return false;

        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT observed_after_utc FROM fingerprint_sensor_inventory WHERE device_id = @device FOR UPDATE;";
        existing.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
        if (await existing.ExecuteScalarAsync(cancellationToken) is DateTime previous && previous >= observedAfter)
            return false;

        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, request.DeviceId!, "inventory", SHA256.HashData(challenge), cancellationToken))
            return false;

        await using var save = connection.CreateCommand();
        save.Transaction = transaction;
        save.CommandText = @"INSERT INTO fingerprint_sensor_inventory
                (device_id, capacity, bitmap, observed_after_utc, received_utc)
            VALUES (@device, @capacity, @bitmap, @observed, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE capacity = VALUES(capacity), bitmap = VALUES(bitmap),
                observed_after_utc = VALUES(observed_after_utc), received_utc = VALUES(received_utc);";
        save.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
        save.Parameters.Add("@capacity", MySqlDbType.Int32).Value = request.Capacity;
        save.Parameters.Add("@bitmap", MySqlDbType.VarBinary).Value = bitmap;
        save.Parameters.Add("@observed", MySqlDbType.DateTime).Value = observedAfter;
        await save.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal static async Task InvalidateAsync(MySqlConnection connection, MySqlTransaction transaction, long deviceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"DELETE FROM fingerprint_sensor_inventory WHERE device_id = @device;
            DELETE FROM fingerprint_enrollment_challenges WHERE device_id = @device AND operation_name = 'inventory';";
        command.Parameters.Add("@device", MySqlDbType.Int64).Value = deviceId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool TryHex(string? value, int length, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value is null || value.Length != length * 2 ||
            value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return false;
        bytes = Convert.FromHexString(value);
        return true;
    }
}
