using MySqlConnector;

namespace SmartClassAC.Services;

// Internal prerequisite only: recording an intent cannot authorize a scanner write.
internal sealed class FingerprintReplacementRecoveryIntent
{
    private readonly string _connectionString;

    internal FingerprintReplacementRecoveryIntent(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before recording recovery intent.");
    }

    internal async Task<OperationResult> RecordAsync(
        long adminId, long scannerId, long teacherId, string fingerPosition, long slot,
        string preparationId, string guardedOperationId, CancellationToken cancellationToken = default)
    {
        if (adminId <= 0 || scannerId <= 0 || teacherId <= 0 || !FingerprintSlots.IsValid(fingerPosition) ||
            slot <= 0 || slot > ushort.MaxValue ||
            !Guid.TryParseExact(preparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(guardedOperationId, "D", out var guardedOperation) || guardedOperation == Guid.Empty)
            return OperationResult.Failure("Select a valid administrator and guarded replacement.");

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var scanner = connection.CreateCommand())
        {
            scanner.Transaction = transaction;
            scanner.CommandText = "SELECT id FROM biometric_devices WHERE id = @id FOR UPDATE;";
            scanner.Parameters.Add("@id", MySqlDbType.Int64).Value = scannerId;
            if (await scanner.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The scanner no longer exists.");
        }

        await using (var admin = connection.CreateCommand())
        {
            admin.Transaction = transaction;
            admin.CommandText = "SELECT id FROM user_accounts WHERE id = @id AND role = 'Admin' AND is_active = TRUE FOR UPDATE;";
            admin.Parameters.Add("@id", MySqlDbType.Int64).Value = adminId;
            if (await admin.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The administrator account is not active.");
        }

        var target = await FingerprintReplacementTarget.LockAndCheckAsync(
            connection, transaction, scannerId, teacherId, fingerPosition, slot, cancellationToken);
        if (!target.Succeeded) return target;

        await using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = @"SELECT w.preparation_id FROM fingerprint_replacement_write_claims w
                WHERE w.preparation_id = @id
                  AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_write_fences f
                      WHERE f.preparation_id = w.preparation_id AND f.operation_id = w.operation_id)
                FOR UPDATE;";
            claim.Parameters.Add("@id", MySqlDbType.VarChar).Value = preparation.ToString("D");
            if (await claim.ExecuteScalarAsync(cancellationToken) is not null)
                return OperationResult.Failure("A replacement write claim is unresolved; keep the slot blocked.");
        }

        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = @"
                SELECT p.id
                FROM fingerprint_enrollment_preparations p
                INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id
                INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
                INNER JOIN fingerprint_templates f ON f.id = p.replacement_template_id
                    AND f.user_account_id = p.teacher_account_id
                    AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci
                    AND f.template_identifier = p.template_identifier
                WHERE p.id = @preparation AND g.operation_id = @operation
                  AND p.device_id = @scanner AND p.teacher_account_id = @teacher
                  AND p.finger_position = @finger COLLATE utf8mb4_unicode_ci AND p.template_identifier = @slot
                FOR UPDATE;";
            guard.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            guard.Parameters.Add("@operation", MySqlDbType.VarChar).Value = guardedOperation.ToString("D");
            guard.Parameters.Add("@scanner", MySqlDbType.Int64).Value = scannerId;
            guard.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
            guard.Parameters.Add("@finger", MySqlDbType.VarChar).Value = fingerPosition;
            guard.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
            if (await guard.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The guarded replacement no longer matches the selected details.");
        }

        await using (var attempt = connection.CreateCommand())
        {
            attempt.Transaction = transaction;
            attempt.CommandText = "SELECT preparation_id FROM fingerprint_replacement_recovery_attempts WHERE preparation_id = @id FOR UPDATE;";
            attempt.Parameters.Add("@id", MySqlDbType.VarChar).Value = preparation.ToString("D");
            if (await attempt.ExecuteScalarAsync(cancellationToken) is not null)
                return OperationResult.Failure("A recovery attempt is unresolved; keep the slot blocked.");
        }

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT expires_utc > UTC_TIMESTAMP(6) FROM fingerprint_replacement_recovery_intents WHERE preparation_id = @id FOR UPDATE;";
            existing.Parameters.Add("@id", MySqlDbType.VarChar).Value = preparation.ToString("D");
            var active = await existing.ExecuteScalarAsync(cancellationToken);
            if (active is not null && Convert.ToBoolean(active))
                return OperationResult.Failure("A recovery intent is already active.");
        }

