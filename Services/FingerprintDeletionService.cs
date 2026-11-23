using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed record FingerprintDeletionMessage(
    string? DeviceId, string? Id, ushort Slot, string? Challenge, string? ClientNonce, string? Tag);

internal static class FingerprintDeletionRoutes
{
    internal static void MapFingerprintDeletionRoutes(this WebApplication app, Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        foreach (var action in new[] { "operation", "claim", "finish" })
        {
            var purpose = $"delete_{action}";
            var route = $"/api/device/fingerprint/delete/{action}";
            app.MapPost(route + "/challenge", async (EnrollmentRecoveryChallengeRequest request, HttpContext context,
                IConfiguration configuration, CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableFingerprintDeletion"))
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                    return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
                if (!isAuthorizedDevice(context, configuration)) return Results.Unauthorized();
                if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId)) return Results.BadRequest();
                var challenge = await new EnrollmentChallengeStore(configuration).IssueAsync(request.DeviceId!, purpose, cancellationToken);
                return challenge.Challenge is null
                    ? Results.StatusCode(StatusCodes.Status429TooManyRequests)
                    : Results.Json(new { challenge = Convert.ToHexString(challenge.Challenge).ToLowerInvariant() });
            }).WithMetadata(new RequestSizeLimitAttribute(2048));

            app.MapPost(route, async (FingerprintDeletionMessage request, HttpContext context,
                IConfiguration configuration, CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableFingerprintDeletion"))
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                    return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
                if (!isAuthorizedDevice(context, configuration)) return Results.Unauthorized();
                var result = await new FingerprintDeletionService(configuration).ExecuteAsync(action, request, cancellationToken);
                return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
            }).WithMetadata(new RequestSizeLimitAttribute(2048));
        }
    }
}

