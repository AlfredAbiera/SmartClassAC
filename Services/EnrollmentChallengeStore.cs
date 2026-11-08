using System.Security.Cryptography;
using MySqlConnector;

namespace SmartClassAC.Services;

public sealed record EnrollmentChallengeIssueResult(byte[]? Challenge, string? Error);

/// <summary>
/// Issues and consumes short-lived authentication challenges. This does not authorize a sensor write.
/// </summary>
public sealed class EnrollmentChallengeStore
{
    private const int LifetimeSeconds = 120;
    private const int MaxOutstandingPerDevice = 16;
    private readonly string _connectionString;

    public EnrollmentChallengeStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before using enrollment challenges.");
    }

    public async Task<EnrollmentChallengeIssueResult> IssueAsync(string deviceId, string operation, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(deviceId, operation);
        var challenge = RandomNumberGenerator.GetBytes(EnrollmentMessageAuthentication.ChallengeSize);
        var challengeHash = SHA256.HashData(challenge);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = @"SELECT id FROM biometric_devices
            WHERE device_id = @deviceId AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        device.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId;
        var deviceRowId = await device.ExecuteScalarAsync(cancellationToken);
        if (deviceRowId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            CryptographicOperations.ZeroMemory(challenge);
            return new EnrollmentChallengeIssueResult(null, "Unknown, inactive, or unpaired device.");
        }
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = @"
                DELETE FROM fingerprint_enrollment_challenges
                WHERE device_id = @deviceId AND expires_utc <= UTC_TIMESTAMP(6);";
            cleanup.Parameters.Add("@deviceId", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRowId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM fingerprint_enrollment_challenges WHERE device_id = @deviceId;";
            count.Parameters.Add("@deviceId", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRowId);
            if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) >= MaxOutstandingPerDevice)
            {
                await transaction.RollbackAsync(cancellationToken);
                CryptographicOperations.ZeroMemory(challenge);
                return new EnrollmentChallengeIssueResult(null, "Too many outstanding challenges for this device.");
            }
        }
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = @"
            INSERT INTO fingerprint_enrollment_challenges (challenge_hash, device_id, operation_name, expires_utc)
            VALUES (@hash, @deviceId, @operation, DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @lifetime SECOND));";
        insert.Parameters.Add("@hash", MySqlDbType.Binary, 32).Value = challengeHash;
        insert.Parameters.Add("@lifetime", MySqlDbType.Int32).Value = LifetimeSeconds;
        insert.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operation;
        insert.Parameters.Add("@deviceId", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRowId);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EnrollmentChallengeIssueResult(challenge, null);
    }

    /// <summary>
    /// Validates the MAC before atomically consuming its challenge. The caller must supply
    /// the device's independently provisioned key and process only the authenticated body.
    /// </summary>
    public async Task<bool> VerifyAndConsumeAsync(
        string deviceId, string operation, byte[] key, byte[] challenge, byte[] body, byte[] tag,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(deviceId, operation);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(tag);
        if (challenge.Length != EnrollmentMessageAuthentication.ChallengeSize ||
            tag.Length != EnrollmentMessageAuthentication.TagSize ||
            body.Length > EnrollmentMessageAuthentication.MaxBodySize)
            return false;
        if (!EnrollmentMessageAuthentication.VerifyTag(key, false, deviceId, operation, challenge, body, tag))
            return false;

        var hash = SHA256.HashData(challenge);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ConsumeAsync(connection, null, deviceId, operation, hash, cancellationToken);
    }

    internal static async Task<bool> ConsumeAsync(
        MySqlConnection connection, MySqlTransaction? transaction, string deviceId, string operation,
        byte[] challengeHash, CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = @"
            DELETE c FROM fingerprint_enrollment_challenges c
            INNER JOIN biometric_devices d ON d.id = c.device_id
            WHERE c.challenge_hash = @hash AND d.device_id = @deviceId
              AND d.is_active = TRUE AND d.paired_at IS NOT NULL
              AND c.operation_name = @operation AND c.expires_utc > UTC_TIMESTAMP(6);";
        delete.Parameters.Add("@hash", MySqlDbType.Binary, 32).Value = challengeHash;
        delete.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId;
        delete.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operation;
        return await delete.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void ValidateIdentity(string deviceId, string operation)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId))
            throw new ArgumentException("The device ID must contain 1 to 23 ASCII digits.", nameof(deviceId));
        if (!EnrollmentMessageAuthentication.IsValidOperation(operation))
            throw new ArgumentException("The operation must contain lowercase ASCII letters, digits or underscores.", nameof(operation));
    }
}
