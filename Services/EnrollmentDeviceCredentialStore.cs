using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace SmartClassAC.Services;

/// <summary>
/// Stores externally provisioned keys. No network or portal provisioning endpoint is enabled.
/// </summary>
internal sealed class EnrollmentDeviceCredentialStore
{
    private readonly string _connectionString;

    public EnrollmentDeviceCredentialStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before provisioning enrollment keys.");
    }

    public async Task<OperationResult> StoreNewAsync(
        string deviceId, byte[] deviceKey, byte[] masterKey, CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(deviceId);
        ArgumentNullException.ThrowIfNull(deviceKey);
        ArgumentNullException.ThrowIfNull(masterKey);
        using var protector = new EnrollmentDeviceKeyProtector(masterKey);
        var encrypted = protector.Protect(deviceId, deviceKey);
        var domain = Encoding.ASCII.GetBytes("SmartClassAC:enrollment-key-id:v1:");
        var fingerprintInput = new byte[domain.Length + deviceKey.Length];
        domain.CopyTo(fingerprintInput, 0);
        deviceKey.CopyTo(fingerprintInput, domain.Length);
        var fingerprint = HMACSHA256.HashData(masterKey, fingerprintInput);
        CryptographicOperations.ZeroMemory(fingerprintInput);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = @"SELECT id FROM biometric_devices
            WHERE device_id = @device AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var deviceRow = await device.ExecuteScalarAsync(cancellationToken);
        if (deviceRow is null)
            return OperationResult.Failure("The scanner is unknown, inactive, or unpaired.");

        await VerifyMasterKeyAsync(connection, transaction, masterKey, cancellationToken);

        await using var guard = connection.CreateCommand();
        guard.Transaction = transaction;
        guard.CommandText = @"SELECT g.preparation_id
            FROM fingerprint_enrollment_write_guards g
            INNER JOIN fingerprint_enrollment_preparations p ON p.id = g.preparation_id
            WHERE p.device_id = @device LIMIT 1 FOR UPDATE;";
        guard.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
        if (await guard.ExecuteScalarAsync(cancellationToken) is not null)
            return OperationResult.Failure("The scanner has an unresolved fingerprint write.");

        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO fingerprint_enrollment_device_credentials (device_id, protected_key, key_fingerprint)
                                   VALUES (@device, @key, @fingerprint);";
            insert.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
            insert.Parameters.Add("@key", MySqlDbType.VarBinary, 61).Value = encrypted;
            insert.Parameters.Add("@fingerprint", MySqlDbType.Binary, 32).Value = fingerprint;
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("The scanner already has a key or this key is already in use; replacement is not yet supported.");
        }
    }

    public async Task<byte[]?> LoadAsync(string deviceId, byte[] masterKey, CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(deviceId);
        ArgumentNullException.ThrowIfNull(masterKey);
        using var protector = new EnrollmentDeviceKeyProtector(masterKey);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await VerifyMasterKeyAsync(connection, transaction, masterKey, cancellationToken, initialize: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT c.protected_key
            FROM fingerprint_enrollment_device_credentials c
            INNER JOIN biometric_devices d ON d.id = c.device_id
            WHERE d.device_id = @device AND d.is_active = TRUE AND d.paired_at IS NOT NULL;";
        command.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var key = result is null ? null : protector.Unprotect(deviceId, (byte[])result);
        try
        {
            await transaction.CommitAsync(cancellationToken);
            return key;
        }
        catch
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private static async Task VerifyMasterKeyAsync(MySqlConnection connection, MySqlTransaction transaction, byte[] masterKey, CancellationToken cancellationToken, bool initialize = true)
    {
        var expected = HMACSHA256.HashData(masterKey, Encoding.ASCII.GetBytes("SmartClassAC:enrollment-master-key-check:v1"));
        if (initialize)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO fingerprint_enrollment_master_key_check (singleton_id, key_check)
                VALUES (1, @check) ON DUPLICATE KEY UPDATE singleton_id = singleton_id;";
            insert.Parameters.Add("@check", MySqlDbType.Binary, 32).Value = expected;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT key_check FROM fingerprint_enrollment_master_key_check WHERE singleton_id = 1 FOR UPDATE;";
        var stored = await command.ExecuteScalarAsync(cancellationToken);
        if (stored is null && !initialize) return;
        if (stored is not byte[] check || !CryptographicOperations.FixedTimeEquals(expected, check))
            throw new CryptographicException("The enrollment key protection key does not match the configured database.");
    }

    private static void ValidateDeviceId(string deviceId)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId))
            throw new ArgumentException("Invalid device ID.", nameof(deviceId));
    }
}
