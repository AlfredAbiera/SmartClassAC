using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed class FingerprintRecoveryCompletion(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("SmartClassMariaDb")
        ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before completing recovery.");

    // The device attests its confirmed journal; authentication alone cannot prove sensor contents.
    internal async Task<OperationResult> CompleteAsync(string deviceId, Guid preparation, Guid guarded, Guid recovery,
        ushort slot, byte[] challengeHash, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT id FROM biometric_devices WHERE device_id = @device FOR UPDATE;";
        device.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        var scanner = await device.ExecuteScalarAsync(cancellationToken);
        if (scanner is null) return OperationResult.Failure("The scanner no longer exists.");
        if (!await EnrollmentChallengeStore.ConsumeAsync(connection, transaction, deviceId, "replacement_attempt_finish", challengeHash, cancellationToken))
            return OperationResult.Failure("The recovery completion challenge is invalid or expired.");

        await using (var prior = connection.CreateCommand())
        {
            prior.Transaction = transaction;
            prior.CommandText = @"SELECT guarded_operation_id, preparation_id, device_id, template_identifier
                FROM fingerprint_recovery_completions WHERE recovery_operation_id = @recovery FOR UPDATE;";
            prior.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recovery.ToString("D");
            await using var reader = await prior.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var match = reader.GetString(0) == guarded.ToString("D") &&
                    reader.GetString(1) == preparation.ToString("D") &&
                    reader.GetString(2) == deviceId && reader.GetInt64(3) == slot;
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return match ? OperationResult.Success() : OperationResult.Failure("Recovery receipt does not match.");
            }
        }

        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = @"
            SELECT f.id FROM fingerprint_enrollment_preparations p
            INNER JOIN biometric_devices d ON d.id = p.device_id
            INNER JOIN fingerprint_enrollment_scanner s ON s.singleton_id = 1 AND s.device_id = d.id
            INNER JOIN classrooms c ON c.id = d.classroom_id
            INNER JOIN user_accounts u ON u.id = p.teacher_account_id
            INNER JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id AND g.operation_id = @guarded
            INNER JOIN fingerprint_replacement_write_fences fence ON fence.preparation_id = p.id AND fence.operation_id = @guarded
            INNER JOIN fingerprint_replacement_recovery_intents i ON i.preparation_id = p.id
                AND i.guarded_operation_id = @guarded AND i.recovery_operation_id = @recovery
            INNER JOIN fingerprint_replacement_recovery_attempts r ON r.preparation_id = p.id
                AND r.guarded_operation_id = @guarded AND r.recovery_operation_id = @recovery
            INNER JOIN fingerprint_recovery_write_claims w ON w.preparation_id = p.id
                AND w.guarded_operation_id = @guarded AND w.recovery_operation_id = @recovery
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
              AND NOT EXISTS (SELECT 1 FROM fingerprint_replacement_completions x WHERE x.operation_id = @guarded OR x.preparation_id = p.id)
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_completions x WHERE x.operation_id IN (@guarded, @recovery))
              AND NOT EXISTS (SELECT 1 FROM fingerprint_recovery_attempt_fences af WHERE af.preparation_id = p.id AND af.recovery_operation_id = @recovery)
            FOR UPDATE;";
        check.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        check.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guarded.ToString("D");
        check.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recovery.ToString("D");
        check.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(scanner);
        check.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        var template = await check.ExecuteScalarAsync(cancellationToken);
        if (template is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Failure("The confirmed recovery or original mapping is no longer eligible; keep the slot blocked.");
        }

        await FingerprintSensorInventory.InvalidateAsync(connection, transaction, Convert.ToInt64(scanner), cancellationToken);
        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = @"INSERT INTO fingerprint_recovery_completions
                (recovery_operation_id, guarded_operation_id, preparation_id, device_id, template_identifier, replacement_template_id)
                VALUES (@recovery, @guarded, @preparation, @device, @slot, @template);";
            receipt.Parameters.Add("@recovery", MySqlDbType.VarChar).Value = recovery.ToString("D");
            receipt.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guarded.ToString("D");
            receipt.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
            receipt.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
            receipt.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
            receipt.Parameters.Add("@template", MySqlDbType.Int64).Value = Convert.ToInt64(template);
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = @"DELETE FROM fingerprint_recovery_write_claims WHERE preparation_id = @preparation;
            DELETE FROM fingerprint_replacement_recovery_attempts WHERE preparation_id = @preparation;
            DELETE FROM fingerprint_enrollment_write_guards WHERE preparation_id = @preparation AND operation_id = @guarded;
            DELETE FROM fingerprint_enrollment_preparations WHERE id = @preparation;
            DELETE FROM fingerprint_slot_blocks WHERE template_identifier = @slot;";
        cleanup.Parameters.Add("@preparation", MySqlDbType.VarChar).Value = preparation.ToString("D");
        cleanup.Parameters.Add("@guarded", MySqlDbType.VarChar).Value = guarded.ToString("D");
        cleanup.Parameters.Add("@slot", MySqlDbType.UInt16).Value = slot;
        if (await cleanup.ExecuteNonQueryAsync(cancellationToken) != 5)
            return OperationResult.Failure("Recovery cleanup was incomplete; keep the slot blocked.");
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }
}
