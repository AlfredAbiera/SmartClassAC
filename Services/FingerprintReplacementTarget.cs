using MySqlConnector;

namespace SmartClassAC.Services;

internal static class FingerprintReplacementTarget
{
    // This check is not authorization. Call it in the same transaction as any
    // future reservation or claim, and keep the returned locks until commit.
    internal static async Task<OperationResult> LockAndCheckAsync(
        MySqlConnection connection, MySqlTransaction transaction, long scannerId,
        long teacherId, string fingerPosition, long slot, CancellationToken cancellationToken = default)
    {
        if (scannerId <= 0 || teacherId <= 0 || !FingerprintSlots.IsValid(fingerPosition) ||
            slot <= 0 || slot > ushort.MaxValue)
            return OperationResult.Failure("Select a valid scanner, teacher, finger, and fingerprint slot.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT f.id
            FROM fingerprint_enrollment_scanner s
            INNER JOIN biometric_devices d ON d.id = s.device_id
            INNER JOIN classrooms c ON c.id = d.classroom_id
            INNER JOIN user_accounts u ON u.id = @teacher
            INNER JOIN fingerprint_templates f ON f.user_account_id = u.id
            INNER JOIN fingerprint_template_device_bindings b ON b.fingerprint_template_id = f.id
                AND b.device_id = d.device_id
            WHERE s.singleton_id = 1 AND d.id = @scanner
              AND d.is_active = TRUE AND d.paired_at IS NOT NULL
              AND d.fingerprint_enrollment_enabled = TRUE AND d.pending_clear_fingerprints = FALSE
              AND c.is_active = TRUE AND u.is_active = TRUE AND u.role = 'Teacher'
              AND f.finger_position = @finger COLLATE utf8mb4_unicode_ci
              AND f.template_identifier = @slot
            FOR UPDATE;";
        command.Parameters.Add("@scanner", MySqlDbType.Int64).Value = scannerId;
        command.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
        command.Parameters.Add("@finger", MySqlDbType.VarChar).Value = fingerPosition;
        command.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
        return await command.ExecuteScalarAsync(cancellationToken) is null
            ? OperationResult.Failure("The selected fingerprint is not bound to this eligible enrollment scanner and teacher.")
            : OperationResult.Success();
    }
}
