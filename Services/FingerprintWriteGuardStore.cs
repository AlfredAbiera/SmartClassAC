using MySqlConnector;
using System.Security.Cryptography;
using System.Text;

namespace SmartClassAC.Services;

internal enum EnrollmentRecoveryState { Unresolved, Completed }
internal sealed record EnrollmentRecoveryResult(EnrollmentRecoveryState? State, string? Error);
internal sealed record AuthorizedEnrollmentOperation(string PreparationId, string OperationId, ushort Slot);

/// <summary>
/// Persists uncertainty before a possible sensor write. This is not a write authorization;
/// no current API or firmware is permitted to initiate a write using it.
/// </summary>
public sealed class FingerprintWriteGuardStore
{
    private readonly string _connectionString;
    private readonly IConfiguration _configuration;

    public FingerprintWriteGuardStore(IConfiguration configuration)
    {
        _configuration = configuration;
        _connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before recording a fingerprint write guard.");
    }

    internal Task<OperationResult> MarkUncertainAsync(string preparationId, string deviceId, string operationId, CancellationToken cancellationToken = default) =>
        MarkCoreAsync(preparationId, deviceId, operationId, null, null, null, cancellationToken);

    internal async Task<AuthorizedEnrollmentOperation?> GetAuthenticatedReplacementOperationAsync(
        string deviceId, byte[] challengeHash, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var scanner = await device.ExecuteScalarAsync(cancellationToken);
        if (scanner is null) return null;
        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_operation", challengeHash, cancellationToken))
            return null;
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = @"
            SELECT p.id, a.operation_id, p.template_identifier
            FROM fingerprint_enrollment_preparations p
            INNER JOIN biometric_devices d ON d.id = p.device_id
            INNER JOIN fingerprint_enrollment_scanner s ON s.singleton_id = 1 AND s.device_id = d.id
            INNER JOIN classrooms c ON c.id = d.classroom_id
            INNER JOIN user_accounts u ON u.id = p.teacher_account_id
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            INNER JOIN fingerprint_templates f ON f.id = p.replacement_template_id
                AND f.user_account_id = p.teacher_account_id
                AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci
                AND f.template_identifier = p.template_identifier
            INNER JOIN fingerprint_template_device_bindings binding ON binding.fingerprint_template_id = f.id
                AND binding.device_id = d.device_id
            INNER JOIN fingerprint_enrollment_authorizations a ON a.preparation_id = p.id
            INNER JOIN user_accounts admin ON admin.id = a.authorized_by AND admin.role = 'Admin' AND admin.is_active = TRUE
            WHERE d.id = @device AND d.is_active = TRUE AND d.paired_at IS NOT NULL
              AND d.fingerprint_enrollment_enabled = TRUE AND d.pending_clear_fingerprints = FALSE
              AND c.is_active = TRUE AND u.is_active = TRUE AND u.role = 'Teacher'
              AND a.allow_occupied_slot = TRUE AND a.expires_utc > UTC_TIMESTAMP(6)
              AND p.template_identifier BETWEEN 1 AND 65535
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_write_guards g WHERE g.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_completions x WHERE x.operation_id = a.operation_id OR x.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_completions x WHERE x.operation_id = a.operation_id)
            ORDER BY a.created_utc, p.id LIMIT 2 FOR UPDATE;";
        query.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(scanner);
        AuthorizedEnrollmentOperation? result = null;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                result = new(reader.GetString(0), reader.GetString(1), reader.GetUInt16(2));
                if (await reader.ReadAsync(cancellationToken))
                    result = null;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    // Informational snapshot only; neither state authorizes a sensor write.
    internal async Task<string?> GetReplacementGuardStatusAsync(
        string deviceId, Guid preparation, Guid operation, ushort slot, byte[] challengeHash, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        if (await device.ExecuteScalarAsync(cancellationToken) is null) return null;
        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_guard_status", challengeHash, cancellationToken))
            return null;

        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = @"
            SELECT g.operation_id, a.operation_id, a.allow_occupied_slot
            FROM fingerprint_enrollment_preparations p
            INNER JOIN biometric_devices d ON d.id = p.device_id
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            LEFT JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id
            LEFT JOIN fingerprint_enrollment_authorizations a ON a.preparation_id = p.id
            WHERE d.device_id = @device AND p.id = @preparation AND p.template_identifier = @slot
              AND p.replacement_template_id IS NOT NULL
            FOR UPDATE;";
        query.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        query.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        query.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        string? status = null;
        var foundPreparation = false;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                foundPreparation = true;
                if (!reader.IsDBNull(0) && string.Equals(reader.GetString(0), operation.ToString("D"), StringComparison.Ordinal))
                    status = "guarded";
                else if (reader.IsDBNull(0) && !reader.IsDBNull(1) && !reader.IsDBNull(2) &&
                         reader.GetBoolean(2) && string.Equals(reader.GetString(1), operation.ToString("D"), StringComparison.Ordinal))
                    status = "not_guarded";
            }
        }
        if (!foundPreparation)
        {
            await using var completed = connection.CreateCommand();
            completed.Transaction = transaction;
            completed.CommandText = @"SELECT operation_id FROM fingerprint_replacement_completions
                WHERE operation_id = @operation AND preparation_id = @preparation AND device_id = @device
                  AND template_identifier = @slot FOR UPDATE;";
            completed.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operation.ToString("D");
            completed.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            completed.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
            completed.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
            if (await completed.ExecuteScalarAsync(cancellationToken) is not null) status = "completed";
        }
        await transaction.CommitAsync(cancellationToken);
        return status;
    }

    internal Task<OperationResult> MarkReplacementUncertainAsync(
        string preparationId, string deviceId, string operationId, ushort slot, CancellationToken cancellationToken = default) =>
        MarkReplacementCoreAsync(preparationId, deviceId, operationId, slot, null, cancellationToken);

    internal Task<OperationResult> ReserveAuthenticatedReplacementGuardAsync(
        string preparationId, string deviceId, string operationId, ushort slot, byte[] challengeHash, CancellationToken cancellationToken) =>
        MarkReplacementCoreAsync(preparationId, deviceId, operationId, slot, challengeHash, cancellationToken);

    // The device attests its durable fence; a MAC alone does not prove that the device is quiescent.
    internal async Task<OperationResult> FenceReplacementWriteAsync(
        string deviceId, Guid preparation, Guid operation, ushort slot, byte[] challengeHash, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var scanner = await device.ExecuteScalarAsync(cancellationToken);
        if (scanner is null) return OperationResult.Failure("The scanner is no longer eligible.");
        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_write_fence", challengeHash, cancellationToken))
            return OperationResult.Failure("The replacement fence challenge is invalid or expired.");

        await using var guard = connection.CreateCommand();
        guard.Transaction = transaction;
        guard.CommandText = @"SELECT p.id FROM fingerprint_enrollment_preparations p
            INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id AND g.operation_id = @operation
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            WHERE p.id = @preparation AND p.device_id = @device AND p.template_identifier = @slot
              AND p.replacement_template_id IS NOT NULL FOR UPDATE;";
        guard.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        guard.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operation.ToString("D");
        guard.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(scanner);
        guard.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        if (await guard.ExecuteScalarAsync(cancellationToken) is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Failure("The replacement guard does not match; keep the slot blocked.");
        }

        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT operation_id FROM fingerprint_replacement_write_fences WHERE preparation_id = @preparation FOR UPDATE;";
        existing.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        var previous = await existing.ExecuteScalarAsync(cancellationToken) as string;
        if (previous is not null && !string.Equals(previous, operation.ToString("D"), StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Failure("The replacement fence belongs to another operation.");
        }
        if (previous is null)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO fingerprint_replacement_write_fences (preparation_id, operation_id) VALUES (@preparation, @operation);";
            insert.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            insert.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operation.ToString("D");
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    internal async Task<OperationResult> ClaimReplacementWriteAsync(
        string deviceId, Guid preparation, Guid operation, ushort slot, byte[] challengeHash, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var deviceRow = await device.ExecuteScalarAsync(cancellationToken);
        if (deviceRow is null)
            return OperationResult.Failure("The scanner no longer exists.");
        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_write_claim", challengeHash, cancellationToken))
            return OperationResult.Failure("The replacement write challenge is invalid or expired.");

        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = @"
            SELECT p.id FROM fingerprint_enrollment_preparations p
            INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id AND g.operation_id = @operation
            INNER JOIN biometric_devices d ON d.id = p.device_id
            INNER JOIN fingerprint_enrollment_scanner s ON s.singleton_id = 1 AND s.device_id = d.id
            INNER JOIN classrooms c ON c.id = d.classroom_id
            INNER JOIN user_accounts u ON u.id = p.teacher_account_id
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            INNER JOIN fingerprint_templates f ON f.id = p.replacement_template_id
                AND f.user_account_id = p.teacher_account_id
                AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci
                AND f.template_identifier = p.template_identifier
            INNER JOIN fingerprint_template_device_bindings binding ON binding.fingerprint_template_id = f.id
                AND binding.device_id = d.device_id
            INNER JOIN fingerprint_replacement_guard_authorizations a ON a.preparation_id = p.id AND a.operation_id = @operation
            INNER JOIN user_accounts admin ON admin.id = a.authorized_by AND admin.role = 'Admin' AND admin.is_active = TRUE
            WHERE p.id = @preparation AND d.id = @device AND p.template_identifier = @slot
              AND d.is_active = TRUE AND d.paired_at IS NOT NULL AND d.fingerprint_enrollment_enabled = TRUE
              AND d.pending_clear_fingerprints = FALSE AND c.is_active = TRUE AND u.is_active = TRUE AND u.role = 'Teacher'
              AND a.expires_utc > UTC_TIMESTAMP(6)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_write_claims w WHERE w.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_write_fences fence WHERE fence.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_recovery_intents i WHERE i.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_recovery_attempts r WHERE r.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_completions x WHERE x.operation_id = @operation OR x.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_completions x WHERE x.operation_id = @operation)
            FOR UPDATE;";
        check.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        check.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operation.ToString("D");
        check.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
        check.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        if (await check.ExecuteScalarAsync(cancellationToken) is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Failure("The replacement guard, target, or recovery state is no longer eligible.");
        }

        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO fingerprint_replacement_write_claims (preparation_id, operation_id) VALUES (@preparation, @operation);";
            insert.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            insert.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operation.ToString("D");
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("A replacement write claim already exists.");
        }
    }

    // A guard records uncertainty; it does not authorize a sensor write.
    private async Task<OperationResult> MarkReplacementCoreAsync(
        string preparationId, string deviceId, string operationId, ushort slot, byte[]? challengeHash, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(preparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty ||
            !EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || slot == 0)
            return OperationResult.Failure("Invalid replacement preparation, scanner, operation, or slot.");

        preparationId = preparation.ToString("D");
        operationId = operation.ToString("D");
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var deviceRow = await device.ExecuteScalarAsync(cancellationToken);
        if (deviceRow is null) return OperationResult.Failure("The scanner no longer exists.");
        if (challengeHash is not null &&
            !await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_guard_reserve", challengeHash, cancellationToken))
            return OperationResult.Failure("The replacement guard challenge is invalid or expired.");

        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = @"
            SELECT p.id FROM fingerprint_enrollment_preparations p
            INNER JOIN biometric_devices d ON d.id = p.device_id
            INNER JOIN fingerprint_enrollment_scanner s ON s.singleton_id = 1 AND s.device_id = d.id
            INNER JOIN classrooms c ON c.id = d.classroom_id
            INNER JOIN user_accounts u ON u.id = p.teacher_account_id
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            INNER JOIN fingerprint_templates f ON f.id = p.replacement_template_id
                AND f.user_account_id = p.teacher_account_id
                AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci
                AND f.template_identifier = p.template_identifier
            INNER JOIN fingerprint_template_device_bindings binding ON binding.fingerprint_template_id = f.id
                AND binding.device_id = d.device_id
            INNER JOIN fingerprint_enrollment_authorizations a ON a.preparation_id = p.id
            INNER JOIN user_accounts admin ON admin.id = a.authorized_by AND admin.role = 'Admin' AND admin.is_active = TRUE
            WHERE p.id = @preparation AND d.id = @device AND p.template_identifier = @slot
              AND d.is_active = TRUE AND d.paired_at IS NOT NULL AND d.fingerprint_enrollment_enabled = TRUE
              AND d.pending_clear_fingerprints = FALSE AND c.is_active = TRUE AND u.is_active = TRUE AND u.role = 'Teacher'
              AND a.operation_id = @operation AND a.allow_occupied_slot = TRUE AND a.expires_utc > UTC_TIMESTAMP(6)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_write_guards g WHERE g.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_completions x WHERE x.operation_id = @operation)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_completions x
                              WHERE x.operation_id = @operation OR x.preparation_id = p.id)
            FOR UPDATE;";
        check.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
        check.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
        check.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        check.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
        if (await check.ExecuteScalarAsync(cancellationToken) is null)
        {
            if (challengeHash is not null) await transaction.CommitAsync(cancellationToken);
            return OperationResult.Failure("The replacement consent, target, or scanner is no longer eligible.");
        }

        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO fingerprint_enrollment_write_guards (preparation_id, operation_id) VALUES (@preparation, @operation);";
            insert.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
            insert.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await using var rememberConsent = connection.CreateCommand();
            rememberConsent.Transaction = transaction;
            rememberConsent.CommandText = @"INSERT INTO fingerprint_replacement_guard_authorizations
                (preparation_id, operation_id, authorized_by, expires_utc)
                SELECT preparation_id, operation_id, authorized_by, expires_utc
                FROM fingerprint_enrollment_authorizations WHERE preparation_id = @preparation AND operation_id = @operation;";
            rememberConsent.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
            rememberConsent.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            if (await rememberConsent.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The replacement consent could not be preserved.");
            await FingerprintSensorInventory.InvalidateAsync(connection, transaction, Convert.ToInt64(deviceRow), cancellationToken);
            await using var consume = connection.CreateCommand();
            consume.Transaction = transaction;
            consume.CommandText = "DELETE FROM fingerprint_enrollment_authorizations WHERE preparation_id = @preparation AND operation_id = @operation;";
            consume.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
            consume.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
                return OperationResult.Failure("The replacement consent could not be consumed.");
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("A replacement write or operation is already guarded.");
        }
    }

    internal Task<OperationResult> FinalizeConfirmedReplacementAsync(
        string preparationId, string deviceId, string operationId, ushort slot, CancellationToken cancellationToken = default) =>
        FinalizeReplacementCoreAsync(preparationId, deviceId, operationId, slot, null, cancellationToken);

    internal Task<OperationResult> FinalizeAuthenticatedReplacementAsync(
        string preparationId, string deviceId, string operationId, ushort slot, byte[] challengeHash, CancellationToken cancellationToken) =>
        FinalizeReplacementCoreAsync(preparationId, deviceId, operationId, slot, challengeHash, cancellationToken);

    private async Task<OperationResult> FinalizeReplacementCoreAsync(
        string preparationId, string deviceId, string operationId, ushort slot, byte[]? challengeHash, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(preparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty ||
            !EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || slot == 0)
            return OperationResult.Failure("Invalid replacement preparation, scanner, operation, or slot.");

        preparationId = preparation.ToString("D");
        operationId = operation.ToString("D");
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var deviceRow = await device.ExecuteScalarAsync(cancellationToken);
        if (deviceRow is null) return OperationResult.Failure("The scanner no longer exists.");
        if (challengeHash is not null &&
            !await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_finish", challengeHash, cancellationToken))
            return OperationResult.Failure("The replacement completion challenge is invalid or expired.");

        await using (var completed = connection.CreateCommand())
        {
            completed.Transaction = transaction;
            completed.CommandText = @"SELECT preparation_id, device_id, template_identifier
                FROM fingerprint_replacement_completions WHERE operation_id = @operation FOR UPDATE;";
            completed.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            await using var reader = await completed.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!string.Equals(reader.GetString(0), preparationId, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(1), deviceId, StringComparison.Ordinal) || reader.GetInt64(2) != slot)
                    return OperationResult.Failure("The operation identifier belongs to another replacement.");
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return OperationResult.Success();
            }
        }

        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = @"
            SELECT f.id FROM fingerprint_enrollment_preparations p
            INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id AND g.operation_id = @operation
            INNER JOIN biometric_devices d ON d.id = p.device_id
            INNER JOIN fingerprint_enrollment_scanner s ON s.singleton_id = 1 AND s.device_id = d.id
            INNER JOIN classrooms c ON c.id = d.classroom_id
            INNER JOIN user_accounts u ON u.id = p.teacher_account_id
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            INNER JOIN fingerprint_templates f ON f.id = p.replacement_template_id
                AND f.user_account_id = p.teacher_account_id
                AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci
                AND f.template_identifier = p.template_identifier
            INNER JOIN fingerprint_template_device_bindings binding ON binding.fingerprint_template_id = f.id
                AND binding.device_id = d.device_id
            WHERE p.id = @preparation AND d.id = @device AND p.template_identifier = @slot
              AND d.is_active = TRUE AND d.paired_at IS NOT NULL AND d.fingerprint_enrollment_enabled = TRUE
              AND d.pending_clear_fingerprints = FALSE AND c.is_active = TRUE AND u.is_active = TRUE AND u.role = 'Teacher'
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_recovery_intents i WHERE i.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_recovery_attempts r WHERE r.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_completions x WHERE x.operation_id = @operation)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_write_fences fence WHERE fence.preparation_id = p.id)
              AND (@requireClaim = FALSE OR EXISTS (
                  SELECT 1 FROM fingerprint_replacement_write_claims w
                  WHERE w.preparation_id = p.id AND w.operation_id = @operation))
            FOR UPDATE;";
        check.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
        check.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
        check.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        check.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
        check.Parameters.Add("@requireClaim", MySqlDbType.Bool).Value = challengeHash is not null;
        var templateId = await check.ExecuteScalarAsync(cancellationToken);
        if (templateId is null)
            return OperationResult.Failure("The guarded replacement or its original mapping is no longer eligible; keep the slot blocked.");

        try
        {
            await FingerprintSensorInventory.InvalidateAsync(connection, transaction, Convert.ToInt64(deviceRow), cancellationToken);
            await using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = @"INSERT INTO fingerprint_replacement_completions
                (operation_id, preparation_id, device_id, template_identifier, replacement_template_id)
                VALUES (@operation, @preparation, @device, @slot, @template);";
            receipt.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            receipt.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
            receipt.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
            receipt.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
            receipt.Parameters.Add("@template", MySqlDbType.Int64).Value = Convert.ToInt64(templateId);
            await receipt.ExecuteNonQueryAsync(cancellationToken);
            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = @"DELETE FROM fingerprint_enrollment_write_guards WHERE preparation_id = @preparation AND operation_id = @operation;
                DELETE FROM fingerprint_enrollment_preparations WHERE id = @preparation;
                DELETE FROM fingerprint_slot_blocks WHERE template_identifier = @slot;";
            cleanup.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
            cleanup.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            cleanup.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
            if (await cleanup.ExecuteNonQueryAsync(cancellationToken) != 3)
                return OperationResult.Failure("Replacement cleanup was incomplete; keep the slot blocked.");
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("The replacement operation or preparation was already completed.");
        }
    }

    /// <summary>
    /// Reports durable state after a lost response. An unresolved guard never grants
    /// permission to write or establishes that a sensor write occurred.
    /// </summary>
    internal async Task<EnrollmentRecoveryResult> GetAuthenticatedRecoveryStateAsync(
        string preparationId, string deviceId, string operationId, ushort slot,
        byte[] challenge, byte[] clientNonce, byte[] tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(clientNonce);
        ArgumentNullException.ThrowIfNull(tag);
        if (!Guid.TryParseExact(preparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty ||
            !EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || slot == 0 ||
            challenge.Length != EnrollmentMessageAuthentication.ChallengeSize ||
            clientNonce.Length != EnrollmentMessageAuthentication.ChallengeSize ||
            tag.Length != EnrollmentMessageAuthentication.TagSize)
            return new(null, "Invalid enrollment recovery request.");

        var nonceText = Convert.ToHexString(clientNonce).ToLowerInvariant();
        var body = Encoding.ASCII.GetBytes(FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{slot}\n{nonceText}\n"));
        if (!await VerifyDeviceMessageAsync(deviceId, "recover", challenge, body, tag, cancellationToken))
            return new(null, "Enrollment recovery authentication failed.");

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @id AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        device.Parameters.Add("@id", MySqlDbType.VarChar).Value = deviceId;
        var deviceRow = await device.ExecuteScalarAsync(cancellationToken);
        if (deviceRow is null)
            return new(null, "Enrollment recovery authentication failed.");
        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "recover", SHA256.HashData(challenge), cancellationToken))
            return new(null, "Enrollment recovery challenge is invalid or expired.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT 1 FROM fingerprint_enrollment_write_guards g
            INNER JOIN fingerprint_enrollment_preparations p ON p.id = g.preparation_id
            WHERE p.id = @preparation AND g.operation_id = @operation
              AND p.device_id = @device AND p.template_identifier = @slot
            FOR UPDATE;";
        command.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        command.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operation.ToString("D");
        command.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
        command.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        var unresolved = await command.ExecuteScalarAsync(cancellationToken) is not null;
        if (!unresolved)
        {
            command.CommandText = @"SELECT 1 FROM fingerprint_enrollment_completions
                WHERE preparation_id = @preparation AND operation_id = @operation
                  AND device_id = @deviceId AND template_identifier = @slot FOR UPDATE;";
            command.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId;
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
                return new(null, "No matching enrollment recovery state was found.");
        }
        await transaction.CommitAsync(cancellationToken);
        return new(unresolved ? EnrollmentRecoveryState.Unresolved : EnrollmentRecoveryState.Completed, null);
    }

    internal async Task<OperationResult> ClaimAuthorizedWriteAsync(
        string preparationId, string deviceId, string operationId, ushort slot, bool occupied,
        byte[] challenge, byte[] tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(tag);
        if (!Guid.TryParseExact(preparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty ||
            !EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || slot == 0 ||
            challenge.Length != EnrollmentMessageAuthentication.ChallengeSize ||
            tag.Length != EnrollmentMessageAuthentication.TagSize)
            return OperationResult.Failure("Invalid enrollment claim.");
        var body = Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"{preparation:D}\n{operation:D}\n{slot}\n{(occupied ? "occupied" : "empty")}\n"));
        if (!await VerifyDeviceMessageAsync(deviceId, "claim", challenge, body, tag, cancellationToken))
            return OperationResult.Failure("Enrollment claim authentication failed.");
        if (occupied)
            return OperationResult.Failure("Replacement of an occupied fingerprint slot is not available yet.");
        return await MarkCoreAsync(preparation.ToString("D"), deviceId, operation.ToString("D"),
            slot, occupied, SHA256.HashData(challenge), cancellationToken);
    }

    internal async Task<AuthorizedEnrollmentOperation?> GetAuthorizedOperationAsync(
        string deviceId, CancellationToken cancellationToken = default)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId)) return null;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT p.id, a.operation_id, p.template_identifier
            FROM fingerprint_enrollment_preparations p
            INNER JOIN biometric_devices d ON d.id = p.device_id
            INNER JOIN fingerprint_enrollment_scanner s ON s.singleton_id = 1 AND s.device_id = d.id
            INNER JOIN classrooms c ON c.id = d.classroom_id
            INNER JOIN user_accounts u ON u.id = p.teacher_account_id
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            INNER JOIN fingerprint_enrollment_authorizations a ON a.preparation_id = p.id
            INNER JOIN user_accounts admin ON admin.id = a.authorized_by AND admin.role = 'Admin' AND admin.is_active = TRUE
            WHERE d.device_id = @deviceId AND p.replacement_template_id IS NULL AND d.is_active = TRUE AND d.paired_at IS NOT NULL
              AND d.fingerprint_enrollment_enabled = TRUE AND d.pending_clear_fingerprints = FALSE
              AND c.is_active = TRUE AND u.is_active = TRUE AND u.role = 'Teacher'
              AND a.expires_utc > UTC_TIMESTAMP(6)
              AND p.template_identifier BETWEEN 1 AND 65535
              AND (SELECT COUNT(*) FROM fingerprint_templates count_fingerprints
                   WHERE count_fingerprints.user_account_id = p.teacher_account_id) < 2
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_write_guards g WHERE g.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_completions x WHERE x.operation_id = a.operation_id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_templates f
                              WHERE f.template_identifier = p.template_identifier
                                 OR (f.user_account_id = p.teacher_account_id
                                     AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci))
            ORDER BY a.created_utc, p.id
            LIMIT 1
            FOR UPDATE;";
        command.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId;
        AuthorizedEnrollmentOperation? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
                result = new AuthorizedEnrollmentOperation(
                    reader.GetString(0), reader.GetString(1), Convert.ToUInt16(reader.GetInt64(2)));
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<OperationResult> MarkCoreAsync(string preparationId, string deviceId, string operationId,
        ushort? claimedSlot, bool? occupied, byte[]? challengeHash, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(preparationId, "D", out var preparationGuid) || preparationGuid == Guid.Empty ||
            !Guid.TryParseExact(operationId, "D", out var operationGuid) || operationGuid == Guid.Empty ||
            !EnrollmentMessageAuthentication.IsValidDeviceId(deviceId))
            return OperationResult.Failure("Invalid enrollment preparation, device, or operation identifier.");
        preparationId = preparationGuid.ToString("D");
        operationId = operationGuid.ToString("D");

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var device = connection.CreateCommand();
            device.Transaction = transaction;
            device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @deviceId FOR UPDATE;";
            device.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId;
            var deviceRow = await device.ExecuteScalarAsync(cancellationToken);
            if (deviceRow is null)
                return OperationResult.Failure("The scanner no longer exists.");

            if (challengeHash is not null &&
                !await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "claim", challengeHash, cancellationToken))
                return OperationResult.Failure("Enrollment claim challenge is invalid or expired.");

            await using var completed = connection.CreateCommand();
            completed.Transaction = transaction;
            completed.CommandText = @"SELECT operation_id FROM fingerprint_enrollment_completions
                WHERE operation_id = @operation
                UNION ALL SELECT operation_id FROM fingerprint_replacement_completions
                WHERE operation_id = @operation FOR UPDATE;";
            completed.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            if (await completed.ExecuteScalarAsync(cancellationToken) is not null)
                return OperationResult.Failure("The write operation identifier has already been completed.");

            await using var preparation = connection.CreateCommand();
            preparation.Transaction = transaction;
            preparation.CommandText = @"
                SELECT p.template_identifier, p.teacher_account_id, p.finger_position,
                       d.is_active AND d.paired_at IS NOT NULL AND d.fingerprint_enrollment_enabled
                         AND NOT d.pending_clear_fingerprints AND c.is_active AND u.is_active
                         AND u.role = 'Teacher' AND e.device_id IS NOT NULL
                FROM fingerprint_enrollment_preparations p
                INNER JOIN biometric_devices d ON d.id = p.device_id
                LEFT JOIN classrooms c ON c.id = d.classroom_id
                INNER JOIN user_accounts u ON u.id = p.teacher_account_id
                LEFT JOIN fingerprint_enrollment_scanner e ON e.singleton_id = 1 AND e.device_id = d.id
                WHERE p.id = @id AND p.device_id = @deviceRow AND p.replacement_template_id IS NULL FOR UPDATE;";
            preparation.Parameters.Add("@id", MySqlDbType.VarChar).Value = preparationId;
            preparation.Parameters.Add("@deviceRow", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
            long slot;
            long teacher;
            string finger;
            bool eligible;
            await using (var reader = await preparation.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    return OperationResult.Failure("The preparation does not belong to this scanner.");
                slot = reader.GetInt64(0);
                teacher = reader.GetInt64(1);
                finger = reader.GetString(2);
                eligible = !reader.IsDBNull(3) && reader.GetBoolean(3);
            }

            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT operation_id FROM fingerprint_enrollment_write_guards WHERE preparation_id = @id FOR UPDATE;";
            existing.Parameters.Add("@id", MySqlDbType.VarChar).Value = preparationId;
            var prior = await existing.ExecuteScalarAsync(cancellationToken) as string;
            if (prior is not null)
                return challengeHash is null && string.Equals(prior, operationId, StringComparison.OrdinalIgnoreCase)
                    ? OperationResult.Success()
                    : OperationResult.Failure("This preparation already has an unresolved sensor write.");
            if (!eligible)
                return OperationResult.Failure("The scanner or teacher is no longer eligible for enrollment.");
            await using var fingerprintCount = connection.CreateCommand();
            fingerprintCount.Transaction = transaction;
            fingerprintCount.CommandText = "SELECT COUNT(*) FROM fingerprint_templates WHERE user_account_id = @teacher;";
            fingerprintCount.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacher;
            if (Convert.ToInt64(await fingerprintCount.ExecuteScalarAsync(cancellationToken)) >= 2)
                return OperationResult.Failure("This teacher already has two or more fingerprints.");
            if (claimedSlot is not null)
            {
                if (slot != claimedSlot)
                    return OperationResult.Failure("The scanner claim does not match the reserved slot.");
                await using var consent = connection.CreateCommand();
                consent.Transaction = transaction;
                consent.CommandText = @"SELECT a.preparation_id
                    FROM fingerprint_enrollment_authorizations a
                    INNER JOIN user_accounts admin ON admin.id = a.authorized_by AND admin.role = 'Admin' AND admin.is_active = TRUE
                    WHERE a.preparation_id = @preparation AND a.operation_id = @operation
                      AND a.expires_utc > UTC_TIMESTAMP(6)
                      AND (@occupied = FALSE OR a.allow_occupied_slot = TRUE)
                    FOR UPDATE;";
                consent.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
                consent.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
                consent.Parameters.Add("@occupied", MySqlDbType.Bool).Value = occupied!.Value;
                if (await consent.ExecuteScalarAsync(cancellationToken) is null)
                    return OperationResult.Failure("Enrollment consent is missing, expired, or does not permit replacement.");
            }

            await using var collision = connection.CreateCommand();
            collision.Transaction = transaction;
            collision.CommandText = @"SELECT id FROM fingerprint_templates
                WHERE template_identifier = @slot OR (user_account_id = @teacher AND finger_position = @finger)
                LIMIT 1 FOR UPDATE;";
            collision.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
            collision.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacher;
            collision.Parameters.Add("@finger", MySqlDbType.VarChar).Value = finger;
            if (await collision.ExecuteScalarAsync(cancellationToken) is not null)
                return OperationResult.Failure("The server slot or finger now has a mapping.");

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO fingerprint_enrollment_write_guards (preparation_id, operation_id) VALUES (@preparation, @operation);";
            insert.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
            insert.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await FingerprintSensorInventory.InvalidateAsync(connection, transaction, Convert.ToInt64(deviceRow), cancellationToken);
            if (claimedSlot is not null)
            {
                await using var consumeConsent = connection.CreateCommand();
                consumeConsent.Transaction = transaction;
                consumeConsent.CommandText = "DELETE FROM fingerprint_enrollment_authorizations WHERE preparation_id = @id AND operation_id = @operation;";
                consumeConsent.Parameters.Add("@id", MySqlDbType.VarChar).Value = preparationId;
                consumeConsent.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
                if (await consumeConsent.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Enrollment consent changed during claim.");
            }
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("The write operation identifier is already in use.");
        }
    }

    /// <summary>
    /// Internal test path for an already guarded write; it does not authenticate a scanner.
    /// No endpoint may call this method.
    /// </summary>
    internal Task<OperationResult> FinalizeConfirmedWriteAsync(string preparationId, string deviceId, string operationId, CancellationToken cancellationToken = default) =>
        FinalizeCoreAsync(preparationId, deviceId, operationId, null, null, cancellationToken);

    /// <summary>
    /// Consumes a fresh authenticated confirmation and commits its mapping and receipt
    /// in one transaction using the independently provisioned scanner key.
    /// This does not establish that the scanner journal or sensor is trustworthy.
    /// </summary>
    internal async Task<OperationResult> FinalizeAuthenticatedWriteAsync(
        string preparationId, string deviceId, string operationId, ushort slot,
        byte[] challenge, byte[] tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(tag);
        if (!Guid.TryParseExact(preparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty ||
            !EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || slot == 0 ||
            challenge.Length != EnrollmentMessageAuthentication.ChallengeSize ||
            tag.Length != EnrollmentMessageAuthentication.TagSize)
            return OperationResult.Failure("Invalid authenticated enrollment confirmation.");

        var body = Encoding.ASCII.GetBytes(FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{slot}\nconfirmed\n"));
        if (!await VerifyDeviceMessageAsync(deviceId, "finish", challenge, body, tag, cancellationToken))
            return OperationResult.Failure("Enrollment confirmation authentication failed.");
        return await FinalizeCoreAsync(preparation.ToString("D"), deviceId, operation.ToString("D"),
            slot, SHA256.HashData(challenge), cancellationToken);
    }

    private async Task<bool> VerifyDeviceMessageAsync(string deviceId, string operation, byte[] challenge,
        byte[] body, byte[] tag, CancellationToken cancellationToken)
    {
        var configured = _configuration["EnrollmentSecurity:MasterKey"];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Set EnrollmentSecurity:MasterKey before authenticating enrollment confirmations.");
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
            var key = await new EnrollmentDeviceCredentialStore(_configuration).LoadAsync(deviceId, master, cancellationToken);
            if (key is null)
                return false;
            try
            {
                return EnrollmentMessageAuthentication.VerifyTag(key, false, deviceId, operation, challenge, body, tag);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    private async Task<OperationResult> FinalizeCoreAsync(string preparationId, string deviceId, string operationId,
        ushort? confirmedSlot, byte[]? challengeHash, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(preparationId, "D", out var preparationGuid) || preparationGuid == Guid.Empty ||
            !Guid.TryParseExact(operationId, "D", out var operationGuid) || operationGuid == Guid.Empty ||
            !EnrollmentMessageAuthentication.IsValidDeviceId(deviceId))
            return OperationResult.Failure("Invalid enrollment preparation, device, or operation identifier.");
        preparationId = preparationGuid.ToString("D");
        operationId = operationGuid.ToString("D");

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var device = connection.CreateCommand();
            device.Transaction = transaction;
            device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @deviceId FOR UPDATE;";
            device.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId;
            var deviceRow = await device.ExecuteScalarAsync(cancellationToken);
            if (deviceRow is null)
                return OperationResult.Failure("The scanner no longer exists.");

            if (challengeHash is not null &&
                !await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "finish", challengeHash, cancellationToken))
                return OperationResult.Failure("Enrollment confirmation challenge is invalid or expired.");

            await using var completed = connection.CreateCommand();
            completed.Transaction = transaction;
            completed.CommandText = @"SELECT preparation_id, device_id, template_identifier FROM fingerprint_enrollment_completions
                    WHERE operation_id = @operation FOR UPDATE;";
            completed.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            await using (var reader = await completed.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (!string.Equals(reader.GetString(0), preparationId, StringComparison.Ordinal) ||
                        !string.Equals(reader.GetString(1), deviceId, StringComparison.Ordinal) ||
                        (confirmedSlot is not null && (reader.IsDBNull(2) || reader.GetInt64(2) != confirmedSlot)))
                        return OperationResult.Failure("The operation identifier belongs to another enrollment.");
                    await reader.DisposeAsync();
                    await transaction.CommitAsync(cancellationToken);
                    return OperationResult.Success();
                }
            }

            await using var preparation = connection.CreateCommand();
            preparation.Transaction = transaction;
            preparation.CommandText = @"
                    SELECT p.template_identifier, p.teacher_account_id, p.finger_position,
                           d.is_active AND d.paired_at IS NOT NULL AND d.fingerprint_enrollment_enabled
                             AND NOT d.pending_clear_fingerprints AND c.is_active AND u.is_active
                             AND u.role = 'Teacher' AND e.device_id IS NOT NULL
                    FROM fingerprint_enrollment_preparations p
                    INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id AND g.operation_id = @operation
                    INNER JOIN biometric_devices d ON d.id = p.device_id
                    LEFT JOIN classrooms c ON c.id = d.classroom_id
                    INNER JOIN user_accounts u ON u.id = p.teacher_account_id
                    LEFT JOIN fingerprint_enrollment_scanner e ON e.singleton_id = 1 AND e.device_id = d.id
                    WHERE p.id = @id AND p.device_id = @deviceRow AND p.replacement_template_id IS NULL FOR UPDATE;";
            preparation.Parameters.Add("@id", MySqlDbType.VarChar).Value = preparationId;
            preparation.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            preparation.Parameters.Add("@deviceRow", MySqlDbType.Int64).Value = Convert.ToInt64(deviceRow);
            long slot;
            long teacher;
            string finger;
            await using (var reader = await preparation.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    return OperationResult.Failure("No matching unresolved write was found for this scanner.");
                if (reader.IsDBNull(3) || !reader.GetBoolean(3))
                    return OperationResult.Failure("The scanner or teacher is no longer eligible; the slot remains blocked.");
                slot = reader.GetInt64(0);
                teacher = reader.GetInt64(1);
                finger = reader.GetString(2);
            }
            if (confirmedSlot is not null && slot != confirmedSlot)
                return OperationResult.Failure("The scanner confirmation does not match the reserved slot.");

            await FingerprintSensorInventory.InvalidateAsync(connection, transaction, Convert.ToInt64(deviceRow), cancellationToken);
            await using var mapping = connection.CreateCommand();
            mapping.Transaction = transaction;
            mapping.CommandText = @"INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                                        VALUES (@teacher, @finger, @slot);";
            mapping.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacher;
            mapping.Parameters.Add("@finger", MySqlDbType.VarChar).Value = finger;
            mapping.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
            await mapping.ExecuteNonQueryAsync(cancellationToken);

            await using var binding = connection.CreateCommand();
            binding.Transaction = transaction;
            binding.CommandText = "INSERT INTO fingerprint_template_device_bindings (fingerprint_template_id, device_id) VALUES (@id, @device);";
            binding.Parameters.Add("@id", MySqlDbType.Int64).Value = mapping.LastInsertedId;
            binding.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
            await binding.ExecuteNonQueryAsync(cancellationToken);

            await using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = @"INSERT INTO fingerprint_enrollment_completions (operation_id, preparation_id, device_id, template_identifier)
                                        VALUES (@operation, @preparation, @device, @slot);";
            receipt.Parameters.Add("@operation", MySqlDbType.VarChar).Value = operationId;
            receipt.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
            receipt.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
            receipt.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
            await receipt.ExecuteNonQueryAsync(cancellationToken);

            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = @"DELETE FROM fingerprint_enrollment_write_guards WHERE preparation_id = @preparation;
                                        DELETE FROM fingerprint_enrollment_preparations WHERE id = @preparation;
                                        DELETE FROM fingerprint_slot_blocks WHERE template_identifier = @slot;";
            cleanup.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparationId;
            cleanup.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("A conflicting fingerprint mapping or completed operation exists; the slot remains blocked.");
        }
    }
}