        var newRecovery = Guid.NewGuid().ToString("D");
        await using (var save = connection.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = @"
                INSERT INTO fingerprint_replacement_recovery_intents
                    (preparation_id, guarded_operation_id, recovery_operation_id, authorized_by, created_utc, expires_utc)
                VALUES (@id, @guarded, @recovery, @admin, UTC_TIMESTAMP(6), DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 5 MINUTE))
                ON DUPLICATE KEY UPDATE guarded_operation_id = VALUES(guarded_operation_id),
                    recovery_operation_id = VALUES(recovery_operation_id), authorized_by = VALUES(authorized_by),
                    created_utc = VALUES(created_utc), expires_utc = VALUES(expires_utc);";
            save.Parameters.Add("@id", MySqlDbType.VarChar).Value = preparation.ToString("D");
            save.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guardedOperation.ToString("D");
            save.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = newRecovery;
            save.Parameters.Add("@admin", MySqlDbType.Int64).Value = adminId;
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
        await RecordIntentHistoryAsync(connection, transaction, preparation, guardedOperation, newRecovery, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    // This stages fresh administrator consent only; it does not reserve or grant a write.
    internal async Task<OperationResult> RecordRetryAsync(
        long adminId, long scannerId, long teacherId, string fingerPosition, long slot,
        string preparationId, string guardedOperationId, string fencedRecoveryId,
        CancellationToken cancellationToken = default)
    {
        if (adminId <= 0 || scannerId <= 0 || teacherId <= 0 || !FingerprintSlots.IsValid(fingerPosition) ||
            slot <= 0 || slot > ushort.MaxValue ||
            !Guid.TryParseExact(preparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(guardedOperationId, "D", out var guarded) || guarded == Guid.Empty ||
            !Guid.TryParseExact(fencedRecoveryId, "D", out var recovery) || recovery == Guid.Empty || recovery == guarded)
            return OperationResult.Failure("Select a valid administrator and fenced recovery attempt.");
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scanner = connection.CreateCommand())
        {
            scanner.Transaction = transaction;
            scanner.CommandText = "SELECT id FROM biometric_devices WHERE id = @id FOR UPDATE;";
            scanner.Parameters.Add("@id", MySqlDbType.Int64).Value = scannerId;
            if (await scanner.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The scanner no longer exists.");
        }
        await using (var admin = connection.CreateCommand())
        {
            admin.Transaction = transaction;
            admin.CommandText = "SELECT id FROM user_accounts WHERE id = @id AND role = 'Admin' AND is_active = TRUE FOR UPDATE;";
            admin.Parameters.Add("@id", MySqlDbType.Int64).Value = adminId;
            if (await admin.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The administrator account is not active.");
        }
        var target = await FingerprintReplacementTarget.LockAndCheckAsync(
            connection, transaction, scannerId, teacherId, fingerPosition, slot, cancellationToken);
        if (!target.Succeeded) return target;
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = @"
                SELECT p.id FROM fingerprint_enrollment_preparations p
                INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id AND g.operation_id = @guarded
                INNER JOIN fingerprint_replacement_write_fences original_fence ON original_fence.preparation_id = p.id AND original_fence.operation_id = @guarded
                INNER JOIN fingerprint_recovery_attempt_fences attempt_fence ON attempt_fence.preparation_id = p.id
                    AND attempt_fence.guarded_operation_id = @guarded AND attempt_fence.recovery_operation_id = @recovery
                INNER JOIN fingerprint_replacement_recovery_intents i ON i.preparation_id = p.id
                    AND i.guarded_operation_id = @guarded AND i.recovery_operation_id = @recovery
                INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
                INNER JOIN fingerprint_templates f ON f.id = p.replacement_template_id
                    AND f.user_account_id = p.teacher_account_id
                    AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci
                    AND f.template_identifier = p.template_identifier
                WHERE p.id = @preparation AND p.device_id = @scanner AND p.teacher_account_id = @teacher
                  AND p.finger_position = @finger COLLATE utf8mb4_unicode_ci AND p.template_identifier = @slot
                  AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_recovery_attempts r
                      WHERE r.preparation_id = p.id AND (r.guarded_operation_id <> @guarded OR r.recovery_operation_id <> @recovery))
                  AND NOT EXISTS (SELECT 1 FROM fingerprint_recovery_write_claims w
                      WHERE w.preparation_id = p.id AND (w.guarded_operation_id <> @guarded OR w.recovery_operation_id <> @recovery))
                  AND NOT EXISTS (SELECT 1 FROM fingerprint_recovery_completions c WHERE c.preparation_id = p.id)
                FOR UPDATE;";
            check.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            check.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guarded.ToString("D");
            check.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recovery.ToString("D");
            check.Parameters.Add("@scanner", MySqlDbType.Int64).Value = scannerId;
            check.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
            check.Parameters.Add("@finger", MySqlDbType.VarChar).Value = fingerPosition;
            check.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
            if (await check.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The fenced recovery no longer matches; keep the slot blocked.");
        }
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = @"DELETE FROM fingerprint_recovery_write_claims
                    WHERE preparation_id = @preparation AND guarded_operation_id = @guarded AND recovery_operation_id = @recovery;
                DELETE FROM fingerprint_replacement_recovery_attempts
                    WHERE preparation_id = @preparation AND guarded_operation_id = @guarded AND recovery_operation_id = @recovery;";
            cleanup.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            cleanup.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guarded.ToString("D");
            cleanup.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recovery.ToString("D");
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        var newRecovery = Guid.NewGuid().ToString("D");
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = @"UPDATE fingerprint_replacement_recovery_intents
                SET recovery_operation_id = @next, authorized_by = @admin, created_utc = UTC_TIMESTAMP(6),
                    expires_utc = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 5 MINUTE)
                WHERE preparation_id = @preparation AND guarded_operation_id = @guarded AND recovery_operation_id = @recovery;";
            update.Parameters.Add("@next", MySqlDbType.VarChar).Value = newRecovery;
            update.Parameters.Add("@admin", MySqlDbType.Int64).Value = adminId;
            update.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            update.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guarded.ToString("D");
            update.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recovery.ToString("D");
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                return OperationResult.Failure("The recovery intent changed; keep the slot blocked.");
        }
        await RecordIntentHistoryAsync(connection, transaction, preparation, guarded, newRecovery, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private static async Task RecordIntentHistoryAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid preparation, Guid guarded, string recovery, CancellationToken cancellationToken)
    {
        await using var history = connection.CreateCommand();
        history.Transaction = transaction;
        history.CommandText = @"INSERT INTO fingerprint_recovery_intent_history
            (preparation_id, guarded_operation_id, recovery_operation_id, issued_utc)
            VALUES (@preparation, @guarded, @recovery, UTC_TIMESTAMP(6));";
        history.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        history.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guarded.ToString("D");
        history.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recovery;
        await history.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<string?> FindAuthenticatedAsync(
        string deviceId, Guid preparation, Guid guardedOperation, ushort slot, byte[] challengeHash,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        if (await device.ExecuteScalarAsync(cancellationToken) is null)
            return null;
        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_intent", challengeHash, cancellationToken))
            return null;

        var recoveryOperation = await FindEligibleAsync(connection, transaction, deviceId, preparation, guardedOperation, slot, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return recoveryOperation;
    }

    // Both results are snapshots, not permission to write or evidence of sensor contents.
    internal async Task<string?> FindAttemptStatusAsync(
        string deviceId, Guid preparation, Guid guardedOperation, Guid recoveryOperation,
        ushort slot, byte[] challengeHash, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        if (await device.ExecuteScalarAsync(cancellationToken) is null) return null;
        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_attempt_status", challengeHash, cancellationToken))
            return null;

        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = @"
            SELECT r.guarded_operation_id, r.recovery_operation_id, i.guarded_operation_id, i.recovery_operation_id
            FROM fingerprint_enrollment_preparations p
            INNER JOIN biometric_devices d ON d.id = p.device_id
            INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            LEFT JOIN fingerprint_replacement_recovery_attempts r ON r.preparation_id = p.id
            LEFT JOIN fingerprint_replacement_recovery_intents i ON i.preparation_id = p.id
            WHERE d.device_id = @device AND p.id = @preparation AND g.operation_id = @guarded
              AND p.template_identifier = @slot AND p.replacement_template_id IS NOT NULL
            FOR UPDATE;";
        query.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        query.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        query.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guardedOperation.ToString("D");
        query.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        string? status = null;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    if (reader.GetString(0) == guardedOperation.ToString("D") &&
                        reader.GetString(1) == recoveryOperation.ToString("D"))
                        status = "reserved";
                }
                else if (reader.IsDBNull(0) && reader.IsDBNull(1) &&
                         !reader.IsDBNull(2) && !reader.IsDBNull(3) &&
                         reader.GetString(2) == guardedOperation.ToString("D") &&
                         reader.GetString(3) == recoveryOperation.ToString("D"))
                    status = "not_reserved";
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return status;
    }

    // This reservation records uncertainty only; it is not exposed as a write grant.
    internal async Task<OperationResult> ReserveAttemptAsync(
        string deviceId, Guid preparation, Guid guardedOperation, Guid recoveryOperation, ushort slot,
        CancellationToken cancellationToken = default) =>
        await ReserveAttemptCoreAsync(deviceId, preparation, guardedOperation, recoveryOperation, slot, null, cancellationToken);

    internal async Task<OperationResult> ReserveAuthenticatedAttemptAsync(
        string deviceId, Guid preparation, Guid guardedOperation, Guid recoveryOperation, ushort slot,
        byte[] challengeHash, CancellationToken cancellationToken) =>
        await ReserveAttemptCoreAsync(deviceId, preparation, guardedOperation, recoveryOperation, slot, challengeHash, cancellationToken);

    // The device attests its local fence; serialize all server transitions on the scanner row.
    internal async Task<OperationResult> FenceAttemptAsync(
        string deviceId, Guid preparation, Guid guardedOperation, Guid recoveryOperation, ushort slot,
        byte[] challengeHash, CancellationToken cancellationToken)
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
        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_attempt_fence", challengeHash, cancellationToken))
            return OperationResult.Failure("The recovery fence challenge is invalid or expired.");
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = @"
            SELECT p.id FROM fingerprint_enrollment_preparations p
            INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id AND g.operation_id = @guarded
            INNER JOIN fingerprint_replacement_write_fences original_fence ON original_fence.preparation_id = p.id AND original_fence.operation_id = @guarded
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            WHERE p.id = @preparation AND p.device_id = @device AND p.template_identifier = @slot
              AND p.replacement_template_id IS NOT NULL
              AND (EXISTS (SELECT 1 FROM fingerprint_recovery_attempt_fences af
                       WHERE af.preparation_id = p.id AND af.guarded_operation_id = @guarded AND af.recovery_operation_id = @recovery)
                   OR (EXISTS (SELECT 1 FROM fingerprint_recovery_intent_history i
                           WHERE i.preparation_id = p.id AND i.guarded_operation_id = @guarded AND i.recovery_operation_id = @recovery)
                       AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_recovery_attempts r
                           WHERE r.preparation_id = p.id AND (r.guarded_operation_id <> @guarded OR r.recovery_operation_id <> @recovery))
                       AND NOT EXISTS (SELECT 1 FROM fingerprint_recovery_write_claims w
                           WHERE w.preparation_id = p.id AND (w.guarded_operation_id <> @guarded OR w.recovery_operation_id <> @recovery))))
            FOR UPDATE;";
        check.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        check.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guardedOperation.ToString("D");
        check.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recoveryOperation.ToString("D");
        check.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(scanner);
        check.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        if (await check.ExecuteScalarAsync(cancellationToken) is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Failure("The recovery attempt does not match; keep the slot blocked.");
        }
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = @"SELECT preparation_id, guarded_operation_id FROM fingerprint_recovery_attempt_fences
                WHERE recovery_operation_id = @recovery FOR UPDATE;";
            existing.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recoveryOperation.ToString("D");
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var match = reader.GetString(0) == preparation.ToString("D") &&
                    reader.GetString(1) == guardedOperation.ToString("D");
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return match ? OperationResult.Success() : OperationResult.Failure("The recovery fence belongs to another operation.");
            }
        }
        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO fingerprint_recovery_attempt_fences
                (preparation_id, guarded_operation_id, recovery_operation_id) VALUES (@preparation, @guarded, @recovery);";
            insert.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            insert.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guardedOperation.ToString("D");
            insert.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recoveryOperation.ToString("D");
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("The recovery fence conflicts with another operation.");
        }
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    internal async Task<OperationResult> ClaimAttemptAsync(
        string deviceId, Guid preparation, Guid guardedOperation, Guid recoveryOperation, ushort slot,
        byte[] challengeHash, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        if (await device.ExecuteScalarAsync(cancellationToken) is null)
            return OperationResult.Failure("The scanner is no longer eligible.");
        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_attempt_claim", challengeHash, cancellationToken))
            return OperationResult.Failure("The recovery write challenge is invalid or expired.");

        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = @"
            SELECT p.id FROM fingerprint_enrollment_preparations p
            INNER JOIN biometric_devices d ON d.id = p.device_id
            INNER JOIN fingerprint_enrollment_scanner s ON s.singleton_id = 1 AND s.device_id = d.id
            INNER JOIN classrooms c ON c.id = d.classroom_id
            INNER JOIN user_accounts u ON u.id = p.teacher_account_id
            INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id AND g.operation_id = @guarded
            INNER JOIN fingerprint_replacement_write_fences fence ON fence.preparation_id = p.id AND fence.operation_id = @guarded
            INNER JOIN fingerprint_replacement_recovery_intents i ON i.preparation_id = p.id
                AND i.guarded_operation_id = @guarded AND i.recovery_operation_id = @recovery
            INNER JOIN user_accounts a ON a.id = i.authorized_by AND a.role = 'Admin' AND a.is_active = TRUE
            INNER JOIN fingerprint_replacement_recovery_attempts r ON r.preparation_id = p.id
                AND r.guarded_operation_id = @guarded AND r.recovery_operation_id = @recovery
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            INNER JOIN fingerprint_templates f ON f.id = p.replacement_template_id
                AND f.user_account_id = p.teacher_account_id
                AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci
                AND f.template_identifier = p.template_identifier
            INNER JOIN fingerprint_template_device_bindings binding ON binding.fingerprint_template_id = f.id
                AND binding.device_id = d.device_id
            WHERE d.device_id = @device AND d.is_active = TRUE AND d.paired_at IS NOT NULL
              AND d.fingerprint_enrollment_enabled = TRUE AND d.pending_clear_fingerprints = FALSE
              AND c.is_active = TRUE AND u.is_active = TRUE AND u.role = 'Teacher'
              AND p.id = @preparation AND p.template_identifier = @slot AND i.expires_utc > UTC_TIMESTAMP(6)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_recovery_write_claims w WHERE w.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_recovery_attempt_fences af WHERE af.preparation_id = p.id AND af.recovery_operation_id = @recovery)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_completions x WHERE x.operation_id = @guarded OR x.preparation_id = p.id)
            FOR UPDATE;";
        check.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        check.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        check.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guardedOperation.ToString("D");
        check.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recoveryOperation.ToString("D");
        check.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        if (await check.ExecuteScalarAsync(cancellationToken) is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Failure("The fenced recovery attempt or administrator consent is no longer eligible.");
        }
        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO fingerprint_recovery_write_claims
                (preparation_id, guarded_operation_id, recovery_operation_id) VALUES (@preparation, @guarded, @recovery);";
            insert.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            insert.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guardedOperation.ToString("D");
            insert.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recoveryOperation.ToString("D");
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("A recovery write claim already exists.");
        }
    }

    private async Task<OperationResult> ReserveAttemptCoreAsync(
        string deviceId, Guid preparation, Guid guardedOperation, Guid recoveryOperation, ushort slot,
        byte[]? challengeHash, CancellationToken cancellationToken)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || preparation == Guid.Empty ||
            guardedOperation == Guid.Empty || recoveryOperation == Guid.Empty || recoveryOperation == guardedOperation || slot == 0)
            return OperationResult.Failure("Select a valid guarded replacement and recovery operation.");
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var device = connection.CreateCommand())
        {
            device.Transaction = transaction;
            device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE;";
            device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
            if (await device.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The scanner is no longer eligible.");
        }
        if (challengeHash is not null &&
            !await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_attempt_reserve", challengeHash, cancellationToken))
            return OperationResult.Failure("The recovery attempt challenge is invalid or expired.");
        var eligible = await FindEligibleAsync(connection, transaction, deviceId, preparation, guardedOperation, slot, cancellationToken);
        if (!string.Equals(eligible, recoveryOperation.ToString("D"), StringComparison.Ordinal))
        {
            if (challengeHash is not null)
                await transaction.CommitAsync(cancellationToken);
            return OperationResult.Failure("The recovery intent is missing, expired, changed, or already reserved.");
        }
        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO fingerprint_replacement_recovery_attempts
                (preparation_id, guarded_operation_id, recovery_operation_id)
                VALUES (@preparation, @guarded, @recovery);";
            insert.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            insert.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guardedOperation.ToString("D");
            insert.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recoveryOperation.ToString("D");
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            if (challengeHash is not null)
                await transaction.CommitAsync(cancellationToken);
            return OperationResult.Failure("A recovery attempt is unresolved; keep the slot blocked.");
        }
    }

    private static async Task<string?> FindEligibleAsync(
        MySqlConnection connection, MySqlTransaction transaction, string deviceId, Guid preparation,
        Guid guardedOperation, ushort slot, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = @"
            SELECT i.recovery_operation_id
            FROM fingerprint_replacement_recovery_intents i
            INNER JOIN fingerprint_enrollment_preparations p ON p.id = i.preparation_id
            INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id AND g.operation_id = i.guarded_operation_id
            INNER JOIN biometric_devices d ON d.id = p.device_id
            INNER JOIN fingerprint_enrollment_scanner s ON s.singleton_id = 1 AND s.device_id = d.id
            INNER JOIN classrooms c ON c.id = d.classroom_id
            INNER JOIN user_accounts u ON u.id = p.teacher_account_id
            INNER JOIN user_accounts a ON a.id = i.authorized_by AND a.role = 'Admin' AND a.is_active = TRUE
            INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
            INNER JOIN fingerprint_templates f ON f.id = p.replacement_template_id
                AND f.user_account_id = p.teacher_account_id
                AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci
                AND f.template_identifier = p.template_identifier
            INNER JOIN fingerprint_template_device_bindings binding ON binding.fingerprint_template_id = f.id
                AND binding.device_id = d.device_id
            WHERE d.device_id = @device AND d.is_active = TRUE AND d.paired_at IS NOT NULL
              AND d.fingerprint_enrollment_enabled = TRUE AND d.pending_clear_fingerprints = FALSE
              AND c.is_active = TRUE AND u.is_active = TRUE AND u.role = 'Teacher'
              AND p.id = @preparation AND g.operation_id = @operation AND p.template_identifier = @slot
              AND i.expires_utc > UTC_TIMESTAMP(6)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_recovery_attempts r WHERE r.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_recovery_attempt_fences af WHERE af.preparation_id = p.id AND af.recovery_operation_id = i.recovery_operation_id)
            FOR UPDATE;";
        query.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        query.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        query.Parameters.Add("@operation", MySqlDbType.VarChar).Value = guardedOperation.ToString("D");
        query.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        return await query.ExecuteScalarAsync(cancellationToken) as string;
    }
}