internal sealed class FingerprintDeletionService(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("SmartClassMariaDb")
        ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before deleting fingerprints.");

    internal async Task<OperationResult> RequestAsync(long adminId, long teacherId, long slot, string fingerPosition,
        string deviceId, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableFingerprintDeletion"))
            return OperationResult.Failure("Fingerprint deletion is disabled in the portal configuration.");
        if (adminId <= 0 || teacherId <= 0 || slot is <= 0 or > ushort.MaxValue ||
            !FingerprintSlots.IsValid(fingerPosition) || !EnrollmentMessageAuthentication.IsValidDeviceId(deviceId))
            return OperationResult.Failure("Select a valid teacher and fingerprint slot.");
        var scannerKey = await LoadKeyAsync(deviceId, cancellationToken);
        if (scannerKey is null) return OperationResult.Failure("The scanner's enrollment key is unavailable.");
        CryptographicOperations.ZeroMemory(scannerKey);

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // The device lock serializes with scanner lifecycle changes and other sensor operations.
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = @"SELECT d.id FROM biometric_devices d
            INNER JOIN fingerprint_template_device_bindings b ON b.device_id=d.device_id
            INNER JOIN fingerprint_templates f ON f.id=b.fingerprint_template_id
            WHERE f.user_account_id=@teacher AND f.template_identifier=@slot
              AND f.finger_position=@finger COLLATE utf8mb4_unicode_ci AND d.device_id=@deviceId
              AND d.is_active=TRUE AND d.paired_at IS NOT NULL AND d.fingerprint_enrollment_enabled=TRUE
              AND d.pending_clear_fingerprints=FALSE
              AND EXISTS (SELECT 1 FROM fingerprint_enrollment_device_credentials c WHERE c.device_id=d.id)
            FOR UPDATE;";
        device.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
        device.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
        device.Parameters.Add("@finger", MySqlDbType.VarChar).Value = fingerPosition;
        device.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId;
        var scanner = await device.ExecuteScalarAsync(cancellationToken);
        if (scanner is null)
            return OperationResult.Failure("The fingerprint must be bound to an active, paired scanner with a provisioned enrollment key, enrollment enabled and no pending wipe.");

        await using var validate = connection.CreateCommand();
        validate.Transaction = transaction;
        validate.CommandText = @"SELECT f.id FROM fingerprint_templates f
            INNER JOIN user_accounts t ON t.id=f.user_account_id AND t.role='Teacher'
            INNER JOIN user_accounts a ON a.id=@admin AND a.role='Admin' AND a.is_active=TRUE
            WHERE f.user_account_id=@teacher AND f.template_identifier=@slot
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_preparations p WHERE p.device_id=@device OR p.template_identifier=@slot)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_deletion_requests r WHERE r.device_id=@device OR r.template_identifier=@slot)
            FOR UPDATE;";
        validate.Parameters.Add("@admin", MySqlDbType.Int64).Value = adminId;
        validate.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
        validate.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
        validate.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(scanner);
        var template = await validate.ExecuteScalarAsync(cancellationToken);
        if (template is null) return OperationResult.Failure("The fingerprint, administrator, or scanner is no longer available.");

        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO fingerprint_slot_blocks (template_identifier) VALUES (@slot);
                INSERT INTO fingerprint_deletion_requests
                    (id, device_id, fingerprint_template_id, teacher_account_id, template_identifier, requested_by)
                VALUES (@id, @device, @template, @teacher, @slot, @admin);";
            insert.Parameters.Add("@id", MySqlDbType.VarChar).Value = Guid.NewGuid().ToString("D");
            insert.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(scanner);
            insert.Parameters.Add("@template", MySqlDbType.Int64).Value = Convert.ToInt64(template);
            insert.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
            insert.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
            insert.Parameters.Add("@admin", MySqlDbType.Int64).Value = adminId;
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await FingerprintSensorInventory.InvalidateAsync(connection, transaction, Convert.ToInt64(scanner), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("The scanner or fingerprint slot is blocked or already reserved.");
        }
    }

    internal async Task<OperationResult> CancelAsync(long teacherId, long slot, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (teacherId <= 0 || slot is <= 0 or > ushort.MaxValue || requestId == Guid.Empty)
            return OperationResult.Failure("Select a valid deletion request.");
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var request = connection.CreateCommand();
        request.Transaction = transaction;
        request.CommandText = @"SELECT r.id, r.state FROM fingerprint_deletion_requests r
            INNER JOIN biometric_devices d ON d.id=r.device_id
            WHERE r.teacher_account_id=@teacher AND r.template_identifier=@slot AND r.id=@id FOR UPDATE;";
        request.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
        request.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
        request.Parameters.Add("@id", MySqlDbType.VarChar).Value = requestId.ToString("D");
        string id;
        await using (var reader = await request.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return OperationResult.Failure("Deletion request not found.");
            if (reader.GetString(1) != "Pending")
                return OperationResult.Failure("The scanner claimed this deletion; keep the slot blocked and investigate.");
            id = reader.GetString(0);
        }
        await using var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = @"DELETE FROM fingerprint_deletion_requests WHERE id=@id AND state='Pending';
            DELETE FROM fingerprint_slot_blocks WHERE template_identifier=@slot;";
        cleanup.Parameters.Add("@id", MySqlDbType.VarChar).Value = id;
        cleanup.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
        if (await cleanup.ExecuteNonQueryAsync(cancellationToken) != 2)
            throw new InvalidOperationException("Fingerprint deletion cancellation did not remove the request and its block.");
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    internal async Task<EnrollmentRecoveryResponse?> ExecuteAsync(string action, FingerprintDeletionMessage message, CancellationToken cancellationToken = default)
    {
        if (action is not ("operation" or "claim" or "finish") ||
            !EnrollmentMessageAuthentication.IsValidDeviceId(message.DeviceId) ||
            !TryHex(message.Challenge, out var challenge) || !TryHex(message.ClientNonce, out _) ||
            !TryHex(message.Tag, out var tag))
            return null;
        Guid id = Guid.Empty;
        if (action != "operation" &&
            (!Guid.TryParseExact(message.Id, "D", out id) || id == Guid.Empty || message.Slot == 0))
            return null;
        var key = await LoadKeyAsync(message.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            var purpose = $"delete_{action}";
            var body = action == "operation" ? $"{message.ClientNonce}\n"
                : FormattableString.Invariant($"{id:D}\n{message.Slot}\n{message.ClientNonce}\n{(action == "claim" ? "claim" : "confirmed")}\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, message.DeviceId!, purpose, challenge, Encoding.ASCII.GetBytes(body), tag))
                return null;
            var reply = await ExecuteAuthenticatedAsync(action, message.DeviceId!, id, message.Slot,
                message.ClientNonce!, SHA256.HashData(challenge), cancellationToken);
            if (reply is null) return null;
            var signature = EnrollmentMessageAuthentication.ComputeTag(key, true, message.DeviceId!, purpose, challenge, Encoding.ASCII.GetBytes(reply));
            return new EnrollmentRecoveryResponse(reply, Convert.ToHexString(signature).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private async Task<byte[]?> LoadKeyAsync(string deviceId, CancellationToken cancellationToken)
    {
        var configured = configuration["EnrollmentSecurity:MasterKey"];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Set EnrollmentSecurity:MasterKey before using fingerprint deletion.");
        byte[] master;
        try { master = Convert.FromBase64String(configured); }
        catch (FormatException exception) { throw new InvalidOperationException("EnrollmentSecurity:MasterKey must be a Base64-encoded 32-byte key.", exception); }
        try
        {
            if (master.Length != EnrollmentMessageAuthentication.KeySize)
                throw new InvalidOperationException("EnrollmentSecurity:MasterKey must decode to 32 bytes.");
            return await new EnrollmentDeviceCredentialStore(configuration).LoadAsync(deviceId, master, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    private async Task<string?> ExecuteAuthenticatedAsync(string action, string deviceId, Guid id, ushort slot,
        string nonce, byte[] challengeHash, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = @"SELECT id FROM biometric_devices
            WHERE device_id=@device AND is_active=TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var scanner = await device.ExecuteScalarAsync(cancellationToken);
        if (scanner is null || !await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId,
            $"delete_{action}", challengeHash, cancellationToken)) return null;

        if (action == "finish")
        {
            await using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = @"SELECT 1 FROM fingerprint_deletion_completions
                WHERE id=@id AND device_id=@device AND template_identifier=@slot FOR UPDATE;";
            receipt.Parameters.Add("@id", MySqlDbType.VarChar).Value = id.ToString("D");
            receipt.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
            receipt.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
            if (await receipt.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return FormattableString.Invariant($"{id:D}\n{slot}\n{nonce}\nconfirmed\ncompleted\n");
            }
        }

        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = @"SELECT r.id, r.template_identifier, r.fingerprint_template_id, r.state
            FROM fingerprint_deletion_requests r
            INNER JOIN biometric_devices d ON d.id=r.device_id
            INNER JOIN fingerprint_templates f ON f.id=r.fingerprint_template_id
                AND f.user_account_id=r.teacher_account_id AND f.template_identifier=r.template_identifier
            INNER JOIN fingerprint_template_device_bindings b ON b.fingerprint_template_id=f.id AND b.device_id=d.device_id
            INNER JOIN fingerprint_slot_blocks block ON block.template_identifier=r.template_identifier
            WHERE r.device_id=@device AND (@discovery=TRUE OR (r.id=@id AND r.template_identifier=@slot))
              AND (@discovery=FALSE OR r.state='Pending')
              AND (@finish=TRUE OR (d.fingerprint_enrollment_enabled=TRUE AND d.pending_clear_fingerprints=FALSE))
            FOR UPDATE;";
        find.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(scanner);
        find.Parameters.Add("@discovery", MySqlDbType.Bool).Value = action == "operation";
        find.Parameters.Add("@finish", MySqlDbType.Bool).Value = action == "finish";
        find.Parameters.Add("@id", MySqlDbType.VarChar).Value = id.ToString("D");
        find.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        string requestId, state;
        long templateId;
        ushort targetSlot;
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            requestId = reader.GetString(0);
            targetSlot = reader.GetUInt16(1);
            templateId = reader.GetInt64(2);
            state = reader.GetString(3);
        }
        if (action == "operation")
        {
            await transaction.CommitAsync(cancellationToken);
            return FormattableString.Invariant($"{requestId}\n{targetSlot}\n{nonce}\ndelete\n");
        }
        if (action == "claim")
        {
            if (state != "Pending") return null;
            await using var claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = "UPDATE fingerprint_deletion_requests SET state='Claimed' WHERE id=@id AND state='Pending';";
            claim.Parameters.Add("@id", MySqlDbType.VarChar).Value = requestId;
            if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
            await transaction.CommitAsync(cancellationToken);
            return FormattableString.Invariant($"{requestId}\n{targetSlot}\n{nonce}\nclaim\ngranted\n");
        }
        if (state != "Claimed") return null;
        await FingerprintSensorInventory.InvalidateAsync(connection, transaction, Convert.ToInt64(scanner), cancellationToken);
        await using var complete = connection.CreateCommand();
        complete.Transaction = transaction;
        complete.CommandText = @"INSERT INTO fingerprint_deletion_completions (id, device_id, template_identifier) VALUES (@id, @device, @slot);
            DELETE FROM fingerprint_deletion_requests WHERE id=@id AND state='Claimed';
            DELETE FROM fingerprint_templates WHERE id=@template;
            DELETE FROM fingerprint_slot_blocks WHERE template_identifier=@slot;";
        complete.Parameters.Add("@id", MySqlDbType.VarChar).Value = requestId;
        complete.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        complete.Parameters.Add("@slot", MySqlDbType.UInt16).Value = targetSlot;
        complete.Parameters.Add("@template", MySqlDbType.Int64).Value = templateId;
        if (await complete.ExecuteNonQueryAsync(cancellationToken) != 4)
            throw new InvalidOperationException("Fingerprint deletion completion was incomplete.");
        await transaction.CommitAsync(cancellationToken);
        return FormattableString.Invariant($"{requestId}\n{targetSlot}\n{nonce}\nconfirmed\ncompleted\n");
    }

    private static bool TryHex(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value is not { Length: 64 } || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;
        bytes = Convert.FromHexString(value);
        return true;
    }
}
