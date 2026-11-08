using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed class FacultyOtpReservation(Guid requestId, string code)
{
    public Guid RequestId { get; } = requestId;
    public string Code { get; } = code;
}

/// <summary>Persists the OTP lifecycle. Callers must authenticate the scanner before invoking it.</summary>
internal sealed class FacultyOtpStore
{
    private const int WindowSeconds = 300;
    private const int MaxDeviceRequests = 10;
    private const int MaxPhoneRequests = 3;
    private const int LifetimeSeconds = 300;
    private const int SessionLifetimeSeconds = 300;
    private const int MaxAttempts = 5;
    private readonly string _connectionString;

    public FacultyOtpStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before using Faculty OTP.");
    }

    public async Task<FacultyOtpReservation?> ReserveAsync(
        string deviceId, string phone, byte[] secret, CancellationToken cancellationToken = default)
    {
        ValidateSecret(secret);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || !ValidPhone(phone)) return null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ReserveOnceAsync(deviceId, phone, secret, cancellationToken);
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.LockDeadlock && attempt < 4)
            {
                await Task.Delay(RandomNumberGenerator.GetInt32(10, 60), cancellationToken);
            }
        }
    }

    private async Task<FacultyOtpReservation?> ReserveOnceAsync(
        string deviceId, string phone, byte[] secret, CancellationToken cancellationToken)
    {
        var phoneKey = PhoneKey(secret, phone);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var device = await LockDeviceAsync(connection, transaction, deviceId, cancellationToken);
        if (device is null) return null;
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                DELETE FROM faculty_otp_phone_limits
                WHERE window_start_utc < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 DAY)
                ORDER BY window_start_utc LIMIT 100;
                """;
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var invalidate = connection.CreateCommand())
        {
            invalidate.Transaction = transaction;
            invalidate.CommandText = """
                DELETE FROM faculty_otp_requests WHERE device_id = @device;
                DELETE FROM faculty_device_sessions WHERE device_id = @device;
                """;
            invalidate.Parameters.Add("@device", MySqlDbType.Int64).Value = device.Value;
            await invalidate.ExecuteNonQueryAsync(cancellationToken);
        }

        var deviceAllowed = await IncrementLimitAsync(connection, transaction, true, device.Value, phoneKey, cancellationToken);
        if (!deviceAllowed)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var phoneAllowed = await IncrementLimitAsync(connection, transaction, false, device.Value, phoneKey, cancellationToken);
        if (!phoneAllowed)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await using var teacher = connection.CreateCommand();
        teacher.Transaction = transaction;
        teacher.CommandText = "SELECT id FROM user_accounts WHERE phone = @phone AND role = 'Teacher' AND is_active = TRUE FOR UPDATE;";
        teacher.Parameters.Add("@phone", MySqlDbType.VarChar).Value = phone;
        var teacherId = await teacher.ExecuteScalarAsync(cancellationToken);
        if (teacherId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var requestId = Guid.NewGuid();
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var codeTag = CodeTag(secret, requestId, code);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO faculty_otp_requests (device_id, request_id, teacher_id, phone_key, code_tag, expires_utc)
            VALUES (@device, @request, @teacher, @phoneKey, @codeTag, DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @lifetime SECOND))
            ON DUPLICATE KEY UPDATE request_id = VALUES(request_id), teacher_id = VALUES(teacher_id),
                phone_key = VALUES(phone_key), code_tag = VALUES(code_tag), expires_utc = VALUES(expires_utc),
                failed_attempts = 0, delivery_accepted = 0;
            """;
        insert.Parameters.Add("@device", MySqlDbType.Int64).Value = device.Value;
        insert.Parameters.Add("@request", MySqlDbType.Binary, 16).Value = requestId.ToByteArray();
        insert.Parameters.Add("@teacher", MySqlDbType.Int64).Value = Convert.ToInt64(teacherId);
        insert.Parameters.Add("@phoneKey", MySqlDbType.Binary, 32).Value = phoneKey;
        insert.Parameters.Add("@codeTag", MySqlDbType.Binary, 32).Value = codeTag;
        insert.Parameters.Add("@lifetime", MySqlDbType.Int32).Value = LifetimeSeconds;
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FacultyOtpReservation(requestId, code);
    }

    public async Task<bool> CompleteDeliveryAsync(
        string deviceId, Guid requestId, bool accepted, CancellationToken cancellationToken = default)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || requestId == Guid.Empty) return false;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var device = await LockDeviceAsync(connection, transaction, deviceId, cancellationToken);
        if (device is null) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = accepted
            ? "UPDATE faculty_otp_requests SET delivery_accepted = 1 WHERE device_id = @device AND request_id = @request AND delivery_accepted = 0 AND expires_utc > UTC_TIMESTAMP(6);"
            : "DELETE FROM faculty_otp_requests WHERE device_id = @device AND request_id = @request AND delivery_accepted = 0;";
        command.Parameters.Add("@device", MySqlDbType.Int64).Value = device.Value;
        command.Parameters.Add("@request", MySqlDbType.Binary, 16).Value = requestId.ToByteArray();
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<bool> VerifyAsync(
        string deviceId, string code, byte[] secret, CancellationToken cancellationToken = default)
        => await VerifyCoreAsync(deviceId, code, secret, false, cancellationToken) is not null;

    public Task<string?> VerifyAndCreateSessionAsync(
        string deviceId, string code, byte[] secret, CancellationToken cancellationToken = default)
        => VerifyCoreAsync(deviceId, code, secret, true, cancellationToken);

    private async Task<string?> VerifyCoreAsync(
        string deviceId, string code, byte[] secret, bool createSession, CancellationToken cancellationToken)
    {
        ValidateSecret(secret);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) ||
            code is not { Length: 6 } || !code.All(char.IsAsciiDigit)) return null;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var device = await LockDeviceAsync(connection, transaction, deviceId, cancellationToken);
        if (device is null) return null;
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT o.request_id, o.phone_key, o.code_tag, u.phone, u.role, u.is_active, u.id
            FROM faculty_otp_requests o JOIN user_accounts u ON u.id = o.teacher_id
            WHERE o.device_id = @device AND o.delivery_accepted = 1
                AND o.expires_utc > UTC_TIMESTAMP(6) AND o.failed_attempts < @maxAttempts FOR UPDATE;
            """;
        select.Parameters.Add("@device", MySqlDbType.Int64).Value = device.Value;
        select.Parameters.Add("@maxAttempts", MySqlDbType.Int32).Value = MaxAttempts;
        byte[] requestId, phoneKey, codeTag;
        string? phone, role;
        bool active;
        long teacherId;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            requestId = reader.GetFieldValue<byte[]>(0);
            phoneKey = reader.GetFieldValue<byte[]>(1);
            codeTag = reader.GetFieldValue<byte[]>(2);
            phone = reader.IsDBNull(3) ? null : reader.GetString(3);
            role = reader.GetString(4);
            active = reader.GetBoolean(5);
            teacherId = reader.GetInt64(6);
        }
        var eligible = active && role == "Teacher" && ValidPhone(phone) &&
            CryptographicOperations.FixedTimeEquals(phoneKey, PhoneKey(secret, phone!));
        var matches = CryptographicOperations.FixedTimeEquals(codeTag, CodeTag(secret, new Guid(requestId), code));
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = eligible && !matches
            ? "UPDATE faculty_otp_requests SET failed_attempts = failed_attempts + 1 WHERE device_id = @device;"
            : "DELETE FROM faculty_otp_requests WHERE device_id = @device;";
        update.Parameters.Add("@device", MySqlDbType.Int64).Value = device.Value;
        await update.ExecuteNonQueryAsync(cancellationToken);
        string? token = null;
        if (eligible && matches)
        {
            if (createSession)
            {
                var tokenBytes = RandomNumberGenerator.GetBytes(32);
                try
                {
                    token = Convert.ToHexString(tokenBytes).ToLowerInvariant();
                    await using var session = connection.CreateCommand();
                    session.Transaction = transaction;
                    session.CommandText = """
                        INSERT INTO faculty_device_sessions (device_id, teacher_id, token_hash, phone_key, paired_at, expires_utc)
                        SELECT id, @teacher, @hash, @phone, paired_at, DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @lifetime SECOND)
                        FROM biometric_devices WHERE id = @device AND is_active = TRUE AND paired_at IS NOT NULL
                        ON DUPLICATE KEY UPDATE teacher_id = VALUES(teacher_id), token_hash = VALUES(token_hash),
                            phone_key = VALUES(phone_key), paired_at = VALUES(paired_at), expires_utc = VALUES(expires_utc);
                        """;
                    session.Parameters.Add("@device", MySqlDbType.Int64).Value = device.Value;
                    session.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
                    session.Parameters.Add("@hash", MySqlDbType.Binary, 32).Value = SHA256.HashData(tokenBytes);
                    session.Parameters.Add("@phone", MySqlDbType.Binary, 32).Value = phoneKey;
                    session.Parameters.Add("@lifetime", MySqlDbType.Int32).Value = SessionLifetimeSeconds;
                    await session.ExecuteNonQueryAsync(cancellationToken);
                }
                finally { CryptographicOperations.ZeroMemory(tokenBytes); }
            }
            else token = string.Empty;
        }
        await transaction.CommitAsync(cancellationToken);
        return token;
    }

    // Callers must authenticate the device and use this inside the same transaction as any privileged operation.
    internal static async Task<long?> LockSessionAsync(MySqlConnection connection, MySqlTransaction transaction,
        string deviceId, string token, byte[] secret, CancellationToken cancellationToken = default)
    {
        ValidateSecret(secret);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) ||
            token is not { Length: 64 } || !token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return null;
        var tokenBytes = Convert.FromHexString(token);
        byte[] hash;
        try { hash = SHA256.HashData(tokenBytes); }
        finally { CryptographicOperations.ZeroMemory(tokenBytes); }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.teacher_id, s.phone_key, u.phone
            FROM faculty_device_sessions s
            INNER JOIN biometric_devices d ON d.id = s.device_id
            INNER JOIN user_accounts u ON u.id = s.teacher_id
            WHERE d.device_id = @device AND d.is_active = TRUE AND d.paired_at IS NOT NULL
              AND s.paired_at = d.paired_at AND s.token_hash = @hash AND s.expires_utc > UTC_TIMESTAMP(6)
              AND u.is_active = TRUE AND u.role = 'Teacher'
            FOR UPDATE;
            """;
        command.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        command.Parameters.Add("@hash", MySqlDbType.Binary, 32).Value = hash;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(2)) return null;
        var phone = reader.GetString(2);
        return ValidPhone(phone) && CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(1), PhoneKey(secret, phone))
            ? reader.GetInt64(0) : null;
    }

    private static async Task<long?> LockDeviceAsync(MySqlConnection connection, MySqlTransaction transaction,
        string deviceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        command.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
    }

    private static async Task<bool> IncrementLimitAsync(MySqlConnection connection, MySqlTransaction transaction,
        bool deviceLimit, long device, byte[] phoneKey, CancellationToken cancellationToken)
    {
        var table = deviceLimit ? "faculty_otp_device_limits" : "faculty_otp_phone_limits";
        var column = deviceLimit ? "device_id" : "phone_key";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {table} ({column}, window_start_utc, request_count) VALUES (@key, UTC_TIMESTAMP(6), 1)
            ON DUPLICATE KEY UPDATE
                request_count = IF(window_start_utc <= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @window SECOND), 1, request_count + 1),
                window_start_utc = IF(window_start_utc <= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @window SECOND), UTC_TIMESTAMP(6), window_start_utc);
            SELECT request_count FROM {table} WHERE {column} = @key;
            """;
        command.Parameters.Add("@key", deviceLimit ? MySqlDbType.Int64 : MySqlDbType.Binary).Value = deviceLimit ? device : phoneKey;
        command.Parameters.Add("@window", MySqlDbType.Int32).Value = WindowSeconds;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) <= (deviceLimit ? MaxDeviceRequests : MaxPhoneRequests);
    }

    private static byte[] PhoneKey(byte[] secret, string phone) =>
        HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes("SmartClassAC:faculty-phone:v1:" + phone));

    private static byte[] CodeTag(byte[] secret, Guid requestId, string code) =>
        HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes("SmartClassAC:faculty-otp:v1:" + requestId.ToString("N") + ":" + code));

    private static bool ValidPhone(string? phone) =>
        phone is { Length: 11 } && phone.StartsWith("09", StringComparison.Ordinal) && phone.All(char.IsAsciiDigit);

    private static void ValidateSecret(byte[] secret)
    {
        if (secret is not { Length: 32 }) throw new ArgumentException("Faculty OTP requires a 32-byte secret.", nameof(secret));
    }
}
