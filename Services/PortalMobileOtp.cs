using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed class PortalMobileOtp(IConfiguration configuration, GsmSmsTransport transport, ILogger<PortalMobileOtp> logger)
{
    private string ConnectionString => configuration.GetConnectionString("SmartClassMariaDb")
        ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb for mobile verification.");

    internal async Task<OperationResult> IssueAsync(long actorId, string purpose, long targetId, string phone)
    {
        if (actorId <= 0 || !ValidPurpose(purpose, targetId) || !GsmSmsTransport.IsAsciiMobile(phone))
            return OperationResult.Failure("Enter a valid mobile number.");
        var secret = FacultyOtpProtocol.ReadKey(configuration["FacultyOtp:Secret"], "FacultyOtp:Secret");
        try
        {
            var requestId = Guid.NewGuid();
            var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var cleanup = connection.CreateCommand())
            {
                cleanup.Transaction = transaction;
                cleanup.CommandText = """
                    DELETE FROM portal_mobile_otp_limits WHERE window_start_utc < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 DAY)
                    ORDER BY window_start_utc LIMIT 100;
                    DELETE FROM portal_mobile_otp_requests WHERE expires_utc < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 DAY)
                    ORDER BY expires_utc LIMIT 100;
                    """;
                await cleanup.ExecuteNonQueryAsync();
            }
            await using var actor = connection.CreateCommand();
            actor.Transaction = transaction;
            actor.CommandText = "SELECT role FROM user_accounts WHERE id = @actor AND is_active = TRUE FOR UPDATE;";
            actor.Parameters.Add("@actor", MySqlDbType.Int64).Value = actorId;
            var role = await actor.ExecuteScalarAsync() as string;
            if (role != "Admin" && !(role == "Teacher" && purpose == "self" && targetId == actorId))
                return OperationResult.Failure("You are not authorized to request this verification.");
            if (purpose == "self" && (role != "Teacher" || actorId != targetId) ||
                purpose != "self" && role != "Admin")
                return OperationResult.Failure("You are not authorized to request this verification.");
            if (targetId > 0)
            {
                await using var target = connection.CreateCommand();
                target.Transaction = transaction;
                target.CommandText = "SELECT id FROM user_accounts WHERE id = @id AND role = 'Teacher' AND is_active = TRUE FOR UPDATE;";
                target.Parameters.Add("@id", MySqlDbType.Int64).Value = targetId;
                if (await target.ExecuteScalarAsync() is null) return OperationResult.Failure("Teacher account unavailable.");
            }
            await using var invalidate = connection.CreateCommand();
            invalidate.Transaction = transaction;
            invalidate.CommandText = "DELETE FROM portal_mobile_otp_requests WHERE actor_id=@actor AND purpose=@purpose AND target_id=@target;";
            invalidate.Parameters.Add("@actor", MySqlDbType.Int64).Value = actorId;
            invalidate.Parameters.Add("@purpose", MySqlDbType.VarChar).Value = purpose;
            invalidate.Parameters.Add("@target", MySqlDbType.Int64).Value = targetId;
            await invalidate.ExecuteNonQueryAsync();
            var phoneKey = Tag(secret, "phone:" + phone);
            var actorKey = Tag(secret, "actor:" + actorId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var actorAllowed = await IncrementAsync(connection, transaction, actorKey, 10);
            var phoneAllowed = await IncrementAsync(connection, transaction, phoneKey, 3);
            if (!actorAllowed || !phoneAllowed)
            {
                await transaction.CommitAsync();
                return OperationResult.Failure("Too many verification requests. Try again after five minutes.");
            }
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO portal_mobile_otp_requests
                    (actor_id, purpose, target_id, request_id, phone_key, code_tag, expires_utc)
                VALUES (@actor, @purpose, @target, @request, @phone, @code,
                    DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 300 SECOND));
                """;
            insert.Parameters.Add("@actor", MySqlDbType.Int64).Value = actorId;
            insert.Parameters.Add("@purpose", MySqlDbType.VarChar).Value = purpose;
            insert.Parameters.Add("@target", MySqlDbType.Int64).Value = targetId;
            insert.Parameters.Add("@request", MySqlDbType.Binary, 16).Value = requestId.ToByteArray();
            insert.Parameters.Add("@phone", MySqlDbType.Binary, 32).Value = phoneKey;
            insert.Parameters.Add("@code", MySqlDbType.Binary, 32).Value = CodeTag(secret, requestId, code);
            await insert.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            var accepted = false;
            GsmSmsResult result = GsmSmsResult.Unavailable;
            try
            {
                result = await transport.SendOtpAsync(await new GsmModemSettingsStore(configuration).GetAsync(), phone, code);
                accepted = result == GsmSmsResult.Accepted;
            }
            finally
            {
                await using var finish = new MySqlCommand(accepted
                    ? "UPDATE portal_mobile_otp_requests SET delivery_accepted=TRUE WHERE actor_id=@actor AND purpose=@purpose AND target_id=@target AND request_id=@request AND expires_utc>UTC_TIMESTAMP(6);"
                    : "DELETE FROM portal_mobile_otp_requests WHERE actor_id=@actor AND purpose=@purpose AND target_id=@target AND request_id=@request;", connection);
                finish.Parameters.Add("@actor", MySqlDbType.Int64).Value = actorId;
                finish.Parameters.Add("@purpose", MySqlDbType.VarChar).Value = purpose;
                finish.Parameters.Add("@target", MySqlDbType.Int64).Value = targetId;
                finish.Parameters.Add("@request", MySqlDbType.Binary, 16).Value = requestId.ToByteArray();
                accepted = await finish.ExecuteNonQueryAsync() == 1 && accepted;
            }
            if (!accepted)
            {
                logger.LogWarning("Portal mobile verification delivery failed or was superseded: {Result}.", result);
                return OperationResult.Failure(result == GsmSmsResult.DeliveryUnknown
                    ? "SMS delivery is uncertain. Check the phone before requesting a new code."
                    : "SMS could not be sent. Check the modem and try again.");
            }
            return OperationResult.Success();
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private static bool ValidPurpose(string purpose, long targetId) =>
        purpose == "create" && targetId == 0 || purpose is "admin" or "self" && targetId > 0;

    private static byte[] Tag(byte[] secret, string value) =>
        HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes("SmartClassAC:portal-mobile:v1:" + value));
    private static byte[] CodeTag(byte[] secret, Guid id, string code) => Tag(secret, "code:" + id.ToString("N") + ":" + code);

    private static async Task<bool> IncrementAsync(MySqlConnection connection, MySqlTransaction transaction, byte[] key, int limit)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO portal_mobile_otp_limits (scope_key, window_start_utc, request_count)
            VALUES (@key, UTC_TIMESTAMP(6), 1)
            ON DUPLICATE KEY UPDATE
              request_count=IF(window_start_utc<=DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 300 SECOND),1,request_count+1),
              window_start_utc=IF(window_start_utc<=DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 300 SECOND),UTC_TIMESTAMP(6),window_start_utc);
            SELECT request_count FROM portal_mobile_otp_limits WHERE scope_key=@key;
            """;
        command.Parameters.Add("@key", MySqlDbType.Binary, 32).Value = key;
        return Convert.ToInt32(await command.ExecuteScalarAsync()) <= limit;
    }

    internal static async Task<bool> ConsumeAsync(MySqlConnection connection, MySqlTransaction transaction,
        IConfiguration configuration, long actorId, string purpose, long targetId, string phone, string? code,
        CancellationToken cancellationToken)
    {
        if (actorId <= 0 || !ValidPurpose(purpose, targetId) || !GsmSmsTransport.IsAsciiMobile(phone) ||
            code is not { Length: 6 } || !code.All(char.IsAsciiDigit)) return false;
        var secret = FacultyOtpProtocol.ReadKey(configuration["FacultyOtp:Secret"], "FacultyOtp:Secret");
        try
        {
            await using var actor = connection.CreateCommand();
            actor.Transaction = transaction;
            actor.CommandText = "SELECT role FROM user_accounts WHERE id=@actor AND is_active=TRUE FOR UPDATE;";
            actor.Parameters.Add("@actor", MySqlDbType.Int64).Value = actorId;
            var role = await actor.ExecuteScalarAsync(cancellationToken) as string;
            if (purpose == "self" ? role != "Teacher" || actorId != targetId : role != "Admin") return false;
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = """
                SELECT request_id, phone_key, code_tag, failed_attempts FROM portal_mobile_otp_requests
                WHERE actor_id=@actor AND purpose=@purpose AND target_id=@target
                  AND delivery_accepted=TRUE AND expires_utc>UTC_TIMESTAMP(6) AND failed_attempts<5 FOR UPDATE;
                """;
            query.Parameters.Add("@actor", MySqlDbType.Int64).Value = actorId;
            query.Parameters.Add("@purpose", MySqlDbType.VarChar).Value = purpose;
            query.Parameters.Add("@target", MySqlDbType.Int64).Value = targetId;
            byte[] request, phoneKey, expected;
            await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) return false;
                request = reader.GetFieldValue<byte[]>(0);
                phoneKey = reader.GetFieldValue<byte[]>(1);
                expected = reader.GetFieldValue<byte[]>(2);
            }
            bool valid = CryptographicOperations.FixedTimeEquals(phoneKey, Tag(secret, "phone:" + phone)) &&
                CryptographicOperations.FixedTimeEquals(expected, CodeTag(secret, new Guid(request), code));
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = valid
                ? "DELETE FROM portal_mobile_otp_requests WHERE actor_id=@actor AND purpose=@purpose AND target_id=@target;"
                : "UPDATE portal_mobile_otp_requests SET failed_attempts=failed_attempts+1 WHERE actor_id=@actor AND purpose=@purpose AND target_id=@target;";
            update.Parameters.Add("@actor", MySqlDbType.Int64).Value = actorId;
            update.Parameters.Add("@purpose", MySqlDbType.VarChar).Value = purpose;
            update.Parameters.Add("@target", MySqlDbType.Int64).Value = targetId;
            await update.ExecuteNonQueryAsync(cancellationToken);
            return valid;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}
