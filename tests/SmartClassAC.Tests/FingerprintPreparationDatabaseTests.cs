using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class MariaDbFactAttribute : FactAttribute
{
    public MariaDbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SMARTCLASS_TEST_CONNECTION")))
            Skip = "Set SMARTCLASS_TEST_CONNECTION to a MariaDB account allowed to create and drop a temporary test database.";
    }
}

public sealed class FingerprintPreparationDatabaseTests
{
    [MariaDbFact]
    public async Task Preparation_IsAtomicRestrictedAndKeepsBlockAfterCancellation()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCLASS_TEST_CONNECTION")!)
        {
            Database = string.Empty
        };
        var database = "smartclass_prep_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = new MySqlCommand($"CREATE DATABASE `{database}`", admin))
                await create.ExecuteNonQueryAsync();
            created = true;
            builder.Database = database;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString
            }).Build();
            var migration = new DatabaseMigrationRunner(configuration, NullLogger<DatabaseMigrationRunner>.Instance);
            await migration.RunMigrationsAsync();
            await migration.RunMigrationsAsync();

            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, """
                INSERT INTO classrooms (id, name, capacity, is_active) VALUES (1, 'Room', 30, 1);
                INSERT INTO biometric_devices (id, classroom_id, device_id, display_name, is_active, paired_at)
                    VALUES (1, 1, '1000000001', 'Device 1', 1, CURRENT_TIMESTAMP),
                           (2, NULL, '1000000002', 'Device 2', 1, CURRENT_TIMESTAMP),
                           (3, NULL, '1000000003', 'Device 3', 1, CURRENT_TIMESTAMP);
                INSERT INTO fingerprint_enrollment_scanner VALUES (1, 1);
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                    VALUES (1, 'one', 'Teacher One', 'hash', 'Teacher', 1),
                           (2, 'two', 'Teacher Two', 'hash', 'Teacher', 1),
                           (3, 'admin', 'Admin', 'hash', 'Admin', 1);
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                    VALUES (1, 'Right thumb', 2);
                """);

            var repository = new MariaDbUserAccountRepository(configuration, Options.Create(new SchoolTimeOptions()));
            async Task<OperationResult> CheckReplacement(long scanner, long teacher, string finger, long slot)
            {
                await using var transaction = await connection.BeginTransactionAsync();
                var result = await FingerprintReplacementTarget.LockAndCheckAsync(
                    connection, transaction, scanner, teacher, finger, slot);
                await transaction.RollbackAsync();
                return result;
            }

            Assert.False((await CheckReplacement(1, 1, "Right thumb", 2)).Succeeded);
            await ExecuteAsync(connection, """
                INSERT INTO fingerprint_template_device_bindings (fingerprint_template_id, device_id)
                SELECT id, '1000000002' FROM fingerprint_templates WHERE template_identifier = 2
                """);
            Assert.False((await CheckReplacement(1, 1, "Right thumb", 2)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001'");
            Assert.True((await CheckReplacement(1, 1, "Right thumb", 2)).Succeeded);
            Assert.False((await CheckReplacement(0, 1, "Right thumb", 2)).Succeeded);
            Assert.False((await CheckReplacement(1, 0, "Right thumb", 2)).Succeeded);
            Assert.False((await CheckReplacement(2, 1, "Right thumb", 2)).Succeeded);
            Assert.False((await CheckReplacement(1, 2, "Right thumb", 2)).Succeeded);
            Assert.False((await CheckReplacement(1, 1, "Left thumb", 2)).Succeeded);
            Assert.False((await CheckReplacement(1, 1, "invalid", 2)).Succeeded);
            Assert.False((await CheckReplacement(1, 1, "Right thumb", 3)).Succeeded);
            Assert.False((await CheckReplacement(1, 1, "Right thumb", 65536)).Succeeded);
            await using (var locked = await connection.BeginTransactionAsync())
            {
                Assert.True((await FingerprintReplacementTarget.LockAndCheckAsync(
                    connection, locked, 1, 1, "Right thumb", 2)).Succeeded);
                await using var concurrent = new MySqlConnection(builder.ConnectionString);
                await concurrent.OpenAsync();
                await ExecuteAsync(concurrent, "SET SESSION innodb_lock_wait_timeout = 1");
                var exception = await Assert.ThrowsAsync<MySqlException>(() =>
                    ExecuteAsync(concurrent, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002'"));
                Assert.Equal(MySqlErrorCode.LockWaitTimeout, exception.ErrorCode);
                await locked.RollbackAsync();
            }
            foreach (var statement in new[]
            {
                "UPDATE biometric_devices SET paired_at = NULL WHERE id = 1",
                "UPDATE biometric_devices SET paired_at = CURRENT_TIMESTAMP, pending_clear_fingerprints = 1 WHERE id = 1",
                "UPDATE biometric_devices SET pending_clear_fingerprints = 0, fingerprint_enrollment_enabled = 0 WHERE id = 1",
                "UPDATE biometric_devices SET fingerprint_enrollment_enabled = 1, is_active = 0 WHERE id = 1",
                "UPDATE biometric_devices SET is_active = 1 WHERE id = 1; UPDATE classrooms SET is_active = 0 WHERE id = 1",
                "UPDATE classrooms SET is_active = 1 WHERE id = 1; UPDATE user_accounts SET is_active = 0 WHERE id = 1"
            })
            {
                await ExecuteAsync(connection, statement);
                Assert.False((await CheckReplacement(1, 1, "Right thumb", 2)).Succeeded);
            }
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 1");
            Assert.True((await CheckReplacement(1, 1, "Right thumb", 2)).Succeeded);
            Assert.False((await repository.PrepareFingerprintReplacementAsync(1, 2, "Right thumb", 2)).Succeeded);
            Assert.DoesNotContain(2, await repository.GetBlockedFingerprintSlotsAsync());

            await ExecuteAsync(connection, """
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                VALUES (2, 'Left index', 30);
                INSERT INTO fingerprint_template_device_bindings (fingerprint_template_id, device_id)
                SELECT id, '1000000001' FROM fingerprint_templates WHERE template_identifier = 30;
                CREATE TRIGGER reject_replacement BEFORE INSERT ON fingerprint_enrollment_preparations
                FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated reservation failure';
                """);
            await Assert.ThrowsAsync<MySqlException>(() => repository.PrepareFingerprintReplacementAsync(1, 2, "Left index", 30));
            Assert.DoesNotContain(30, await repository.GetBlockedFingerprintSlotsAsync());
            await ExecuteAsync(connection, "DROP TRIGGER reject_replacement");

            var replacementAttempts = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => repository.PrepareFingerprintReplacementAsync(1, 2, "Left index", 30)));
            Assert.Single(replacementAttempts, result => result.Succeeded);
            var replacement = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.Equal(30, replacement.Slot);
            Assert.Contains(30, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(30, "1000000001"));
            Assert.False((await repository.SetFingerprintSlotBlockedAsync(30, false)).Succeeded);
            await using (var marker = new MySqlCommand("""
                SELECT p.replacement_template_id = f.id
                FROM fingerprint_enrollment_preparations p
                INNER JOIN fingerprint_templates f ON f.template_identifier = p.template_identifier
                WHERE p.id = @id
                """, connection))
            {
                marker.Parameters.AddWithValue("@id", replacement.Id);
                Assert.True(Convert.ToBoolean(await marker.ExecuteScalarAsync()));
            }

            Task<OperationResult> AuthorizeReplacement(long adminId, long scannerId, long teacherId, string finger, long slot, string id) =>
                repository.AuthorizeFingerprintReplacementAsync(adminId, scannerId, teacherId, finger, slot, id);
            Assert.False((await AuthorizeReplacement(2, 1, 2, "Left index", 30, replacement.Id)).Succeeded);
            Assert.False((await AuthorizeReplacement(3, 2, 2, "Left index", 30, replacement.Id)).Succeeded);
            Assert.False((await AuthorizeReplacement(3, 1, 1, "Left index", 30, replacement.Id)).Succeeded);
            Assert.False((await AuthorizeReplacement(3, 1, 2, "Right index", 30, replacement.Id)).Succeeded);
            Assert.False((await AuthorizeReplacement(3, 1, 2, "Left index", 31, replacement.Id)).Succeeded);
            Assert.False((await AuthorizeReplacement(3, 1, 2, "Left index", 30, Guid.NewGuid().ToString("D"))).Succeeded);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 3");
            Assert.False((await AuthorizeReplacement(3, 1, 2, "Left index", 30, replacement.Id)).Succeeded);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 3");
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 30)");
            Assert.False((await AuthorizeReplacement(3, 1, 2, "Left index", 30, replacement.Id)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 30)");
            await ExecuteAsync(connection, "UPDATE biometric_devices SET pending_clear_fingerprints = 1 WHERE id = 1");
            Assert.False((await AuthorizeReplacement(3, 1, 2, "Left index", 30, replacement.Id)).Succeeded);
            await ExecuteAsync(connection, "UPDATE biometric_devices SET pending_clear_fingerprints = 0 WHERE id = 1");
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_preparations SET replacement_template_id = replacement_template_id + 10000");
            Assert.False((await AuthorizeReplacement(3, 1, 2, "Left index", 30, replacement.Id)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_preparations SET replacement_template_id = replacement_template_id - 10000");
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(connection, "DELETE FROM fingerprint_slot_blocks WHERE template_identifier = 30"));
            await ExecuteAsync(connection, """
                CREATE TRIGGER reject_consent BEFORE INSERT ON fingerprint_enrollment_authorizations
                FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated consent failure'
                """);
            await Assert.ThrowsAsync<MySqlException>(() => AuthorizeReplacement(3, 1, 2, "Left index", 30, replacement.Id));
            await ExecuteAsync(connection, "DROP TRIGGER reject_consent");
            await using (var missingConsent = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_enrollment_authorizations", connection))
                Assert.Equal(0L, Convert.ToInt64(await missingConsent.ExecuteScalarAsync()));
            Assert.Contains(30, await repository.GetBlockedFingerprintSlotsAsync());
            var authorizations = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => AuthorizeReplacement(3, 1, 2, "Left index", 30, replacement.Id)));
            Assert.Single(authorizations, result => result.Succeeded);
            string firstReplacementOperation;
            await using (var replacementConsent = new MySqlCommand("""
                SELECT operation_id, authorized_by, allow_occupied_slot,
                       TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(6), expires_utc)
                FROM fingerprint_enrollment_authorizations WHERE preparation_id = @id
                """, connection))
            {
                replacementConsent.Parameters.AddWithValue("@id", replacement.Id);
                await using var reader = await replacementConsent.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                firstReplacementOperation = reader.GetString(0);
                Assert.True(Guid.TryParseExact(firstReplacementOperation, "D", out var consentOperation) && consentOperation != Guid.Empty);
                Assert.Equal(3, reader.GetInt64(1));
                Assert.True(reader.GetBoolean(2));
                Assert.InRange(reader.GetInt32(3), 0, 300);
            }
            Assert.Null(await new FingerprintWriteGuardStore(configuration).GetAuthorizedOperationAsync("1000000001"));
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(30, "1000000001"));
            var replacementGuard = new FingerprintWriteGuardStore(configuration);
            Task<OperationResult> MarkReplacement(string prep, string device, string operation, ushort slot) =>
                replacementGuard.MarkReplacementUncertainAsync(prep, device, operation, slot);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_authorizations SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", firstReplacementOperation, 30)).Succeeded);
            Assert.True((await AuthorizeReplacement(3, 1, 2, "Left index", 30, replacement.Id)).Succeeded);
            string guardedOperation;
            await using (var renewedConsent = new MySqlCommand("SELECT operation_id FROM fingerprint_enrollment_authorizations WHERE preparation_id = @id", connection))
            {
                renewedConsent.Parameters.AddWithValue("@id", replacement.Id);
                guardedOperation = (string)(await renewedConsent.ExecuteScalarAsync())!;
                Assert.NotEqual(firstReplacementOperation, guardedOperation);
            }
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", firstReplacementOperation, 30)).Succeeded);
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", guardedOperation, 31)).Succeeded);
            Assert.False((await MarkReplacement(replacement.Id, "1000000002", guardedOperation, 30)).Succeeded);
            Assert.False((await MarkReplacement(Guid.NewGuid().ToString("D"), "1000000001", guardedOperation, 30)).Succeeded);
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", Guid.NewGuid().ToString("D"), 30)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_authorizations SET allow_occupied_slot = 0");
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", guardedOperation, 30)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_authorizations SET allow_occupied_slot = 1");
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 3");
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", guardedOperation, 30)).Succeeded);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 3");
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 2");
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", guardedOperation, 30)).Succeeded);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 2");
            await ExecuteAsync(connection, "UPDATE biometric_devices SET pending_clear_fingerprints = 1 WHERE id = 1");
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", guardedOperation, 30)).Succeeded);
            await ExecuteAsync(connection, "UPDATE biometric_devices SET pending_clear_fingerprints = 0 WHERE id = 1");
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 30)");
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", guardedOperation, 30)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 30)");
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_preparations SET replacement_template_id = replacement_template_id + 10000");
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", guardedOperation, 30)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_preparations SET replacement_template_id = replacement_template_id - 10000");
            await using (var priorCompletion = new MySqlCommand("""
                INSERT INTO fingerprint_enrollment_completions (operation_id, preparation_id, device_id, template_identifier)
                VALUES (@operation, @preparation, '1000000001', 30)
                """, connection))
            {
                priorCompletion.Parameters.AddWithValue("@operation", guardedOperation);
                priorCompletion.Parameters.AddWithValue("@preparation", Guid.NewGuid().ToString("D"));
                await priorCompletion.ExecuteNonQueryAsync();
            }
            Assert.False((await MarkReplacement(replacement.Id, "1000000001", guardedOperation, 30)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_completions");
            await ExecuteAsync(connection, """
                INSERT INTO fingerprint_sensor_inventory (device_id, capacity, bitmap, observed_after_utc)
                    VALUES (1, 8, X'00', UTC_TIMESTAMP(6));
                CREATE TRIGGER reject_consumption BEFORE DELETE ON fingerprint_enrollment_authorizations
                FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated consent consumption failure'
                """);
            var replacementInventoryChallenge = Assert.IsType<byte[]>((await new EnrollmentChallengeStore(configuration).IssueAsync("1000000001", "inventory")).Challenge);
            await Assert.ThrowsAsync<MySqlException>(() => MarkReplacement(replacement.Id, "1000000001", guardedOperation, 30));
            await ExecuteAsync(connection, "DROP TRIGGER reject_consumption");
            await using (var rollback = new MySqlCommand("""
                SELECT (SELECT COUNT(*) FROM fingerprint_enrollment_write_guards),
                       (SELECT COUNT(*) FROM fingerprint_enrollment_authorizations),
                       (SELECT COUNT(*) FROM fingerprint_sensor_inventory),
                       (SELECT COUNT(*) FROM fingerprint_enrollment_challenges WHERE operation_name = 'inventory')
                """, connection))
            await using (var reader = await rollback.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(1L, reader.GetInt64(2));
                Assert.Equal(1L, reader.GetInt64(3));
            }
            var recoveryIntent = new FingerprintReplacementRecoveryIntent(configuration);
            Task<OperationResult> RecordIntent(long adminId, long scannerId, long teacherId, string finger, long slot, string operation) =>
                recoveryIntent.RecordAsync(adminId, scannerId, teacherId, finger, slot, replacement.Id, operation);
            Assert.False((await RecordIntent(3, 1, 2, "Left index", 30, guardedOperation)).Succeeded);
            var guardResults = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => MarkReplacement(replacement.Id, "1000000001", guardedOperation, 30)));
            Assert.Single(guardResults, result => result.Succeeded);
            await using (var guarded = new MySqlCommand("""
                SELECT (SELECT COUNT(*) FROM fingerprint_enrollment_write_guards WHERE operation_id = @operation),
                       (SELECT COUNT(*) FROM fingerprint_enrollment_authorizations),
                       (SELECT COUNT(*) FROM fingerprint_sensor_inventory),
                       (SELECT COUNT(*) FROM fingerprint_enrollment_challenges WHERE challenge_hash = @challenge)
                """, connection))
            {
                guarded.Parameters.AddWithValue("@operation", guardedOperation);
                guarded.Parameters.AddWithValue("@challenge", SHA256.HashData(replacementInventoryChallenge));
                await using var reader = await guarded.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(0L, reader.GetInt64(1));
                Assert.Equal(0L, reader.GetInt64(2));
                Assert.Equal(0L, reader.GetInt64(3));
            }
            Assert.False((await replacementGuard.FinalizeConfirmedWriteAsync(replacement.Id, "1000000001", guardedOperation)).Succeeded);
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(30, "1000000001"));
            Assert.Contains(30, await repository.GetBlockedFingerprintSlotsAsync());
            await using (var mapping = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_templates WHERE user_account_id = 2 AND template_identifier = 30", connection))
            {
                Assert.Equal(1L, Convert.ToInt64(await mapping.ExecuteScalarAsync()));
            }
            Assert.False((await AuthorizeReplacement(3, 1, 2, "Left index", 30, replacement.Id)).Succeeded);
            Assert.False((await RecordIntent(2, 1, 2, "Left index", 30, guardedOperation)).Succeeded);
            Assert.False((await RecordIntent(3, 2, 2, "Left index", 30, guardedOperation)).Succeeded);
            Assert.False((await RecordIntent(3, 1, 1, "Left index", 30, guardedOperation)).Succeeded);
            Assert.False((await RecordIntent(3, 1, 2, "Right index", 30, guardedOperation)).Succeeded);
            Assert.False((await RecordIntent(3, 1, 2, "Left index", 31, guardedOperation)).Succeeded);
            Assert.False((await RecordIntent(3, 1, 2, "Left index", 30, Guid.NewGuid().ToString("D"))).Succeeded);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 3");
            Assert.False((await RecordIntent(3, 1, 2, "Left index", 30, guardedOperation)).Succeeded);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 3");
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 30)");
            Assert.False((await RecordIntent(3, 1, 2, "Left index", 30, guardedOperation)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 30)");
            var intents = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => RecordIntent(3, 1, 2, "Left index", 30, guardedOperation)));
            Assert.Single(intents, result => result.Succeeded);
            string firstRecoveryOperation;
            await using (var readIntent = new MySqlCommand("""
                SELECT guarded_operation_id, recovery_operation_id, authorized_by,
                       TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(6), expires_utc)
                FROM fingerprint_replacement_recovery_intents WHERE preparation_id = @id
                """, connection))
            {
                readIntent.Parameters.AddWithValue("@id", replacement.Id);
                await using var reader = await readIntent.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(guardedOperation, reader.GetString(0));
                firstRecoveryOperation = reader.GetString(1);
                Assert.True(Guid.TryParseExact(firstRecoveryOperation, "D", out var recoveryId) && recoveryId != Guid.Empty);
                Assert.NotEqual(guardedOperation, firstRecoveryOperation);
                Assert.Equal(3, reader.GetInt64(2));
                Assert.InRange(reader.GetInt32(3), 0, 300);
            }
            Assert.False((await repository.CancelFingerprintEnrollmentPreparationAsync(replacement.Id)).Succeeded);
            Assert.False((await repository.SetFingerprintSlotBlockedAsync(30, false)).Succeeded);
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(30, "1000000001"));
            await ExecuteAsync(connection, "UPDATE fingerprint_replacement_recovery_intents SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            Assert.True((await RecordIntent(3, 1, 2, "Left index", 30, guardedOperation)).Succeeded);
            await using (var renewed = new MySqlCommand("SELECT recovery_operation_id FROM fingerprint_replacement_recovery_intents WHERE preparation_id = @id", connection))
            {
                renewed.Parameters.AddWithValue("@id", replacement.Id);
                Assert.NotEqual(firstRecoveryOperation, (string)(await renewed.ExecuteScalarAsync())!);
            }
            await ExecuteAsync(connection, "UPDATE fingerprint_replacement_recovery_intents SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            await ExecuteAsync(connection, "DELETE FROM fingerprint_templates WHERE template_identifier = 30");
            Assert.False((await RecordIntent(3, 1, 2, "Left index", 30, guardedOperation)).Succeeded);
            Assert.Contains(30, await repository.GetBlockedFingerprintSlotsAsync());
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_write_guards");
            await using (var countIntents = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_replacement_recovery_intents", connection))
                Assert.Equal(0L, Convert.ToInt64(await countIntents.ExecuteScalarAsync()));

            // Even if the original mapping disappears and old consent is inserted,
            // a replacement preparation must never become an ordinary enrollment.
            var replacementOperation = Guid.NewGuid().ToString("D");
            await using (var injectConsent = new MySqlCommand("""
                INSERT INTO fingerprint_enrollment_authorizations
                    (preparation_id, operation_id, authorized_by, allow_occupied_slot, created_utc, expires_utc)
                VALUES (@id, @operation, 3, 0, UTC_TIMESTAMP(6), DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 5 MINUTE))
                """, connection))
            {
                injectConsent.Parameters.AddWithValue("@id", replacement.Id);
                injectConsent.Parameters.AddWithValue("@operation", replacementOperation);
                await injectConsent.ExecuteNonQueryAsync();
            }
            var replacementGuards = new FingerprintWriteGuardStore(configuration);
            Assert.False((await repository.AuthorizeFingerprintEnrollmentAsync(3, replacement.Id, false)).Succeeded);
            Assert.Null(await replacementGuards.GetAuthorizedOperationAsync("1000000001"));
            Assert.False((await replacementGuards.MarkUncertainAsync(replacement.Id, "1000000001", replacementOperation)).Succeeded);
            await using (var injectGuard = new MySqlCommand("""
                INSERT INTO fingerprint_enrollment_write_guards (preparation_id, operation_id) VALUES (@id, @operation)
                """, connection))
            {
                injectGuard.Parameters.AddWithValue("@id", replacement.Id);
                injectGuard.Parameters.AddWithValue("@operation", replacementOperation);
                await injectGuard.ExecuteNonQueryAsync();
            }
            Assert.False((await replacementGuards.FinalizeConfirmedWriteAsync(replacement.Id, "1000000001", replacementOperation)).Succeeded);
            Assert.False((await repository.CancelFingerprintEnrollmentPreparationAsync(replacement.Id)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_write_guards");
            Assert.True((await repository.CancelFingerprintEnrollmentPreparationAsync(replacement.Id)).Succeeded);
            Assert.Contains(30, await repository.GetBlockedFingerprintSlotsAsync());
            await ExecuteAsync(connection, """
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                VALUES (2, 'Left index', 30);
                INSERT INTO fingerprint_template_device_bindings (fingerprint_template_id, device_id)
                SELECT id, '1000000001' FROM fingerprint_templates WHERE template_identifier = 30;
                """);
            Assert.False((await repository.PrepareFingerprintReplacementAsync(1, 2, "Left index", 30)).Succeeded);
            Assert.True((await repository.SetFingerprintSlotBlockedAsync(30, false)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_templates WHERE template_identifier = 30");

            Assert.False((await repository.PrepareFingerprintEnrollmentAutomaticallyAsync(1, "Left index")).Succeeded);
            await ExecuteAsync(connection, "INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier) VALUES (1, 'Left thumb', 10);");
            Assert.False((await repository.PrepareFingerprintEnrollmentAsync(1, "Left index", 12)).Succeeded);
            await ExecuteAsync(connection, "INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier) VALUES (1, 'Right index', 11);");
            Assert.False((await repository.PrepareFingerprintEnrollmentAsync(1, "Left index", 12)).Succeeded);
            Assert.True((await CheckReplacement(1, 1, "Right thumb", 2)).Succeeded);
            Assert.True((await repository.PrepareFingerprintReplacementAsync(1, 1, "Right thumb", 2)).Succeeded);
            var overLimitReplacement = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(2, "1000000001"));
            Assert.True((await repository.CancelFingerprintEnrollmentPreparationAsync(overLimitReplacement.Id)).Succeeded);
            Assert.Contains(2, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.True((await repository.SetFingerprintSlotBlockedAsync(2, false)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_template_device_bindings");
            var existingFingerprints = await repository.GetTeacherFingerprintsAsync(1);
            Assert.Equal(3, existingFingerprints.Count);
            Assert.Contains(existingFingerprints, item => item.FingerPosition == "Right thumb" && item.Slot == 2 && item.DeviceId is null && !item.IsBlocked);
            Assert.Empty(await repository.GetTeacherFingerprintsAsync(2));
            await using (var count = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_templates WHERE user_account_id = 1", connection))
                Assert.Equal(3L, Convert.ToInt64(await count.ExecuteScalarAsync()));
            Assert.DoesNotContain(12, await repository.GetBlockedFingerprintSlotsAsync());
            await ExecuteAsync(connection, "DELETE FROM fingerprint_templates WHERE template_identifier IN (10, 11)");
            Assert.False((await repository.PrepareFingerprintEnrollmentAsync(2, "Left thumb", 2)).Succeeded);
            Assert.False((await repository.PrepareFingerprintEnrollmentAsync(1, "Right thumb", 3)).Succeeded);
            Assert.DoesNotContain(3, await repository.GetBlockedFingerprintSlotsAsync());

            foreach (var statement in new[]
            {
                "UPDATE biometric_devices SET paired_at = NULL WHERE id = 1",
                "UPDATE biometric_devices SET paired_at = CURRENT_TIMESTAMP, classroom_id = NULL WHERE id = 1",
                "UPDATE biometric_devices SET classroom_id = 1, pending_clear_fingerprints = 1 WHERE id = 1",
                "UPDATE biometric_devices SET pending_clear_fingerprints = 0, fingerprint_enrollment_enabled = 0 WHERE id = 1",
                "UPDATE biometric_devices SET fingerprint_enrollment_enabled = 1, is_active = 0 WHERE id = 1",
                "UPDATE biometric_devices SET is_active = 1 WHERE id = 1; UPDATE classrooms SET is_active = 0 WHERE id = 1"
            })
            {
                await ExecuteAsync(connection, statement);
                Assert.False((await repository.PrepareFingerprintEnrollmentAsync(2, "Left thumb", 4)).Succeeded);
            }
            await ExecuteAsync(connection, "UPDATE classrooms SET is_active = 1 WHERE id = 1");
            Assert.DoesNotContain(4, await repository.GetBlockedFingerprintSlotsAsync());

            var attempts = await Task.WhenAll(
                repository.PrepareFingerprintEnrollmentAsync(1, "Left thumb", 5),
                repository.PrepareFingerprintEnrollmentAsync(2, "Left thumb", 6));
            Assert.Single(attempts, attempt => attempt.Succeeded);
            var preparation = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.Contains(preparation.Slot, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.False((await repository.SetFingerprintSlotBlockedAsync(preparation.Slot, false)).Succeeded);
            Assert.False((await repository.AuthorizeFingerprintEnrollmentAsync(1, preparation.Id, true)).Succeeded);
            Assert.False((await repository.AuthorizeFingerprintEnrollmentAsync(3, "bad", true)).Succeeded);
            var consentAttempts = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => repository.AuthorizeFingerprintEnrollmentAsync(3, preparation.Id, false)));
            Assert.Single(consentAttempts, result => result.Succeeded);
            var consent = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.True(consent.HasActiveAuthorization);
            Assert.False(consent.AllowsOccupiedSlot);
            var writeGuards = new FingerprintWriteGuardStore(configuration);
            var authorizedOperation = await writeGuards.GetAuthorizedOperationAsync("1000000001");
            Assert.NotNull(authorizedOperation);
            Assert.Equal(preparation.Id, authorizedOperation.PreparationId);
            Assert.Equal(preparation.Slot, authorizedOperation.Slot);
            Assert.Null(await writeGuards.GetAuthorizedOperationAsync("1000000002"));
            string? consentOperationId = null;
            await using (var inspectConsent = new MySqlCommand("""
                SELECT operation_id, authorized_by, TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(6), expires_utc)
                FROM fingerprint_enrollment_authorizations WHERE preparation_id = @id
                """, connection))
            {
                inspectConsent.Parameters.AddWithValue("@id", preparation.Id);
                await using var reader = await inspectConsent.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                consentOperationId = reader.GetString(0);
                Assert.True(Guid.TryParseExact(consentOperationId, "D", out var consentOperation) && consentOperation != Guid.Empty);
                Assert.Equal(3, reader.GetInt64(1));
                Assert.InRange(reader.GetInt32(2), 0, 300);
            }
            Assert.Equal(consentOperationId, authorizedOperation.OperationId);
            Assert.False((await repository.AuthorizeFingerprintEnrollmentAsync(3, preparation.Id, true)).Succeeded);
            Assert.True((await repository.RevokeFingerprintEnrollmentAuthorizationAsync(preparation.Id)).Succeeded);
            Assert.False((await repository.RevokeFingerprintEnrollmentAuthorizationAsync(preparation.Id)).Succeeded);
            Assert.False((await repository.AuthorizeFingerprintEnrollmentAsync(3, preparation.Id, true)).Succeeded);
            Assert.True((await repository.AuthorizeFingerprintEnrollmentAsync(3, preparation.Id, false)).Succeeded);
            consent = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.True(consent.HasActiveAuthorization && !consent.AllowsOccupiedSlot);
            Assert.NotNull(await writeGuards.GetAuthorizedOperationAsync("1000000001"));
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_authorizations SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            Assert.False(Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync()).HasActiveAuthorization);
            Assert.Null(await writeGuards.GetAuthorizedOperationAsync("1000000001"));
            Assert.True((await repository.AuthorizeFingerprintEnrollmentAsync(3, preparation.Id, false)).Succeeded);
            Assert.False(Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync()).AllowsOccupiedSlot);
            Assert.NotNull(await writeGuards.GetAuthorizedOperationAsync("1000000001"));
            Assert.True((await repository.CancelFingerprintEnrollmentPreparationAsync(preparation.Id)).Succeeded);
            Assert.Null(await writeGuards.GetAuthorizedOperationAsync("1000000001"));
            Assert.Contains(preparation.Slot, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.Empty(await repository.GetFingerprintEnrollmentPreparationsAsync());
            await using (var remainingConsent = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_enrollment_authorizations", connection))
                Assert.Equal(0L, Convert.ToInt64(await remainingConsent.ExecuteScalarAsync()));
            Assert.Equal(1, (await repository.FindTeacherByFingerprintTemplateIdAsync(2, "1000000001"))?.TeacherAccountId);

            Assert.True((await repository.PrepareFingerprintEnrollmentAsync(2, "Left index", 7)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM user_accounts WHERE id = 2");
            Assert.Empty(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.Contains(7, await repository.GetBlockedFingerprintSlotsAsync());

            var challenges = new EnrollmentChallengeStore(configuration);
            var key = RandomNumberGenerator.GetBytes(32);
            var master = RandomNumberGenerator.GetBytes(32);
            var credentials = new EnrollmentDeviceCredentialStore(configuration);
            Assert.False((await credentials.StoreNewAsync("1000000004", key, master)).Succeeded);
            Assert.True((await credentials.StoreNewAsync("1000000001", key, master)).Succeeded);
            await Assert.ThrowsAnyAsync<CryptographicException>(() =>
                credentials.StoreNewAsync("1000000002", key, RandomNumberGenerator.GetBytes(32)));
            Assert.False((await credentials.StoreNewAsync("1000000001", RandomNumberGenerator.GetBytes(32), master)).Succeeded);
            Assert.False((await credentials.StoreNewAsync("1000000002", key, master)).Succeeded);
            var keyTwo = RandomNumberGenerator.GetBytes(32);
            Assert.True((await credentials.StoreNewAsync("1000000002", keyTwo, master)).Succeeded);
            Assert.Equal(key, await credentials.LoadAsync("1000000001", master));
            Assert.Equal(keyTwo, await credentials.LoadAsync("1000000002", master));
            configuration["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master);
            var inventory = new FingerprintSensorInventory(configuration);
            var inventoryChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "inventory")).Challenge);
            var inventoryBitmap = "0c" + new string('0', 74);
            var inventoryBody = Encoding.ASCII.GetBytes($"300\n{inventoryBitmap}\n");
            var inventoryTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "inventory", inventoryChallenge, inventoryBody);
            var inventoryRequest = new FingerprintInventoryRequest("1000000001", 300, inventoryBitmap,
                Convert.ToHexString(inventoryChallenge).ToLowerInvariant(), Convert.ToHexString(inventoryTag).ToLowerInvariant());
            Assert.False(await inventory.RecordAsync(inventoryRequest with { Bitmap = "00" }));
            Assert.False(await inventory.RecordAsync(inventoryRequest with { Capacity = 2049 }));
            Assert.False(await inventory.RecordAsync(inventoryRequest with { Tag = new string('0', 64) }));
            Assert.True(await inventory.RecordAsync(inventoryRequest));
            Assert.False(await inventory.RecordAsync(inventoryRequest));
            var oldChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "inventory")).Challenge);
            await using (var ageChallenge = new MySqlCommand("UPDATE fingerprint_enrollment_challenges SET expires_utc = DATE_SUB(expires_utc, INTERVAL 10 SECOND) WHERE challenge_hash = @hash", connection))
            {
                ageChallenge.Parameters.AddWithValue("@hash", SHA256.HashData(oldChallenge));
                await ageChallenge.ExecuteNonQueryAsync();
            }
            var oldTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "inventory", oldChallenge, inventoryBody);
            Assert.False(await inventory.RecordAsync(inventoryRequest with {
                Challenge = Convert.ToHexString(oldChallenge).ToLowerInvariant(),
                Tag = Convert.ToHexString(oldTag).ToLowerInvariant()
            }));
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_challenges WHERE operation_name = 'inventory'");
            await using (var snapshot = new MySqlCommand("SELECT capacity, bitmap FROM fingerprint_sensor_inventory WHERE device_id = 1", connection))
            await using (var snapshotReader = await snapshot.ExecuteReaderAsync())
            {
                Assert.True(await snapshotReader.ReadAsync());
                Assert.Equal(300, snapshotReader.GetInt32(0));
                Assert.Equal(Convert.FromHexString(inventoryBitmap), (byte[])snapshotReader.GetValue(1));
            }
            await ExecuteAsync(connection, "INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active) VALUES (5, 'five', 'Teacher Five', 'hash', 'Teacher', 1); INSERT INTO fingerprint_slot_blocks (template_identifier) VALUES (1)");
            var automaticAttempts = await Task.WhenAll(
                repository.PrepareFingerprintEnrollmentAutomaticallyAsync(1, "Left index"),
                repository.PrepareFingerprintEnrollmentAutomaticallyAsync(5, "Left index"));
            Assert.Single(automaticAttempts, result => result.Succeeded);
            var automaticPreparation = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.Equal(4, automaticPreparation.Slot);
            Assert.Contains(4, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.True((await repository.CancelFingerprintEnrollmentPreparationAsync(automaticPreparation.Id)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_slot_blocks WHERE template_identifier IN (1, 4)");
            await ExecuteAsync(connection, "UPDATE fingerprint_sensor_inventory SET observed_after_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 91 SECOND)");
            Assert.False((await repository.PrepareFingerprintEnrollmentAutomaticallyAsync(1, "Left index")).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_sensor_inventory SET observed_after_utc = UTC_TIMESTAMP(6), capacity = 300, bitmap = UNHEX('00')");
            Assert.False((await repository.PrepareFingerprintEnrollmentAutomaticallyAsync(1, "Left index")).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_sensor_inventory SET observed_after_utc = UTC_TIMESTAMP(6), capacity = 4, bitmap = UNHEX('0E')");
            Assert.False((await repository.PrepareFingerprintEnrollmentAutomaticallyAsync(1, "Left index")).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_sensor_inventory");
            configuration["EnrollmentSecurity:MasterKey"] = null;
            var localProvisioning = new EnrollmentLocalProvisioning(configuration);
            Assert.Equal(key, await localProvisioning.GetOrCreateKeyAsync("1000000001", master));
            var concurrentlyCreated = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
                localProvisioning.GetOrCreateKeyAsync("1000000003", master)));
            Assert.All(concurrentlyCreated, value => Assert.Equal(concurrentlyCreated[0], value));
            Assert.NotEqual(key, concurrentlyCreated[0]);
            var localStart = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            localStart.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SmartClassAC.dll"));
            localStart.ArgumentList.Add("--enrollment-local-key");
            localStart.ArgumentList.Add("1000000001");
            localStart.Environment["ConnectionStrings__SmartClassMariaDb"] = builder.ConnectionString;
            localStart.Environment["EnrollmentSecurity__MasterKey"] = Convert.ToBase64String(master);
            using (var child = Process.Start(localStart)!)
            using (var output = new MemoryStream())
            {
                await child.StandardOutput.BaseStream.CopyToAsync(output);
                var errors = await child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync();
                Assert.True(child.ExitCode == 0, errors);
                Assert.Equal(key, output.ToArray());
            }
            await Assert.ThrowsAnyAsync<CryptographicException>(() => credentials.LoadAsync("1000000001", RandomNumberGenerator.GetBytes(32)));
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_master_key_check WHERE singleton_id = 1"));
            await using (var inspect = new MySqlCommand("SELECT protected_key FROM fingerprint_enrollment_device_credentials WHERE device_id = 1", connection))
            {
                var storedKey = (byte[])(await inspect.ExecuteScalarAsync())!;
                Assert.Equal(61, storedKey.Length);
                Assert.DoesNotContain(Convert.ToHexString(key), Convert.ToHexString(storedKey));
            }
            var body = Encoding.UTF8.GetBytes("{\"slot\":7}");
            var authenticator = new EnrollmentDeviceAuthenticator(configuration);
            var authChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "claim")).Challenge);
            var authTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "claim", authChallenge, body);
            Assert.False(await authenticator.VerifyAndConsumeAsync("1000000002", "claim", master, authChallenge, body, authTag));
            Assert.True(await authenticator.VerifyAndConsumeAsync("1000000001", "claim", master, authChallenge, body, authTag));
            Assert.False(await authenticator.VerifyAndConsumeAsync("1000000001", "claim", master, authChallenge, body, authTag));
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_device_credentials SET protected_key = (SELECT protected_key FROM (SELECT protected_key FROM fingerprint_enrollment_device_credentials WHERE device_id = 1) AS source_key) WHERE device_id = 2");
            await Assert.ThrowsAnyAsync<CryptographicException>(() => credentials.LoadAsync("1000000002", master));
            var challenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "claim")).Challenge);
            var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "claim", challenge, body);
            await using (var stored = new MySqlCommand("SELECT challenge_hash, TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(6), expires_utc) FROM fingerprint_enrollment_challenges", connection))
            await using (var reader = await stored.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(SHA256.HashData(challenge), (byte[])reader.GetValue(0));
                Assert.InRange(reader.GetInt32(1), 0, 120);
            }
            var badTag = tag.ToArray();
            badTag[0] ^= 1;
            Assert.False(await challenges.VerifyAndConsumeAsync("1000000001", "claim", key, challenge, body, badTag));
            Assert.False(await challenges.VerifyAndConsumeAsync("1000000001", "claim", RandomNumberGenerator.GetBytes(32), challenge, body, tag));
            Assert.False(await challenges.VerifyAndConsumeAsync("1000000001", "claim", key, challenge.AsSpan(1).ToArray(), body, tag));
            Assert.False(await challenges.VerifyAndConsumeAsync("1000000001", "claim", key, challenge, Encoding.UTF8.GetBytes("{}"), tag));
            Assert.False(await challenges.VerifyAndConsumeAsync("1000000001", "finish", key, challenge, body,
                EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "finish", challenge, body)));
            Assert.False(await challenges.VerifyAndConsumeAsync("1000000002", "claim", key, challenge, body,
                EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000002", "claim", challenge, body)));
            var restartedStore = new EnrollmentChallengeStore(configuration);
            var checks = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                restartedStore.VerifyAndConsumeAsync("1000000001", "claim", key, challenge, body, tag)));
            Assert.Single(checks, result => result);
            Assert.False(await challenges.VerifyAndConsumeAsync("1000000001", "claim", key, challenge, body, tag));

            var expired = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "claim")).Challenge);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_challenges SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            Assert.False(await challenges.VerifyAndConsumeAsync("1000000001", "claim", key, expired, body,
                EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "claim", expired, body)));
            var valid = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "claim")).Challenge);
            await ExecuteAsync(connection, "UPDATE biometric_devices SET is_active = 0 WHERE id = 1");
            Assert.Null(await credentials.LoadAsync("1000000001", master));
            Assert.False(await challenges.VerifyAndConsumeAsync("1000000001", "claim", key, valid, body,
                EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "claim", valid, body)));
            Assert.NotNull((await challenges.IssueAsync("1000000001", "claim")).Error);
            Assert.NotNull((await challenges.IssueAsync("1000000004", "claim")).Error);
            await ExecuteAsync(connection, "UPDATE biometric_devices SET is_active = 1 WHERE id = 1");
            var issued = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => challenges.IssueAsync("1000000001", "claim")));
            Assert.Equal(15, issued.Count(result => result.Challenge is not null));
            Assert.Equal(5, issued.Count(result => result.Error is not null));

            Assert.True((await repository.PrepareFingerprintEnrollmentAsync(1, "Left index", 9)).Succeeded);
            var guardedPreparation = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.True((await repository.AuthorizeFingerprintEnrollmentAsync(3, guardedPreparation.Id, false)).Succeeded);
            var guard = new FingerprintWriteGuardStore(configuration);
            var operation = Guid.NewGuid().ToString("D");
            Assert.False((await guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", Guid.Empty.ToString("D"))).Succeeded);
            Assert.False((await guard.MarkUncertainAsync(guardedPreparation.Id, "1000000002", operation)).Succeeded);
            await ExecuteAsync(connection, "UPDATE biometric_devices SET fingerprint_enrollment_enabled = 0 WHERE id = 1");
            Assert.False((await guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", operation)).Succeeded);
            await ExecuteAsync(connection, "UPDATE biometric_devices SET fingerprint_enrollment_enabled = 1 WHERE id = 1");
            await ExecuteAsync(connection, "INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier) VALUES (1, 'Left index', 10)");
            Assert.False((await guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", operation)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_templates WHERE template_identifier = 10");
            var deactivateRace = repository.SetTeacherActiveAsync(1, false);
            var teacherGuardRace = guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", operation);
            await Task.WhenAll(deactivateRace, teacherGuardRace);
            Assert.NotEqual(deactivateRace.Result.Succeeded, teacherGuardRace.Result.Succeeded);
            if (deactivateRace.Result.Succeeded)
                Assert.True((await repository.SetTeacherActiveAsync(1, true)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_write_guards");
            var roomRace = repository.UpdateClassroomAsync(new UpdateClassroomRequest(1, "Room", 30, 24, false));
            var roomGuardRace = guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", operation);
            await Task.WhenAll(roomRace, roomGuardRace);
            Assert.NotEqual(roomRace.Result.Succeeded, roomGuardRace.Result.Succeeded);
            if (roomRace.Result.Succeeded)
                Assert.True((await repository.UpdateClassroomAsync(new UpdateClassroomRequest(1, "Room", 30, 24, true))).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_write_guards");
            var deviceRace = repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, null, null, 30, true, true, true));
            var deviceGuardRace = guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", operation);
            await Task.WhenAll(deviceRace, deviceGuardRace);
            Assert.NotEqual(deviceRace.Result.Succeeded, deviceGuardRace.Result.Succeeded);
            if (deviceRace.Result.Succeeded)
                Assert.True((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 1, 24, 30, true, true, true))).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_write_guards");
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_challenges");
            configuration["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master);
            var beforeWriteChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "inventory")).Challenge);
            await ExecuteAsync(connection, "INSERT INTO fingerprint_sensor_inventory (device_id, capacity, bitmap, observed_after_utc) VALUES (1, 8, UNHEX('0C'), UTC_TIMESTAMP(6))");
            Assert.True((await guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", operation)).Succeeded);
            await using (var snapshotCount = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_sensor_inventory", connection))
                Assert.Equal(0L, Convert.ToInt64(await snapshotCount.ExecuteScalarAsync()));
            var beforeWriteTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "inventory", beforeWriteChallenge, inventoryBody);
            Assert.False(await inventory.RecordAsync(inventoryRequest with {
                Challenge = Convert.ToHexString(beforeWriteChallenge).ToLowerInvariant(),
                Tag = Convert.ToHexString(beforeWriteTag).ToLowerInvariant()
            }));
            var duringWriteChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "inventory")).Challenge);
            var duringWriteTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "inventory", duringWriteChallenge, inventoryBody);
            Assert.False(await inventory.RecordAsync(inventoryRequest with {
                Challenge = Convert.ToHexString(duringWriteChallenge).ToLowerInvariant(),
                Tag = Convert.ToHexString(duringWriteTag).ToLowerInvariant()
            }));
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_write_guards; DELETE FROM fingerprint_enrollment_challenges");
            configuration["EnrollmentSecurity:MasterKey"] = null;
            var selectionRace = repository.SetEnrollmentScannerAsync(2);
            var markingRace = guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", operation);
            await Task.WhenAll(selectionRace, markingRace);
            Assert.NotEqual(selectionRace.Result.Succeeded, markingRace.Result.Succeeded);
            if (selectionRace.Result.Succeeded)
                Assert.True((await repository.SetEnrollmentScannerAsync(1)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_write_guards");
            var guardAttempt = guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", operation);
            var clearAttempt = repository.RequestClearAllFingerprintDevicesAsync();
            await Task.WhenAll(guardAttempt, clearAttempt);
            Assert.NotEqual(guardAttempt.Result.Succeeded, clearAttempt.Result.Succeeded);
            if (clearAttempt.Result.Succeeded)
            {
                await ExecuteAsync(connection, """
                    UPDATE biometric_devices SET pending_clear_fingerprints = 0;
                    INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier) VALUES (1, 'Right thumb', 2);
                    """);
            }
            var marking = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", operation)));
            Assert.All(marking, result => Assert.True(result.Succeeded));
            Assert.False((await guard.MarkUncertainAsync(guardedPreparation.Id, "1000000001", Guid.NewGuid().ToString("D"))).Succeeded);
            Assert.True((await new FingerprintWriteGuardStore(configuration).MarkUncertainAsync(guardedPreparation.Id, "1000000001", operation)).Succeeded);
            Assert.False((await guard.FinalizeConfirmedWriteAsync(guardedPreparation.Id, "1000000002", operation)).Succeeded);
            Assert.False((await guard.FinalizeConfirmedWriteAsync(guardedPreparation.Id, "1000000001", Guid.NewGuid().ToString("D"))).Succeeded);
            Assert.False((await repository.CancelFingerprintEnrollmentPreparationAsync(guardedPreparation.Id)).Succeeded);
            Assert.False((await repository.AuthorizeFingerprintEnrollmentAsync(3, guardedPreparation.Id, true)).Succeeded);
            Assert.False((await repository.RevokeFingerprintEnrollmentAuthorizationAsync(guardedPreparation.Id)).Succeeded);
            Assert.False((await repository.SetFingerprintSlotBlockedAsync(9, false)).Succeeded);
            Assert.False((await repository.RequestClearAllFingerprintDevicesAsync()).Succeeded);
            Assert.False((await repository.ResetSystemToFirstAccessAsync(new DefaultAdminOptions { Username = "admin" }, "hash")).Succeeded);
            Assert.False((await repository.DeleteTeacherAsync(1)).Succeeded);
            Assert.False((await repository.DeleteBiometricDeviceAsync(1)).Succeeded);
            Assert.False((await repository.SetTeacherActiveAsync(1, false)).Succeeded);
            Assert.False((await repository.UpdateClassroomAsync(new UpdateClassroomRequest(1, "Room", 30, 24, false))).Succeeded);
            Assert.False((await repository.DeleteClassroomAsync(1)).Succeeded);
            Assert.False((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, null, null, 30, true, true, true))).Succeeded);
            Assert.False((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 1, 24, 30, false, true, true))).Succeeded);
            Assert.False((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 1, 24, 30, true, false, true))).Succeeded);
            Assert.True((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 1, 24, 30, true, true, true))).Succeeded);
            Assert.True((await repository.UpdateClassroomAsync(new UpdateClassroomRequest(1, "Room", 30, 24, true))).Succeeded);
            Assert.True((await repository.SetEnrollmentScannerAsync(1)).Succeeded);
            Assert.False((await repository.SetEnrollmentScannerAsync(2)).Succeeded);
            Assert.False((await repository.SetEnrollmentScannerAsync(null)).Succeeded);
            Assert.Equal(1, (await repository.FindTeacherByFingerprintTemplateIdAsync(2, "1000000001"))?.TeacherAccountId);
            Assert.Contains(9, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.True(Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync()).HasUnresolvedWrite);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_scanner SET device_id = 2 WHERE singleton_id = 1");
            Assert.False((await guard.FinalizeConfirmedWriteAsync(guardedPreparation.Id, "1000000001", operation)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_scanner SET device_id = 1 WHERE singleton_id = 1");
            await ExecuteAsync(connection, "INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier) VALUES (1, 'Left index', 10)");
            Assert.False((await guard.FinalizeConfirmedWriteAsync(guardedPreparation.Id, "1000000001", operation)).Succeeded);
            Assert.Contains(9, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(9, "1000000001"));
            await ExecuteAsync(connection, "DELETE FROM fingerprint_templates WHERE template_identifier = 10");
            await ExecuteAsync(connection, """
                CREATE TRIGGER reject_binding BEFORE INSERT ON fingerprint_template_device_bindings
                FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated failure'
                """);
            await Assert.ThrowsAsync<MySqlException>(() => guard.FinalizeConfirmedWriteAsync(guardedPreparation.Id, "1000000001", operation));
            await ExecuteAsync(connection, "DROP TRIGGER reject_binding");
            Assert.Contains(9, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(9, "1000000001"));
            Assert.True(Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync()).HasUnresolvedWrite);

            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_challenges");
            var finishChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "finish")).Challenge);
            byte[] FinishTag(byte[] nonce, ushort slot, byte[] signingKey) =>
                EnrollmentMessageAuthentication.ComputeTag(signingKey, false, "1000000001", "finish", nonce,
                    Encoding.ASCII.GetBytes($"{guardedPreparation.Id}\n{operation}\n{slot}\nconfirmed\n"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => guard.FinalizeAuthenticatedWriteAsync(
                guardedPreparation.Id, "1000000001", operation, 9, finishChallenge, FinishTag(finishChallenge, 9, key)));
            configuration["EnrollmentSecurity:MasterKey"] = "not-base64";
            await Assert.ThrowsAsync<InvalidOperationException>(() => guard.FinalizeAuthenticatedWriteAsync(
                guardedPreparation.Id, "1000000001", operation, 9, finishChallenge, FinishTag(finishChallenge, 9, key)));
            configuration["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await Assert.ThrowsAsync<CryptographicException>(() => guard.FinalizeAuthenticatedWriteAsync(
                guardedPreparation.Id, "1000000001", operation, 9, finishChallenge, FinishTag(finishChallenge, 9, key)));
            configuration["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master);
            Assert.False((await guard.FinalizeAuthenticatedWriteAsync(guardedPreparation.Id, "1000000001", operation, 9,
                finishChallenge, FinishTag(finishChallenge, 9, keyTwo))).Succeeded);
            Assert.False((await guard.FinalizeAuthenticatedWriteAsync(guardedPreparation.Id, "1000000003", operation, 9,
                finishChallenge, FinishTag(finishChallenge, 9, key))).Succeeded);
            Assert.False((await guard.FinalizeAuthenticatedWriteAsync(guardedPreparation.Id, "1000000001", operation, 10,
                finishChallenge, FinishTag(finishChallenge, 10, key))).Succeeded);
            Assert.Contains(9, await repository.GetBlockedFingerprintSlotsAsync());

            await ExecuteAsync(connection, """
                CREATE TRIGGER reject_binding BEFORE INSERT ON fingerprint_template_device_bindings
                FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated failure'
                """);
            await Assert.ThrowsAsync<MySqlException>(() => guard.FinalizeAuthenticatedWriteAsync(
                guardedPreparation.Id, "1000000001", operation, 9, finishChallenge, FinishTag(finishChallenge, 9, key)));
            await ExecuteAsync(connection, "DROP TRIGGER reject_binding");
            Assert.True((await guard.FinalizeAuthenticatedWriteAsync(
                guardedPreparation.Id, "1000000001", operation, 9, finishChallenge, FinishTag(finishChallenge, 9, key))).Succeeded);
            Assert.False((await guard.FinalizeAuthenticatedWriteAsync(
                guardedPreparation.Id, "1000000001", operation, 9, finishChallenge, FinishTag(finishChallenge, 9, key))).Succeeded);
            var retryChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "finish")).Challenge);
            Assert.False((await guard.FinalizeAuthenticatedWriteAsync(
                guardedPreparation.Id, "1000000001", operation, 10, retryChallenge, FinishTag(retryChallenge, 10, key))).Succeeded);
            Assert.True((await guard.FinalizeAuthenticatedWriteAsync(
                guardedPreparation.Id, "1000000001", operation, 9, retryChallenge, FinishTag(retryChallenge, 9, key))).Succeeded);
            Assert.False((await guard.FinalizeAuthenticatedWriteAsync(
                guardedPreparation.Id, "1000000001", operation, 9, retryChallenge, FinishTag(retryChallenge, 9, key))).Succeeded);
            var concurrentChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "finish")).Challenge);
            var concurrentFinishes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                guard.FinalizeAuthenticatedWriteAsync(guardedPreparation.Id, "1000000001", operation, 9,
                    concurrentChallenge, FinishTag(concurrentChallenge, 9, key))));
            Assert.Single(concurrentFinishes, result => result.Succeeded);

            var completions = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                guard.FinalizeConfirmedWriteAsync(guardedPreparation.Id, "1000000001", operation)));
            Assert.All(completions, result => Assert.True(result.Succeeded));
            Assert.DoesNotContain(9, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.Empty(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.Equal(1, (await repository.FindTeacherByFingerprintTemplateIdAsync(9, "1000000001"))?.TeacherAccountId);
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(9, "1000000002"));
            Assert.Equal(1, (await repository.FindTeacherByFingerprintTemplateIdAsync(2, "1000000002"))?.TeacherAccountId);
            Assert.False((await guard.FinalizeConfirmedWriteAsync(Guid.NewGuid().ToString("D"), "1000000001", operation)).Succeeded);
            Assert.False((await guard.FinalizeConfirmedWriteAsync(guardedPreparation.Id, "1000000002", operation)).Succeeded);
            Assert.True((await new FingerprintWriteGuardStore(configuration).FinalizeConfirmedWriteAsync(guardedPreparation.Id, "1000000001", operation)).Succeeded);
            Assert.False((await repository.PrepareFingerprintEnrollmentAsync(1, "Left middle", 11)).Succeeded);
            await ExecuteAsync(connection, "INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active) VALUES (4, 'four', 'Teacher Four', 'hash', 'Teacher', 1)");
            Assert.True((await repository.PrepareFingerprintEnrollmentAsync(4, "Left middle", 11)).Succeeded);
            var nextPreparation = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.False((await guard.MarkUncertainAsync(nextPreparation.Id, "1000000001", operation.ToUpperInvariant())).Succeeded);
            Assert.False(Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync()).HasUnresolvedWrite);
            Assert.True((await repository.AuthorizeFingerprintEnrollmentAsync(3, nextPreparation.Id, false)).Succeeded);
            string nextOperation;
            await using (var readOperation = new MySqlCommand("SELECT operation_id FROM fingerprint_enrollment_authorizations WHERE preparation_id = @id", connection))
            {
                readOperation.Parameters.AddWithValue("@id", nextPreparation.Id);
                nextOperation = (string)(await readOperation.ExecuteScalarAsync())!;
            }
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_challenges");
            var claimChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "claim")).Challenge);
            byte[] ClaimTag(byte[] nonce, ushort slot, bool occupied, byte[] signingKey) =>
                EnrollmentMessageAuthentication.ComputeTag(signingKey, false, "1000000001", "claim", nonce,
                    Encoding.ASCII.GetBytes($"{nextPreparation.Id}\n{nextOperation}\n{slot}\n{(occupied ? "occupied" : "empty")}\n"));
            await ExecuteAsync(connection, """
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                VALUES (4, 'Right thumb', 20), (4, 'Left thumb', 21)
                """);
            Assert.Null(await guard.GetAuthorizedOperationAsync("1000000001"));
            Assert.False((await guard.ClaimAuthorizedWriteAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                false, claimChallenge, ClaimTag(claimChallenge, 11, false, key))).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_templates WHERE template_identifier IN (20, 21)");
            Assert.False((await guard.ClaimAuthorizedWriteAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                false, claimChallenge, ClaimTag(claimChallenge, 11, false, keyTwo))).Succeeded);
            Assert.False((await guard.ClaimAuthorizedWriteAsync(nextPreparation.Id, "1000000001", nextOperation, 12,
                false, claimChallenge, ClaimTag(claimChallenge, 12, false, key))).Succeeded);
            Assert.False((await guard.ClaimAuthorizedWriteAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                true, claimChallenge, ClaimTag(claimChallenge, 11, true, key))).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_authorizations SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            Assert.False((await guard.ClaimAuthorizedWriteAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                false, claimChallenge, ClaimTag(claimChallenge, 11, false, key))).Succeeded);
            Assert.True((await repository.AuthorizeFingerprintEnrollmentAsync(3, nextPreparation.Id, false)).Succeeded);
            await using (var readOperation = new MySqlCommand("SELECT operation_id FROM fingerprint_enrollment_authorizations WHERE preparation_id = @id", connection))
            {
                readOperation.Parameters.AddWithValue("@id", nextPreparation.Id);
                nextOperation = (string)(await readOperation.ExecuteScalarAsync())!;
            }
            await using (var legacyConsent = new MySqlCommand("UPDATE fingerprint_enrollment_authorizations SET allow_occupied_slot = 1 WHERE preparation_id = @id", connection))
            {
                legacyConsent.Parameters.AddWithValue("@id", nextPreparation.Id);
                Assert.Equal(1, await legacyConsent.ExecuteNonQueryAsync());
            }
            var occupiedClaim = await guard.ClaimAuthorizedWriteAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                true, claimChallenge, ClaimTag(claimChallenge, 11, true, key));
            Assert.False(occupiedClaim.Succeeded);
            Assert.Equal("Replacement of an occupied fingerprint slot is not available yet.", occupiedClaim.Error);
            Assert.False(Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync()).HasUnresolvedWrite);
            Assert.True(Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync()).HasActiveAuthorization);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 3");
            Assert.False((await guard.ClaimAuthorizedWriteAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                false, claimChallenge, ClaimTag(claimChallenge, 11, false, key))).Succeeded);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 3");
            await ExecuteAsync(connection, """
                CREATE TRIGGER reject_guard BEFORE INSERT ON fingerprint_enrollment_write_guards
                FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated guard failure'
                """);
            await Assert.ThrowsAsync<MySqlException>(() => guard.ClaimAuthorizedWriteAsync(
                nextPreparation.Id, "1000000001", nextOperation, 11, false, claimChallenge, ClaimTag(claimChallenge, 11, false, key)));
            await ExecuteAsync(connection, "DROP TRIGGER reject_guard");
            var claimResults = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                guard.ClaimAuthorizedWriteAsync(nextPreparation.Id.ToUpperInvariant(), "1000000001", nextOperation.ToUpperInvariant(), 11,
                    false, claimChallenge, ClaimTag(claimChallenge, 11, false, key))));
            Assert.Single(claimResults, result => result.Succeeded);
            Assert.True(Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync()).HasUnresolvedWrite);
            Assert.False(Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync()).HasActiveAuthorization);
            Assert.False((await guard.ClaimAuthorizedWriteAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                false, claimChallenge, ClaimTag(claimChallenge, 11, false, key))).Succeeded);
            var clientNonce = RandomNumberGenerator.GetBytes(32);
            var clientNonceText = Convert.ToHexString(clientNonce).ToLowerInvariant();
            byte[] RecoveryTag(byte[] nonce, ushort slot, string op, byte[] signingKey) =>
                EnrollmentMessageAuthentication.ComputeTag(signingKey, false, "1000000001", "recover", nonce,
                    Encoding.ASCII.GetBytes($"{nextPreparation.Id}\n{op}\n{slot}\n{clientNonceText}\n"));
            var recoveryChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "recover")).Challenge);
            var wrongOperation = Guid.NewGuid().ToString("D");
            Assert.Null((await guard.GetAuthenticatedRecoveryStateAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                recoveryChallenge, clientNonce, RecoveryTag(recoveryChallenge, 11, nextOperation, keyTwo))).State);
            Assert.Null((await guard.GetAuthenticatedRecoveryStateAsync(nextPreparation.Id, "1000000001", wrongOperation, 11,
                recoveryChallenge, clientNonce, RecoveryTag(recoveryChallenge, 11, wrongOperation, key))).State);
            Assert.Null((await guard.GetAuthenticatedRecoveryStateAsync(nextPreparation.Id, "1000000001", nextOperation, 12,
                recoveryChallenge, clientNonce, RecoveryTag(recoveryChallenge, 12, nextOperation, key))).State);
            var recoveryResults = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                guard.GetAuthenticatedRecoveryStateAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                    recoveryChallenge, clientNonce, RecoveryTag(recoveryChallenge, 11, nextOperation, key))));
            Assert.Single(recoveryResults, result => result.State == EnrollmentRecoveryState.Unresolved);
            Assert.Equal(5, recoveryResults.Count(result => result.State is null && result.Error is not null));
            var protocol = new EnrollmentRecoveryProtocol(configuration);
            var signedChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "recover")).Challenge);
            var protocolRequest = new EnrollmentRecoveryRequest("1000000001", nextPreparation.Id, nextOperation, 11,
                Convert.ToHexString(signedChallenge).ToLowerInvariant(),
                clientNonceText,
                Convert.ToHexString(RecoveryTag(signedChallenge, 11, nextOperation, key)).ToLowerInvariant());
            Assert.Null(await protocol.RecoverAsync(protocolRequest with { Tag = protocolRequest.Tag!.ToUpperInvariant() }));
            Assert.Null(await protocol.RecoverAsync(protocolRequest with { Slot = 12 }));
            Assert.Null(await protocol.RecoverAsync(protocolRequest with { ClientNonce = new string('0', 64) }));
            var signed = Assert.IsType<EnrollmentRecoveryResponse>(await protocol.RecoverAsync(protocolRequest));
            Assert.Equal($"{nextPreparation.Id}\n{nextOperation}\n11\n{clientNonceText}\nunresolved\n", signed.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "recover", signedChallenge,
                Encoding.ASCII.GetBytes(signed.Body), Convert.FromHexString(signed.Tag)));
            Assert.False(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "recover", signedChallenge,
                Encoding.ASCII.GetBytes(signed.Body.Replace("unresolved", "completed")), Convert.FromHexString(signed.Tag)));
            Assert.False(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "recover", signedChallenge,
                Encoding.ASCII.GetBytes(signed.Body.Replace(clientNonceText, new string('0', 64))), Convert.FromHexString(signed.Tag)));
            Assert.Null(await protocol.RecoverAsync(protocolRequest));
            var webBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            webBuilder.Logging.ClearProviders();
            webBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            webBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString,
                ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master)
            });
            await using (var app = webBuilder.Build())
            {
                app.MapEnrollmentRecoveryRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
                await app.StartAsync();
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                var address = Assert.Single(addresses.Addresses);
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                var challengeRequest = new EnrollmentRecoveryChallengeRequest("1000000001");
                using var disabled = await client.PostAsJsonAsync("api/device/enrollment/recovery/challenge", challengeRequest);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
                using var disabledRecovery = await client.PostAsJsonAsync("api/device/enrollment/recovery", protocolRequest);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledRecovery.StatusCode);
                app.Configuration["EnrollmentSecurity:EnableRecoveryApi"] = "true";
                using var unauthorized = await client.PostAsJsonAsync("api/device/enrollment/recovery/challenge", challengeRequest);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                using var unauthorizedRecovery = await client.PostAsJsonAsync("api/device/enrollment/recovery", protocolRequest);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedRecovery.StatusCode);
                client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
                using var oversizedContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
                using var oversized = await client.PostAsync("api/device/enrollment/recovery/challenge", oversizedContent);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
                using var oversizedRecoveryContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
                using var oversizedRecovery = await client.PostAsync("api/device/enrollment/recovery", oversizedRecoveryContent);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedRecovery.StatusCode);
                using var invalid = await client.PostAsJsonAsync("api/device/enrollment/recovery/challenge", new EnrollmentRecoveryChallengeRequest("bad"));
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                using var challengeResponse = await client.PostAsJsonAsync("api/device/enrollment/recovery/challenge", challengeRequest);
                Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
                Assert.True(challengeResponse.Headers.CacheControl?.NoStore);
                var received = await challengeResponse.Content.ReadFromJsonAsync<EnrollmentRecoveryChallengeResponse>();
                var httpChallenge = Convert.FromHexString(Assert.IsType<EnrollmentRecoveryChallengeResponse>(received).Challenge);
                var httpRequest = protocolRequest with {
                    Challenge = Convert.ToHexString(httpChallenge).ToLowerInvariant(),
                    Tag = Convert.ToHexString(RecoveryTag(httpChallenge, 11, nextOperation, key)).ToLowerInvariant()
                };
                using var recoveryResponse = await client.PostAsJsonAsync("api/device/enrollment/recovery", httpRequest);
                Assert.Equal(HttpStatusCode.OK, recoveryResponse.StatusCode);
                Assert.True(recoveryResponse.Headers.CacheControl?.NoStore);
                var signedHttp = Assert.IsType<EnrollmentRecoveryResponse>(await recoveryResponse.Content.ReadFromJsonAsync<EnrollmentRecoveryResponse>());
                Assert.Equal($"{nextPreparation.Id}\n{nextOperation}\n11\n{clientNonceText}\nunresolved\n", signedHttp.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "recover", httpChallenge,
                    Encoding.ASCII.GetBytes(signedHttp.Body), Convert.FromHexString(signedHttp.Tag)));
                using var replay = await client.PostAsJsonAsync("api/device/enrollment/recovery", httpRequest);
                Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
                Assert.True(replay.Headers.CacheControl?.NoStore);
                await app.StopAsync();
            }
            var expiredRecovery = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "recover")).Challenge);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_challenges SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND) WHERE operation_name = 'recover'");
            var expiredResult = await guard.GetAuthenticatedRecoveryStateAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                expiredRecovery, clientNonce, RecoveryTag(expiredRecovery, 11, nextOperation, key));
            Assert.Null(expiredResult.State);
            Assert.NotNull(expiredResult.Error);
            Assert.Contains(11, await repository.GetBlockedFingerprintSlotsAsync());
            var beforeFinishChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "inventory")).Challenge);
            Assert.True((await guard.FinalizeConfirmedWriteAsync(nextPreparation.Id.ToUpperInvariant(), "1000000001", nextOperation)).Succeeded);
            var beforeFinishTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "inventory", beforeFinishChallenge, inventoryBody);
            Assert.False(await inventory.RecordAsync(inventoryRequest with {
                Challenge = Convert.ToHexString(beforeFinishChallenge).ToLowerInvariant(),
                Tag = Convert.ToHexString(beforeFinishTag).ToLowerInvariant()
            }));
            var completedChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "recover")).Challenge);
            Assert.Equal(EnrollmentRecoveryState.Completed, (await guard.GetAuthenticatedRecoveryStateAsync(
                nextPreparation.Id.ToUpperInvariant(), "1000000001", nextOperation.ToUpperInvariant(), 11,
                completedChallenge, clientNonce, RecoveryTag(completedChallenge, 11, nextOperation, key))).State);
            Assert.Null((await guard.GetAuthenticatedRecoveryStateAsync(nextPreparation.Id, "1000000001", nextOperation, 11,
                completedChallenge, clientNonce, RecoveryTag(completedChallenge, 11, nextOperation, key))).State);
            var signedCompletedChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "recover")).Challenge);
            var signedCompleted = Assert.IsType<EnrollmentRecoveryResponse>(await protocol.RecoverAsync(protocolRequest with {
                Challenge = Convert.ToHexString(signedCompletedChallenge).ToLowerInvariant(),
                Tag = Convert.ToHexString(RecoveryTag(signedCompletedChallenge, 11, nextOperation, key)).ToLowerInvariant()
            }));
            Assert.Equal($"{nextPreparation.Id}\n{nextOperation}\n11\n{clientNonceText}\ncompleted\n", signedCompleted.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "recover", signedCompletedChallenge,
                Encoding.ASCII.GetBytes(signedCompleted.Body), Convert.FromHexString(signedCompleted.Tag)));
            Assert.Equal(4, (await repository.FindTeacherByFingerprintTemplateIdAsync(11, "1000000001"))?.TeacherAccountId);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_challenges");
            var beforeClearChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "inventory")).Challenge);
            await ExecuteAsync(connection, "INSERT INTO fingerprint_sensor_inventory (device_id, capacity, bitmap, observed_after_utc) VALUES (1, 8, UNHEX('0C'), UTC_TIMESTAMP(6))");
            Assert.True((await repository.RequestClearAllFingerprintDevicesAsync()).Succeeded);
            await using (var snapshotCount = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_sensor_inventory", connection))
                Assert.Equal(0L, Convert.ToInt64(await snapshotCount.ExecuteScalarAsync()));
            var beforeClearTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "inventory", beforeClearChallenge, inventoryBody);
            Assert.False(await inventory.RecordAsync(inventoryRequest with {
                Challenge = Convert.ToHexString(beforeClearChallenge).ToLowerInvariant(),
                Tag = Convert.ToHexString(beforeClearTag).ToLowerInvariant()
            }));
            var duringClearChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "inventory")).Challenge);
            var duringClearTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "inventory", duringClearChallenge, inventoryBody);
            var duringClear = inventoryRequest with {
                Challenge = Convert.ToHexString(duringClearChallenge).ToLowerInvariant(),
                Tag = Convert.ToHexString(duringClearTag).ToLowerInvariant()
            };
            Assert.False(await inventory.RecordAsync(duringClear));
            Assert.True((await repository.ClearRoomSessionByDeviceIdAsync("1000000001")).Succeeded);
            Assert.False(await inventory.RecordAsync(duringClear));
            Assert.True((await repository.DeleteBiometricDeviceAsync(1)).Succeeded);
            Assert.Null(await credentials.LoadAsync("1000000001", master));
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(9, "1000000002"));
            Assert.True((await repository.ResetSystemToFirstAccessAsync(new DefaultAdminOptions { Username = "admin" }, "hash")).Succeeded);
        }
        finally
        {
            if (created)
            {
                await using var drop = new MySqlCommand($"DROP DATABASE `{database}`", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    [MariaDbFact]
    public async Task ReplacementIntentLookup_AuthenticatesAndNeverAuthorizesWrite()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCLASS_TEST_CONNECTION")!) { Database = string.Empty };
        var database = "smartclass_intent_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await ExecuteAsync(admin, $"CREATE DATABASE `{database}`");
            created = true;
            builder.Database = database;
            var master = RandomNumberGenerator.GetBytes(32);
            var key = RandomNumberGenerator.GetBytes(32);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString,
                ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master)
            }).Build();
            await new DatabaseMigrationRunner(configuration, NullLogger<DatabaseMigrationRunner>.Instance).RunMigrationsAsync();
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, """
                INSERT INTO classrooms (id, name, capacity, is_active) VALUES (1, 'Room', 30, 1);
                INSERT INTO biometric_devices (id, classroom_id, device_id, display_name, is_active, paired_at)
                    VALUES (1, 1, '1000000001', 'Device 1', 1, CURRENT_TIMESTAMP);
                INSERT INTO fingerprint_enrollment_scanner VALUES (1, 1);
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                    VALUES (1, 'teacher', 'Teacher', 'hash', 'Teacher', 1), (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier) VALUES (1, 'Right thumb', 3);
                INSERT INTO fingerprint_template_device_bindings (fingerprint_template_id, device_id)
                    SELECT id, '1000000001' FROM fingerprint_templates;
                """);
            var credentials = new EnrollmentDeviceCredentialStore(configuration);
            Assert.True((await credentials.StoreNewAsync("1000000001", key, master)).Succeeded);
            var repository = new MariaDbUserAccountRepository(configuration, Options.Create(new SchoolTimeOptions()));
            Assert.True((await repository.PrepareFingerprintReplacementAsync(1, 1, "Right thumb", 3)).Succeeded);
            var preparation = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            var guardedOperation = Guid.NewGuid().ToString("D");
            await using (var insert = new MySqlCommand("INSERT INTO fingerprint_enrollment_write_guards (preparation_id, operation_id) VALUES (@id, @operation)", connection))
            {
                insert.Parameters.AddWithValue("@id", preparation.Id);
                insert.Parameters.AddWithValue("@operation", guardedOperation);
                await insert.ExecuteNonQueryAsync();
            }
            Assert.True((await new FingerprintReplacementRecoveryIntent(configuration)
                .RecordAsync(2, 1, 1, "Right thumb", 3, preparation.Id, guardedOperation)).Succeeded);
            var challengeStore = new EnrollmentChallengeStore(configuration);
            var protocol = new EnrollmentRecoveryProtocol(configuration);
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            async Task<EnrollmentRecoveryRequest> RequestAsync(ushort slot = 3)
            {
                var challenge = Assert.IsType<byte[]>((await challengeStore.IssueAsync("1000000001", "replacement_intent")).Challenge);
                var body = Encoding.ASCII.GetBytes($"{preparation.Id}\n{guardedOperation}\n{slot}\n{nonce}\n");
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_intent", challenge, body);
                return new("1000000001", preparation.Id, guardedOperation, slot,
                    Convert.ToHexString(challenge).ToLowerInvariant(), nonce, Convert.ToHexString(tag).ToLowerInvariant());
            }

            var request = await RequestAsync();
            Assert.Null(await protocol.GetReplacementIntentAsync(request with { Tag = new string('0', 64) }));
            var response = Assert.IsType<EnrollmentRecoveryResponse>(await protocol.GetReplacementIntentAsync(request));
            var parts = response.Body.Split('\n');
            Assert.Equal(7, parts.Length);
            Assert.Equal(preparation.Id, parts[0]);
            Assert.Equal(guardedOperation, parts[1]);
            Assert.Equal("3", parts[2]);
            Assert.Equal(nonce, parts[3]);
            Assert.True(Guid.TryParseExact(parts[4], "D", out var recoveryOperation) && recoveryOperation != Guid.Empty);
            Assert.Equal("intent_only", parts[5]);
            await using (var readIntent = new MySqlCommand("SELECT recovery_operation_id FROM fingerprint_replacement_recovery_intents WHERE preparation_id = @id", connection))
            {
                readIntent.Parameters.AddWithValue("@id", preparation.Id);
                Assert.Equal((string)(await readIntent.ExecuteScalarAsync())!, parts[4]);
            }
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_intent",
                Convert.FromHexString(request.Challenge!), Encoding.ASCII.GetBytes(response.Body), Convert.FromHexString(response.Tag)));
            Assert.Null(await protocol.GetReplacementIntentAsync(request));
            Assert.Null(await protocol.GetReplacementIntentAsync(await RequestAsync(4)));
            var nonceRequest = await RequestAsync();
            Assert.Null(await protocol.GetReplacementIntentAsync(nonceRequest with { ClientNonce = new string('0', 64) }));
            Assert.NotNull(await protocol.GetReplacementIntentAsync(nonceRequest));
            var parallelRequest = await RequestAsync();
            var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => protocol.GetReplacementIntentAsync(parallelRequest)));
            Assert.Single(replies, reply => reply is not null);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 2");
            Assert.Null(await protocol.GetReplacementIntentAsync(await RequestAsync()));
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 2");
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002'");
            var changedBindingRequest = await RequestAsync();
            Assert.Null(await protocol.GetReplacementIntentAsync(changedBindingRequest));
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001'");
            Assert.Null(await protocol.GetReplacementIntentAsync(changedBindingRequest));
            var intentStore = new FingerprintReplacementRecoveryIntent(configuration);
            var preparationGuid = Guid.Parse(preparation.Id);
            var guardGuid = Guid.Parse(guardedOperation);
            var retryGuid = Guid.Parse(parts[4]);
            async Task<ReplacementAttemptStatusRequest> StatusRequestAsync(Guid retry, ushort slot = 3)
            {
                var challenge = Assert.IsType<byte[]>((await challengeStore.IssueAsync("1000000001", "replacement_attempt_status")).Challenge);
                var body = Encoding.ASCII.GetBytes($"{preparationGuid:D}\n{guardGuid:D}\n{retry:D}\n{slot}\n{nonce}\n");
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_attempt_status", challenge, body);
                return new("1000000001", preparation.Id, guardedOperation, retry.ToString("D"), slot,
                    Convert.ToHexString(challenge).ToLowerInvariant(), nonce, Convert.ToHexString(tag).ToLowerInvariant());
            }
            var statusRequest = await StatusRequestAsync(retryGuid);
            Assert.Null(await protocol.GetReplacementAttemptStatusAsync(statusRequest with { Tag = new string('0', 64) }));
            var unreserved = Assert.IsType<EnrollmentRecoveryResponse>(await protocol.GetReplacementAttemptStatusAsync(statusRequest));
            Assert.Equal($"{preparationGuid:D}\n{guardGuid:D}\n{retryGuid:D}\n3\n{nonce}\nnot_reserved\n", unreserved.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_attempt_status",
                Convert.FromHexString(statusRequest.Challenge!), Encoding.ASCII.GetBytes(unreserved.Body), Convert.FromHexString(unreserved.Tag)));
            Assert.Null(await protocol.GetReplacementAttemptStatusAsync(statusRequest));
            Assert.Null(await protocol.GetReplacementAttemptStatusAsync(await StatusRequestAsync(Guid.NewGuid())));
            Assert.Null(await protocol.GetReplacementAttemptStatusAsync(await StatusRequestAsync(retryGuid, 4)));
            var alteredNonce = await StatusRequestAsync(retryGuid);
            Assert.Null(await protocol.GetReplacementAttemptStatusAsync(alteredNonce with { ClientNonce = new string('0', 64) }));
            Assert.NotNull(await protocol.GetReplacementAttemptStatusAsync(alteredNonce));
            async Task<ReplacementAttemptReserveRequest> ReserveRequestAsync(Guid retry, Guid? guard = null, ushort slot = 3)
            {
                var challenge = Assert.IsType<byte[]>((await challengeStore.IssueAsync("1000000001", "replacement_attempt_reserve")).Challenge);
                var body = Encoding.ASCII.GetBytes($"{preparationGuid:D}\n{(guard ?? guardGuid):D}\n{retry:D}\n{slot}\n{nonce}\n");
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_attempt_reserve", challenge, body);
                return new("1000000001", preparation.Id, (guard ?? guardGuid).ToString("D"), retry.ToString("D"), slot,
                    Convert.ToHexString(challenge).ToLowerInvariant(), nonce, Convert.ToHexString(tag).ToLowerInvariant());
            }
            var otherPurpose = await StatusRequestAsync(retryGuid);
            var otherPurposeBody = Encoding.ASCII.GetBytes($"{preparationGuid:D}\n{guardGuid:D}\n{retryGuid:D}\n3\n{nonce}\n");
            var otherPurposeTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_attempt_reserve",
                Convert.FromHexString(otherPurpose.Challenge!), otherPurposeBody);
            var wrongPurpose = new ReplacementAttemptReserveRequest(otherPurpose.DeviceId, otherPurpose.PreparationId,
                otherPurpose.OperationId, otherPurpose.RecoveryOperationId, otherPurpose.Slot, otherPurpose.Challenge,
                otherPurpose.ClientNonce, Convert.ToHexString(otherPurposeTag).ToLowerInvariant());
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(wrongPurpose));
            Assert.NotNull(await protocol.GetReplacementAttemptStatusAsync(otherPurpose));
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(await ReserveRequestAsync(Guid.NewGuid())));
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(await ReserveRequestAsync(retryGuid, Guid.NewGuid())));
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(await ReserveRequestAsync(retryGuid, slot: 4)));
            var expiredChallenge = await ReserveRequestAsync(retryGuid);
            await using (var expireChallenge = new MySqlCommand(
                "UPDATE fingerprint_enrollment_challenges SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND) WHERE challenge_hash = @hash", connection))
            {
                expireChallenge.Parameters.AddWithValue("@hash", SHA256.HashData(Convert.FromHexString(expiredChallenge.Challenge!)));
                Assert.Equal(1, await expireChallenge.ExecuteNonQueryAsync());
            }
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(expiredChallenge));
            var invalidEligibility = await ReserveRequestAsync(retryGuid);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 2");
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(invalidEligibility));
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 2");
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(invalidEligibility));
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002'");
            var invalidBinding = await ReserveRequestAsync(retryGuid);
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(invalidBinding));
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001'");
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(invalidBinding));
            Task<OperationResult> ReserveAttempt(string device, Guid prep, Guid guard, Guid retry, ushort slot) =>
                intentStore.ReserveAttemptAsync(device, prep, guard, retry, slot);
            Assert.False((await ReserveAttempt("bad", preparationGuid, guardGuid, retryGuid, 3)).Succeeded);
            Assert.False((await ReserveAttempt("1000000001", preparationGuid, guardGuid, Guid.NewGuid(), 3)).Succeeded);
            Assert.False((await ReserveAttempt("1000000001", preparationGuid, Guid.NewGuid(), retryGuid, 3)).Succeeded);
            Assert.False((await ReserveAttempt("1000000001", preparationGuid, guardGuid, retryGuid, 4)).Succeeded);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 2");
            Assert.False((await ReserveAttempt("1000000001", preparationGuid, guardGuid, retryGuid, 3)).Succeeded);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 2");
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002'");
            Assert.False((await ReserveAttempt("1000000001", preparationGuid, guardGuid, retryGuid, 3)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001'");
            await ExecuteAsync(connection, "UPDATE fingerprint_replacement_recovery_intents SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            Assert.Null(await protocol.GetReplacementIntentAsync(await RequestAsync()));
            Assert.False((await ReserveAttempt("1000000001", preparationGuid, guardGuid, retryGuid, 3)).Succeeded);
            var expiredReserve = await ReserveRequestAsync(retryGuid);
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(expiredReserve));
            var expiredStatus = Assert.IsType<EnrollmentRecoveryResponse>(await protocol.GetReplacementAttemptStatusAsync(await StatusRequestAsync(retryGuid)));
            Assert.EndsWith("\nnot_reserved\n", expiredStatus.Body);
            Assert.True((await intentStore.RecordAsync(2, 1, 1, "Right thumb", 3, preparation.Id, guardedOperation)).Succeeded);
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(await ReserveRequestAsync(retryGuid)));
            await using (var restoreIntent = new MySqlCommand(
                "UPDATE fingerprint_replacement_recovery_intents SET recovery_operation_id = @retry WHERE preparation_id = @id", connection))
            {
                restoreIntent.Parameters.AddWithValue("@retry", retryGuid.ToString("D"));
                restoreIntent.Parameters.AddWithValue("@id", preparation.Id);
                Assert.Equal(1, await restoreIntent.ExecuteNonQueryAsync());
            }
            await ExecuteAsync(connection, "UPDATE fingerprint_replacement_recovery_intents SET expires_utc = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 1 MINUTE)");
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(expiredReserve));
            var rollbackRequest = await ReserveRequestAsync(retryGuid);
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(rollbackRequest with { Tag = new string('0', 64) }));
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(rollbackRequest with { ClientNonce = new string('0', 64) }));
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(rollbackRequest with { DeviceId = "1000000002" }));
            var wrongKeyTag = EnrollmentMessageAuthentication.ComputeTag(RandomNumberGenerator.GetBytes(32), false, "1000000001",
                "replacement_attempt_reserve", Convert.FromHexString(rollbackRequest.Challenge!), otherPurposeBody);
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(rollbackRequest with { Tag = Convert.ToHexString(wrongKeyTag).ToLowerInvariant() }));
            await ExecuteAsync(connection, """
                CREATE TRIGGER reject_retry BEFORE INSERT ON fingerprint_replacement_recovery_attempts
                FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated attempt failure'
                """);
            await Assert.ThrowsAsync<MySqlException>(() => protocol.ReserveReplacementAttemptAsync(rollbackRequest));
            await ExecuteAsync(connection, "DROP TRIGGER reject_retry");
            await using (var attemptsBeforeRetry = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_replacement_recovery_attempts", connection))
                Assert.Equal(0L, Convert.ToInt64(await attemptsBeforeRetry.ExecuteScalarAsync()));
            var attempts = new[] { rollbackRequest, rollbackRequest, rollbackRequest, await ReserveRequestAsync(retryGuid), await ReserveRequestAsync(retryGuid),
                await ReserveRequestAsync(retryGuid) };
            var attemptResults = await Task.WhenAll(attempts.Select(attempt => protocol.ReserveReplacementAttemptAsync(attempt)));
            var receipt = Assert.Single(attemptResults, result => result is not null);
            var successfulRequest = attempts[Array.FindIndex(attemptResults, result => result is not null)];
            Assert.Equal($"{preparationGuid:D}\n{guardGuid:D}\n{retryGuid:D}\n3\n{nonce}\nreserved_only\n", receipt!.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_attempt_reserve",
                Convert.FromHexString(successfulRequest.Challenge!), Encoding.ASCII.GetBytes(receipt.Body), Convert.FromHexString(receipt.Tag)));
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(successfulRequest));
            Assert.Null(await protocol.ReserveReplacementAttemptAsync(await ReserveRequestAsync(retryGuid)));
            var reservedRequest = await StatusRequestAsync(retryGuid);
            var reservedReplies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => protocol.GetReplacementAttemptStatusAsync(reservedRequest)));
            var reserved = Assert.Single(reservedReplies, reply => reply is not null);
            Assert.Equal($"{preparationGuid:D}\n{guardGuid:D}\n{retryGuid:D}\n3\n{nonce}\nreserved\n", reserved!.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_attempt_status",
                Convert.FromHexString(reservedRequest.Challenge!), Encoding.ASCII.GetBytes(reserved.Body), Convert.FromHexString(reserved.Tag)));
            Assert.Null(await protocol.GetReplacementAttemptStatusAsync(await StatusRequestAsync(Guid.NewGuid())));
            await ExecuteAsync(connection, "UPDATE fingerprint_replacement_recovery_intents SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            var expiredReserved = Assert.IsType<EnrollmentRecoveryResponse>(await protocol.GetReplacementAttemptStatusAsync(await StatusRequestAsync(retryGuid)));
            Assert.EndsWith("\nreserved\n", expiredReserved.Body);
            Assert.False((await ReserveAttempt("1000000001", preparationGuid, guardGuid, retryGuid, 3)).Succeeded);
            Assert.Null(await protocol.GetReplacementIntentAsync(await RequestAsync()));
            await ExecuteAsync(connection, "UPDATE fingerprint_replacement_recovery_intents SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            Assert.False((await intentStore.RecordAsync(2, 1, 1, "Right thumb", 3, preparation.Id, guardedOperation)).Succeeded);
            await using (var attempt = new MySqlCommand("""
                SELECT guarded_operation_id, recovery_operation_id
                FROM fingerprint_replacement_recovery_attempts WHERE preparation_id = @id
                """, connection))
            {
                attempt.Parameters.AddWithValue("@id", preparation.Id);
                await using var reader = await attempt.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(guardedOperation, reader.GetString(0));
                Assert.Equal(parts[4], reader.GetString(1));
            }
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_write_guards"));
            Assert.False((await repository.CancelFingerprintEnrollmentPreparationAsync(preparation.Id)).Succeeded);
            Assert.False((await repository.SetFingerprintSlotBlockedAsync(3, false)).Succeeded);
            Assert.True(Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync()).HasUnresolvedWrite);
            Assert.Contains(3, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(3, "1000000001"));
            Assert.Null(await new FingerprintWriteGuardStore(configuration).GetAuthorizedOperationAsync("1000000001"));
        }
        finally
        {
            if (created) await ExecuteAsync(admin, $"DROP DATABASE `{database}`");
        }
    }

    [MariaDbFact]
    public async Task ReplacementCompletion_IsAtomicBoundAndDoesNotResolveRecoveryAttempts()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCLASS_TEST_CONNECTION")!) { Database = string.Empty };
        var database = "smartclass_replacement_finish_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await ExecuteAsync(admin, $"CREATE DATABASE `{database}`");
            created = true;
            builder.Database = database;
            var master = RandomNumberGenerator.GetBytes(32);
            var key = RandomNumberGenerator.GetBytes(32);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString,
                ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master)
            }).Build();
            var migrator = new DatabaseMigrationRunner(configuration, NullLogger<DatabaseMigrationRunner>.Instance);
            await migrator.RunMigrationsAsync();
            await migrator.RunMigrationsAsync();
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, """
                INSERT INTO classrooms (id, name, capacity, is_active) VALUES (1, 'Room', 30, 1);
                INSERT INTO biometric_devices (id, classroom_id, device_id, display_name, is_active, paired_at)
                    VALUES (1, 1, '1000000001', 'Device 1', 1, CURRENT_TIMESTAMP),
                           (2, NULL, '1000000002', 'Device 2', 1, CURRENT_TIMESTAMP);
                INSERT INTO fingerprint_enrollment_scanner VALUES (1, 1);
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                    VALUES (1, 'teacher', 'Teacher', 'hash', 'Teacher', 1), (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier) VALUES (1, 'Right thumb', 3);
                INSERT INTO fingerprint_template_device_bindings (fingerprint_template_id, device_id)
                    SELECT id, '1000000001' FROM fingerprint_templates;
                """);
            Assert.True((await new EnrollmentDeviceCredentialStore(configuration).StoreNewAsync("1000000001", key, master)).Succeeded);
            var repository = new MariaDbUserAccountRepository(configuration, Options.Create(new SchoolTimeOptions()));
            var guards = new FingerprintWriteGuardStore(configuration);
            var protocol = new EnrollmentRecoveryProtocol(configuration);
            var challenges = new EnrollmentChallengeStore(configuration);
            Assert.True((await repository.PrepareFingerprintReplacementAsync(1, 1, "Right thumb", 3)).Succeeded);
            var preparation = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.True((await repository.AuthorizeFingerprintReplacementAsync(2, 1, 1, "Right thumb", 3, preparation.Id)).Succeeded);
            string operation;
            await using (var consent = new MySqlCommand("SELECT operation_id FROM fingerprint_enrollment_authorizations WHERE preparation_id = @id", connection))
            {
                consent.Parameters.AddWithValue("@id", preparation.Id);
                operation = (string)(await consent.ExecuteScalarAsync())!;
            }
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            async Task<ReplacementOperationRequest> ReplacementOperationRequestAsync(string purpose = "replacement_operation")
            {
                var challenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", purpose)).Challenge);
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_operation",
                    challenge, Encoding.ASCII.GetBytes($"{nonce}\n"));
                return new("1000000001", Convert.ToHexString(challenge).ToLowerInvariant(), nonce, Convert.ToHexString(tag).ToLowerInvariant());
            }
            Assert.Null(await protocol.GetReplacementOperationAsync(await ReplacementOperationRequestAsync("finish")));
            var operationRequest = await ReplacementOperationRequestAsync();
            Assert.Null(await protocol.GetReplacementOperationAsync(operationRequest with { Tag = new string('0', 64) }));
            Assert.Null(await protocol.GetReplacementOperationAsync(operationRequest with { ClientNonce = new string('0', 64) }));
            var discovered = Assert.IsType<EnrollmentRecoveryResponse>(await protocol.GetReplacementOperationAsync(operationRequest));
            Assert.Equal($"{preparation.Id}\n{operation}\n3\n{nonce}\nreplacement\n", discovered.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_operation",
                Convert.FromHexString(operationRequest.Challenge!), Encoding.ASCII.GetBytes(discovered.Body), Convert.FromHexString(discovered.Tag)));
            Assert.Null(await protocol.GetReplacementOperationAsync(operationRequest));
            Assert.True(preparation.IsReplacement);
            Assert.Equal(1, preparation.DeviceRowId);
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002'");
            var changedBindingRequest = await ReplacementOperationRequestAsync();
            Assert.Null(await protocol.GetReplacementOperationAsync(changedBindingRequest));
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001'");
            Assert.Null(await protocol.GetReplacementOperationAsync(changedBindingRequest));
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_authorizations SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            Assert.Null(await protocol.GetReplacementOperationAsync(await ReplacementOperationRequestAsync()));
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_authorizations SET expires_utc = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 1 MINUTE)");
            var operationBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            operationBuilder.Logging.ClearProviders();
            operationBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            operationBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString,
                ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master),
                ["EnrollmentSecurity:EnableReplacementOperationApi"] = "true"
            });
            await using (var operationApp = operationBuilder.Build())
            {
                operationApp.MapEnrollmentRecoveryRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
                await operationApp.StartAsync();
                var addresses = operationApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
                client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
                using var challengeHttp = await client.PostAsJsonAsync("api/device/enrollment/replacement-operation/challenge",
                    new EnrollmentRecoveryChallengeRequest("1000000001"));
                Assert.Equal(HttpStatusCode.OK, challengeHttp.StatusCode);
                var challengeText = (await challengeHttp.Content.ReadFromJsonAsync<EnrollmentRecoveryChallengeResponse>())!.Challenge;
                var challenge = Convert.FromHexString(challengeText);
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_operation",
                    challenge, Encoding.ASCII.GetBytes($"{nonce}\n"));
                var httpOperation = new ReplacementOperationRequest("1000000001", challengeText, nonce, Convert.ToHexString(tag).ToLowerInvariant());
                using var operationResponse = await client.PostAsJsonAsync("api/device/enrollment/replacement-operation", httpOperation);
                Assert.Equal(HttpStatusCode.OK, operationResponse.StatusCode);
                Assert.True(operationResponse.Headers.CacheControl?.NoStore);
                var receipt = Assert.IsType<EnrollmentRecoveryResponse>(await operationResponse.Content.ReadFromJsonAsync<EnrollmentRecoveryResponse>());
                Assert.Equal(discovered.Body, receipt.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_operation",
                    challenge, Encoding.ASCII.GetBytes(receipt.Body), Convert.FromHexString(receipt.Tag)));
                using var replay = await client.PostAsJsonAsync("api/device/enrollment/replacement-operation", httpOperation);
                Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
                await operationApp.StopAsync();
            }
            async Task<EnrollmentRecoveryRequest> GuardStatusRequestAsync(string op, ushort slot = 3, string purpose = "replacement_guard_status")
            {
                var challenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", purpose)).Challenge);
                var body = Encoding.ASCII.GetBytes($"{preparation.Id}\n{op}\n{slot}\n{nonce}\n");
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_guard_status", challenge, body);
                return new("1000000001", preparation.Id, op, slot, Convert.ToHexString(challenge).ToLowerInvariant(),
                    nonce, Convert.ToHexString(tag).ToLowerInvariant());
            }
            var guardRequest = await GuardStatusRequestAsync(operation);
            Assert.Null(await protocol.GetReplacementGuardStatusAsync(guardRequest with { Tag = new string('0', 64) }));
            Assert.Null(await protocol.GetReplacementGuardStatusAsync(guardRequest with { ClientNonce = new string('0', 64) }));
            var unguarded = Assert.IsType<EnrollmentRecoveryResponse>(await protocol.GetReplacementGuardStatusAsync(guardRequest));
            Assert.Equal($"{preparation.Id}\n{operation}\n3\n{nonce}\nnot_guarded\n", unguarded.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_guard_status",
                Convert.FromHexString(guardRequest.Challenge!), Encoding.ASCII.GetBytes(unguarded.Body), Convert.FromHexString(unguarded.Tag)));
            Assert.Null(await protocol.GetReplacementGuardStatusAsync(guardRequest));
            Assert.Null(await protocol.GetReplacementGuardStatusAsync(await GuardStatusRequestAsync(Guid.NewGuid().ToString("D"))));
            Assert.Null(await protocol.GetReplacementGuardStatusAsync(await GuardStatusRequestAsync(operation, 4)));
            Assert.Null(await protocol.GetReplacementGuardStatusAsync(await GuardStatusRequestAsync(operation, purpose: "finish")));
            var expiredGuardChallenge = await GuardStatusRequestAsync(operation);
            await using (var expireGuard = new MySqlCommand("UPDATE fingerprint_enrollment_challenges SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND) WHERE challenge_hash = @hash", connection))
            {
                expireGuard.Parameters.AddWithValue("@hash", SHA256.HashData(Convert.FromHexString(expiredGuardChallenge.Challenge!)));
                Assert.Equal(1, await expireGuard.ExecuteNonQueryAsync());
            }
            Assert.Null(await protocol.GetReplacementGuardStatusAsync(expiredGuardChallenge));
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_authorizations SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            Assert.EndsWith("\nnot_guarded\n", Assert.IsType<EnrollmentRecoveryResponse>(
                await protocol.GetReplacementGuardStatusAsync(await GuardStatusRequestAsync(operation))).Body);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_authorizations SET expires_utc = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 1 MINUTE)");
            async Task<EnrollmentRecoveryRequest> GuardReserveRequestAsync(string op, ushort slot = 3, string issuePurpose = "replacement_guard_reserve")
            {
                var challenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", issuePurpose)).Challenge);
                var body = Encoding.ASCII.GetBytes($"{preparation.Id}\n{op}\n{slot}\n{nonce}\n");
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_guard_reserve", challenge, body);
                return new("1000000001", preparation.Id, op, slot, Convert.ToHexString(challenge).ToLowerInvariant(),
                    nonce, Convert.ToHexString(tag).ToLowerInvariant());
            }
            Assert.Null(await protocol.ReserveReplacementGuardAsync(await GuardReserveRequestAsync(operation, slot: 4)));
            Assert.Null(await protocol.ReserveReplacementGuardAsync(await GuardReserveRequestAsync(Guid.NewGuid().ToString("D"))));
            Assert.Null(await protocol.ReserveReplacementGuardAsync(await GuardReserveRequestAsync(operation, issuePurpose: "replacement_guard_status")));
            var expiredReserve = await GuardReserveRequestAsync(operation);
            await using (var expireReserve = new MySqlCommand("UPDATE fingerprint_enrollment_challenges SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND) WHERE challenge_hash = @hash", connection))
            {
                expireReserve.Parameters.AddWithValue("@hash", SHA256.HashData(Convert.FromHexString(expiredReserve.Challenge!)));
                Assert.Equal(1, await expireReserve.ExecuteNonQueryAsync());
            }
            Assert.Null(await protocol.ReserveReplacementGuardAsync(expiredReserve));
            var ineligibleReserve = await GuardReserveRequestAsync(operation);
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002'");
            Assert.Null(await protocol.ReserveReplacementGuardAsync(ineligibleReserve));
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001'");
            Assert.Null(await protocol.ReserveReplacementGuardAsync(ineligibleReserve));
            var reserveRequest = await GuardReserveRequestAsync(operation);
            Assert.Null(await protocol.ReserveReplacementGuardAsync(reserveRequest with { Tag = new string('0', 64) }));
            Assert.Null(await protocol.ReserveReplacementGuardAsync(reserveRequest with { ClientNonce = new string('0', 64) }));
            Assert.Null(await protocol.ReserveReplacementGuardAsync(reserveRequest with { DeviceId = "1000000002" }));
            await ExecuteAsync(connection, """
                CREATE TRIGGER reject_reserve_cleanup BEFORE DELETE ON fingerprint_enrollment_authorizations
                FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated guard reservation failure'
                """);
            await Assert.ThrowsAsync<MySqlException>(() => protocol.ReserveReplacementGuardAsync(reserveRequest));
            await ExecuteAsync(connection, "DROP TRIGGER reject_reserve_cleanup");
            await using (var beforeReserve = new MySqlCommand("""
                SELECT (SELECT COUNT(*) FROM fingerprint_enrollment_write_guards),
                       (SELECT COUNT(*) FROM fingerprint_enrollment_authorizations),
                       (SELECT COUNT(*) FROM fingerprint_enrollment_challenges WHERE challenge_hash = @hash)
                """, connection))
            {
                beforeReserve.Parameters.AddWithValue("@hash", SHA256.HashData(Convert.FromHexString(reserveRequest.Challenge!)));
                await using var reader = await beforeReserve.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(1L, reader.GetInt64(2));
            }
            var otherReserve = await GuardReserveRequestAsync(operation);
            var reserveRequests = new[] { reserveRequest, reserveRequest, reserveRequest, otherReserve };
            var reserveResponses = await Task.WhenAll(reserveRequests.Select(item => protocol.ReserveReplacementGuardAsync(item)));
            var reservation = Assert.Single(reserveResponses, item => item is not null);
            var winningRequest = reserveRequests[Array.FindIndex(reserveResponses, item => item is not null)];
            Assert.Equal($"{preparation.Id}\n{operation}\n3\n{nonce}\nguarded_only\n", reservation!.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_guard_reserve",
                Convert.FromHexString(winningRequest.Challenge!), Encoding.ASCII.GetBytes(reservation.Body), Convert.FromHexString(reservation.Tag)));
            Assert.Null(await protocol.ReserveReplacementGuardAsync(winningRequest));
            Assert.Null(await protocol.ReserveReplacementGuardAsync(await GuardReserveRequestAsync(operation)));
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(3, "1000000001"));
            Assert.Contains(3, await repository.GetBlockedFingerprintSlotsAsync());
            var guardedStatusRequest = await GuardStatusRequestAsync(operation);
            var guardedResponses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => protocol.GetReplacementGuardStatusAsync(guardedStatusRequest)));
            var guardedStatus = Assert.Single(guardedResponses, item => item is not null);
            Assert.Equal($"{preparation.Id}\n{operation}\n3\n{nonce}\nguarded\n", guardedStatus!.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_guard_status",
                Convert.FromHexString(guardedStatusRequest.Challenge!), Encoding.ASCII.GetBytes(guardedStatus.Body), Convert.FromHexString(guardedStatus.Tag)));
            Assert.Null(await protocol.GetReplacementGuardStatusAsync(await GuardStatusRequestAsync(Guid.NewGuid().ToString("D"))));
            async Task<ReplacementFinishRequest> FinishRequestAsync(string purpose = "replacement_finish", ushort slot = 3)
            {
                var challenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", purpose)).Challenge);
                var body = Encoding.ASCII.GetBytes($"{preparation.Id}\n{operation}\n{slot}\n{nonce}\nconfirmed\n");
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_finish", challenge, body);
                return new("1000000001", preparation.Id, operation, slot, Convert.ToHexString(challenge).ToLowerInvariant(),
                    nonce, Convert.ToHexString(tag).ToLowerInvariant());
            }
            var wrongPurpose = await FinishRequestAsync("finish");
            Assert.Null(await protocol.FinishReplacementAsync(wrongPurpose));
            var expiredChallenge = await FinishRequestAsync();
            await using (var expire = new MySqlCommand("UPDATE fingerprint_enrollment_challenges SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND) WHERE challenge_hash = @hash", connection))
            {
                expire.Parameters.AddWithValue("@hash", SHA256.HashData(Convert.FromHexString(expiredChallenge.Challenge!)));
                Assert.Equal(1, await expire.ExecuteNonQueryAsync());
            }
            Assert.Null(await protocol.FinishReplacementAsync(expiredChallenge));
            var request = await FinishRequestAsync();
            Assert.Null(await protocol.FinishReplacementAsync(request with { Tag = new string('0', 64) }));
            Assert.Null(await protocol.FinishReplacementAsync(request with { ClientNonce = new string('0', 64) }));
            Assert.Null(await protocol.FinishReplacementAsync(request with { DeviceId = "1000000002" }));
            Assert.Null(await protocol.FinishReplacementAsync(request with { Slot = 4 }));
            Assert.Null(await protocol.FinishReplacementAsync(request with { OperationId = Guid.NewGuid().ToString("D") }));
            Task<OperationResult> Finish(string prep, string device, string op, ushort slot) =>
                guards.FinalizeConfirmedReplacementAsync(prep, device, op, slot);
            Assert.False((await Finish(preparation.Id, "1000000001", operation, 4)).Succeeded);
            Assert.False((await Finish(preparation.Id, "1000000002", operation, 3)).Succeeded);
            Assert.False((await Finish(Guid.NewGuid().ToString("D"), "1000000001", operation, 3)).Succeeded);
            Assert.False((await Finish(preparation.Id, "1000000001", Guid.NewGuid().ToString("D"), 3)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002'");
            Assert.False((await Finish(preparation.Id, "1000000001", operation, 3)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001'");
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_preparations SET replacement_template_id = replacement_template_id + 10000");
            Assert.False((await Finish(preparation.Id, "1000000001", operation, 3)).Succeeded);
            await ExecuteAsync(connection, "UPDATE fingerprint_enrollment_preparations SET replacement_template_id = replacement_template_id - 10000");
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 1");
            Assert.False((await Finish(preparation.Id, "1000000001", operation, 3)).Succeeded);
            await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 1");
            Assert.True((await new FingerprintReplacementRecoveryIntent(configuration)
                .RecordAsync(2, 1, 1, "Right thumb", 3, preparation.Id, operation)).Succeeded);
            Assert.False((await Finish(preparation.Id, "1000000001", operation, 3)).Succeeded);
            Assert.Null(await protocol.FinishReplacementAsync(request));
            await ExecuteAsync(connection, "UPDATE fingerprint_replacement_recovery_intents SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
            Assert.False((await Finish(preparation.Id, "1000000001", operation, 3)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_replacement_recovery_intents");
            await ExecuteAsync(connection, """
                INSERT INTO fingerprint_replacement_recovery_attempts (preparation_id, guarded_operation_id, recovery_operation_id)
                SELECT preparation_id, operation_id, UUID() FROM fingerprint_enrollment_write_guards;
                """);
            Assert.False((await Finish(preparation.Id, "1000000001", operation, 3)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_replacement_recovery_attempts");
            await using (var ordinaryReceipt = new MySqlCommand("""
                INSERT INTO fingerprint_enrollment_completions (operation_id, preparation_id, device_id, template_identifier)
                VALUES (@operation, @preparation, '1000000001', 3)
                """, connection))
            {
                ordinaryReceipt.Parameters.AddWithValue("@operation", operation);
                ordinaryReceipt.Parameters.AddWithValue("@preparation", Guid.NewGuid().ToString("D"));
                await ordinaryReceipt.ExecuteNonQueryAsync();
            }
            Assert.False((await Finish(preparation.Id, "1000000001", operation, 3)).Succeeded);
            await ExecuteAsync(connection, "DELETE FROM fingerprint_enrollment_completions");
            Assert.Null(await protocol.FinishReplacementAsync(request));
            var claimBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            claimBuilder.Logging.ClearProviders();
            claimBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            claimBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString,
                ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master),
                ["EnrollmentSecurity:EnableReplacementWriteClaimApi"] = "true"
            });
            await using (var claimApp = claimBuilder.Build())
            {
                claimApp.MapEnrollmentRecoveryRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
                await claimApp.StartAsync();
                var addresses = claimApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                using var claimClient = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
                claimClient.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
                using var challengeHttp = await claimClient.PostAsJsonAsync("api/device/enrollment/replacement-write-claim/challenge",
                    new EnrollmentRecoveryChallengeRequest("1000000001"));
                Assert.Equal(HttpStatusCode.OK, challengeHttp.StatusCode);
                Assert.True(challengeHttp.Headers.CacheControl?.NoStore);
                var claimChallengeText = (await challengeHttp.Content.ReadFromJsonAsync<EnrollmentRecoveryChallengeResponse>())!.Challenge;
                var writeChallenge = Convert.FromHexString(claimChallengeText);
                var writeBody = Encoding.ASCII.GetBytes($"{preparation.Id}\n{operation}\n3\n{nonce}\n");
                var writeTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_write_claim", writeChallenge, writeBody);
                var claimRequest = new EnrollmentRecoveryRequest("1000000001", preparation.Id, operation, 3,
                    claimChallengeText, nonce, Convert.ToHexString(writeTag).ToLowerInvariant());
                using var claimHttp = await claimClient.PostAsJsonAsync("api/device/enrollment/replacement-write-claim", claimRequest);
                Assert.Equal(HttpStatusCode.OK, claimHttp.StatusCode);
                Assert.True(claimHttp.Headers.CacheControl?.NoStore);
                var claimReceipt = Assert.IsType<EnrollmentRecoveryResponse>(await claimHttp.Content.ReadFromJsonAsync<EnrollmentRecoveryResponse>());
                Assert.Equal($"{preparation.Id}\n{operation}\n3\n{nonce}\ngranted\n", claimReceipt.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_write_claim",
                    writeChallenge, Encoding.ASCII.GetBytes(claimReceipt.Body), Convert.FromHexString(claimReceipt.Tag)));
                using var claimReplay = await claimClient.PostAsJsonAsync("api/device/enrollment/replacement-write-claim", claimRequest);
                Assert.Equal(HttpStatusCode.Forbidden, claimReplay.StatusCode);
                await claimApp.StopAsync();
            }
            await ExecuteAsync(connection, """
                INSERT INTO fingerprint_sensor_inventory (device_id, capacity, bitmap, observed_after_utc)
                    VALUES (1, 8, X'08', UTC_TIMESTAMP(6))
                """);
            var inventoryChallenge = Assert.IsType<byte[]>((await new EnrollmentChallengeStore(configuration).IssueAsync("1000000001", "inventory")).Challenge);
            await ExecuteAsync(connection, """
                CREATE TRIGGER reject_cleanup BEFORE DELETE ON fingerprint_slot_blocks
                FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated replacement cleanup failure'
                """);
            await Assert.ThrowsAsync<MySqlException>(() => protocol.FinishReplacementAsync(request));
            await ExecuteAsync(connection, "DROP TRIGGER reject_cleanup");
            await using (var rollback = new MySqlCommand("""
                SELECT (SELECT COUNT(*) FROM fingerprint_enrollment_write_guards),
                       (SELECT COUNT(*) FROM fingerprint_enrollment_preparations),
                       (SELECT COUNT(*) FROM fingerprint_slot_blocks),
                       (SELECT COUNT(*) FROM fingerprint_replacement_completions)
                """, connection))
            await using (var reader = await rollback.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(1L, reader.GetInt64(2));
                Assert.Equal(0L, reader.GetInt64(3));
            }
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(3, "1000000001"));
            await using (var inventoryBefore = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_sensor_inventory", connection))
                Assert.Equal(1L, Convert.ToInt64(await inventoryBefore.ExecuteScalarAsync()));
            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => protocol.FinishReplacementAsync(request)));
            var response = Assert.Single(responses, result => result is not null);
            Assert.Equal($"{preparation.Id}\n{operation}\n3\n{nonce}\nconfirmed\ncompleted\n", response!.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_finish",
                Convert.FromHexString(request.Challenge!), Encoding.ASCII.GetBytes(response.Body), Convert.FromHexString(response.Tag)));
            var completedStatusRequest = await GuardStatusRequestAsync(operation);
            var completedStatus = Assert.IsType<EnrollmentRecoveryResponse>(await protocol.GetReplacementGuardStatusAsync(completedStatusRequest));
            Assert.Equal($"{preparation.Id}\n{operation}\n3\n{nonce}\ncompleted\n", completedStatus.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_guard_status",
                Convert.FromHexString(completedStatusRequest.Challenge!), Encoding.ASCII.GetBytes(completedStatus.Body), Convert.FromHexString(completedStatus.Tag)));
            Assert.Null(await protocol.FinishReplacementAsync(request));
            var retry = await FinishRequestAsync();
            var retryResponse = Assert.IsType<EnrollmentRecoveryResponse>(await protocol.FinishReplacementAsync(retry));
            Assert.Equal(response.Body, retryResponse.Body);
            Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_finish",
                Convert.FromHexString(retry.Challenge!), Encoding.ASCII.GetBytes(retryResponse.Body), Convert.FromHexString(retryResponse.Tag)));
            Assert.Null(await protocol.FinishReplacementAsync(retry));
            var webBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            webBuilder.Logging.ClearProviders();
            webBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            webBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString,
                ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master),
                ["EnrollmentSecurity:EnableReplacementFinishApi"] = "true",
                ["EnrollmentSecurity:EnableReplacementGuardStatusApi"] = "true"
            });
            await using (var app = webBuilder.Build())
            {
                app.MapEnrollmentRecoveryRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
                await app.StartAsync();
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
                client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
                using var challengeResponse = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish/challenge",
                    new EnrollmentRecoveryChallengeRequest("1000000001"));
                Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
                Assert.True(challengeResponse.Headers.CacheControl?.NoStore);
                var challengeText = (await challengeResponse.Content.ReadFromJsonAsync<EnrollmentRecoveryChallengeResponse>())!.Challenge;
                var challenge = Convert.FromHexString(challengeText);
                var requestBody = Encoding.ASCII.GetBytes($"{preparation.Id}\n{operation}\n3\n{nonce}\nconfirmed\n");
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_finish", challenge, requestBody);
                var httpRequest = new ReplacementFinishRequest("1000000001", preparation.Id, operation, 3,
                    challengeText, nonce, Convert.ToHexString(tag).ToLowerInvariant());
                using var httpResponse = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish", httpRequest);
                Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
                Assert.True(httpResponse.Headers.CacheControl?.NoStore);
                var httpReceipt = Assert.IsType<EnrollmentRecoveryResponse>(await httpResponse.Content.ReadFromJsonAsync<EnrollmentRecoveryResponse>());
                Assert.Equal(response.Body, httpReceipt.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_finish",
                    challenge, Encoding.ASCII.GetBytes(httpReceipt.Body), Convert.FromHexString(httpReceipt.Tag)));
                using var replay = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish", httpRequest);
                Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
                using var guardChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-status/challenge",
                    new EnrollmentRecoveryChallengeRequest("1000000001"));
                Assert.Equal(HttpStatusCode.OK, guardChallenge.StatusCode);
                var guardChallengeText = (await guardChallenge.Content.ReadFromJsonAsync<EnrollmentRecoveryChallengeResponse>())!.Challenge;
                var guardChallengeBytes = Convert.FromHexString(guardChallengeText);
                var guardBody = Encoding.ASCII.GetBytes($"{preparation.Id}\n{operation}\n3\n{nonce}\n");
                var guardTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_guard_status", guardChallengeBytes, guardBody);
                var httpGuardRequest = new EnrollmentRecoveryRequest("1000000001", preparation.Id, operation, 3,
                    guardChallengeText, nonce, Convert.ToHexString(guardTag).ToLowerInvariant());
                using var httpGuardResponse = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-status", httpGuardRequest);
                Assert.Equal(HttpStatusCode.OK, httpGuardResponse.StatusCode);
                Assert.True(httpGuardResponse.Headers.CacheControl?.NoStore);
                var httpGuardReceipt = Assert.IsType<EnrollmentRecoveryResponse>(await httpGuardResponse.Content.ReadFromJsonAsync<EnrollmentRecoveryResponse>());
                Assert.Equal(completedStatus.Body, httpGuardReceipt.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_guard_status",
                    guardChallengeBytes, Encoding.ASCII.GetBytes(httpGuardReceipt.Body), Convert.FromHexString(httpGuardReceipt.Tag)));
                using var guardReplay = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-status", httpGuardRequest);
                Assert.Equal(HttpStatusCode.Forbidden, guardReplay.StatusCode);
                await app.StopAsync();
            }
            Assert.False((await Finish(preparation.Id, "1000000001", operation, 4)).Succeeded);
            Assert.False((await Finish(preparation.Id, "1000000002", operation, 3)).Succeeded);
            Assert.False((await Finish(Guid.NewGuid().ToString("D"), "1000000001", operation, 3)).Succeeded);
            Assert.False((await guards.FinalizeConfirmedWriteAsync(preparation.Id, "1000000001", operation)).Succeeded);
            Assert.NotNull(await repository.FindTeacherByFingerprintTemplateIdAsync(3, "1000000001"));
            Assert.DoesNotContain(3, await repository.GetBlockedFingerprintSlotsAsync());
            await using (var inventoryAfter = new MySqlCommand("""
                SELECT (SELECT COUNT(*) FROM fingerprint_sensor_inventory),
                       (SELECT COUNT(*) FROM fingerprint_enrollment_challenges WHERE challenge_hash = @challenge)
                """, connection))
            {
                inventoryAfter.Parameters.AddWithValue("@challenge", SHA256.HashData(inventoryChallenge));
                await using var reader = await inventoryAfter.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0L, reader.GetInt64(0));
                Assert.Equal(0L, reader.GetInt64(1));
            }
            await using (var receipt = new MySqlCommand("""
                SELECT r.preparation_id, r.device_id, r.template_identifier, r.replacement_template_id = f.id,
                       (SELECT COUNT(*) FROM fingerprint_enrollment_completions),
                       (SELECT COUNT(*) FROM fingerprint_templates)
                FROM fingerprint_replacement_completions r
                INNER JOIN fingerprint_templates f ON f.template_identifier = r.template_identifier
                WHERE r.operation_id = @operation
                """, connection))
            {
                receipt.Parameters.AddWithValue("@operation", operation);
                await using var reader = await receipt.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(preparation.Id, reader.GetString(0));
                Assert.Equal("1000000001", reader.GetString(1));
                Assert.Equal(3, reader.GetInt64(2));
                Assert.True(reader.GetBoolean(3));
                Assert.Equal(0L, reader.GetInt64(4));
                Assert.Equal(1L, reader.GetInt64(5));
            }
            await ExecuteAsync(connection, """
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier) VALUES (1, 'Left thumb', 4);
                INSERT INTO fingerprint_template_device_bindings (fingerprint_template_id, device_id)
                    SELECT id, '1000000001' FROM fingerprint_templates WHERE template_identifier = 4;
                """);
            Assert.True((await repository.PrepareFingerprintReplacementAsync(1, 1, "Left thumb", 4)).Succeeded);
            var secondPreparation = Assert.Single(await repository.GetFingerprintEnrollmentPreparationsAsync());
            Assert.True((await repository.AuthorizeFingerprintReplacementAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id)).Succeeded);
            string secondOperation;
            await using (var consent = new MySqlCommand("SELECT operation_id FROM fingerprint_enrollment_authorizations WHERE preparation_id = @id", connection))
            {
                consent.Parameters.AddWithValue("@id", secondPreparation.Id);
                secondOperation = (string)(await consent.ExecuteScalarAsync())!;
            }
            var reserveBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            reserveBuilder.Logging.ClearProviders();
            reserveBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            reserveBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString,
                ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master),
                ["EnrollmentSecurity:EnableReplacementGuardReserveApi"] = "true",
                ["EnrollmentSecurity:EnableReplacementWriteFenceApi"] = "true",
                ["EnrollmentSecurity:EnableReplacementAttemptClaimApi"] = "true",
                ["EnrollmentSecurity:EnableReplacementAttemptFenceApi"] = "true",
                ["EnrollmentSecurity:EnableReplacementAttemptFinishApi"] = "true"
            });
            await using (var app = reserveBuilder.Build())
            {
                app.MapEnrollmentRecoveryRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
                await app.StartAsync();
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
                client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
                using var challengeResponse = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve/challenge",
                    new EnrollmentRecoveryChallengeRequest("1000000001"));
                Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
                Assert.True(challengeResponse.Headers.CacheControl?.NoStore);
                var challengeText = (await challengeResponse.Content.ReadFromJsonAsync<EnrollmentRecoveryChallengeResponse>())!.Challenge;
                var challenge = Convert.FromHexString(challengeText);
                var body = Encoding.ASCII.GetBytes($"{secondPreparation.Id}\n{secondOperation}\n4\n{nonce}\n");
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_guard_reserve", challenge, body);
                var httpRequest = new EnrollmentRecoveryRequest("1000000001", secondPreparation.Id, secondOperation, 4,
                    challengeText, nonce, Convert.ToHexString(tag).ToLowerInvariant());
                using var httpResponse = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve", httpRequest);
                Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
                Assert.True(httpResponse.Headers.CacheControl?.NoStore);
                var httpReceipt = Assert.IsType<EnrollmentRecoveryResponse>(await httpResponse.Content.ReadFromJsonAsync<EnrollmentRecoveryResponse>());
                Assert.Equal($"{secondPreparation.Id}\n{secondOperation}\n4\n{nonce}\nguarded_only\n", httpReceipt.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_guard_reserve",
                    challenge, Encoding.ASCII.GetBytes(httpReceipt.Body), Convert.FromHexString(httpReceipt.Tag)));
                using var replay = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve", httpRequest);
                Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
                Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));

                async Task<EnrollmentRecoveryRequest> WriteClaimRequestAsync(string op, ushort slot = 4, string purpose = "replacement_write_claim")
                {
                    var claimChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", purpose)).Challenge);
                    var claimBody = Encoding.ASCII.GetBytes($"{secondPreparation.Id}\n{op}\n{slot}\n{nonce}\n");
                    var claimTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_write_claim", claimChallenge, claimBody);
                    return new("1000000001", secondPreparation.Id, op, slot, Convert.ToHexString(claimChallenge).ToLowerInvariant(),
                        nonce, Convert.ToHexString(claimTag).ToLowerInvariant());
                }
                Assert.Null(await protocol.ClaimReplacementWriteAsync(await WriteClaimRequestAsync(secondOperation, 3)));
                Assert.Null(await protocol.ClaimReplacementWriteAsync(await WriteClaimRequestAsync(Guid.NewGuid().ToString("D"))));
                Assert.Null(await protocol.ClaimReplacementWriteAsync(await WriteClaimRequestAsync(secondOperation, purpose: "finish")));
                await ExecuteAsync(connection, "UPDATE fingerprint_replacement_guard_authorizations SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
                Assert.Null(await protocol.ClaimReplacementWriteAsync(await WriteClaimRequestAsync(secondOperation)));
                await ExecuteAsync(connection, "UPDATE fingerprint_replacement_guard_authorizations SET expires_utc = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 1 MINUTE)");
                await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 2");
                Assert.Null(await protocol.ClaimReplacementWriteAsync(await WriteClaimRequestAsync(secondOperation)));
                await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 2");
                await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 4)");
                Assert.Null(await protocol.ClaimReplacementWriteAsync(await WriteClaimRequestAsync(secondOperation)));
                await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 4)");
                var intents = new FingerprintReplacementRecoveryIntent(configuration);
                var racingClaimRequest = await WriteClaimRequestAsync(secondOperation);
                var raceClaim = protocol.ClaimReplacementWriteAsync(racingClaimRequest);
                var raceIntent = intents.RecordAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation);
                await Task.WhenAll(raceClaim, raceIntent);
                Assert.NotEqual(await raceClaim is not null, (await raceIntent).Succeeded);
                Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));
                await ExecuteAsync(connection, "DELETE FROM fingerprint_replacement_write_claims; DELETE FROM fingerprint_replacement_recovery_intents");
                var preClaimFenceChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "replacement_write_fence")).Challenge);
                var preClaimFenceBody = Encoding.ASCII.GetBytes($"{secondPreparation.Id}\n{secondOperation}\n4\n{nonce}\nfenced\n");
                var preClaimFenceTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_write_fence",
                    preClaimFenceChallenge, preClaimFenceBody);
                var preClaimFence = new EnrollmentRecoveryRequest("1000000001", secondPreparation.Id, secondOperation, 4,
                    Convert.ToHexString(preClaimFenceChallenge).ToLowerInvariant(), nonce, Convert.ToHexString(preClaimFenceTag).ToLowerInvariant());
                var preFenceClaim = await WriteClaimRequestAsync(secondOperation);
                var concurrentFence = protocol.FenceReplacementWriteAsync(preClaimFence);
                var concurrentClaim = protocol.ClaimReplacementWriteAsync(preFenceClaim);
                await Task.WhenAll(concurrentFence, concurrentClaim);
                Assert.NotNull(await concurrentFence);
                Assert.Null(await protocol.ClaimReplacementWriteAsync(await WriteClaimRequestAsync(secondOperation)));
                Assert.False((await guards.FinalizeConfirmedReplacementAsync(secondPreparation.Id, "1000000001", secondOperation, 4)).Succeeded);
                await ExecuteAsync(connection, "DELETE FROM fingerprint_replacement_write_fences; DELETE FROM fingerprint_replacement_write_claims");
                Assert.True((await intents.RecordAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation)).Succeeded);
                Assert.Null(await protocol.ClaimReplacementWriteAsync(await WriteClaimRequestAsync(secondOperation)));
                await ExecuteAsync(connection, "DELETE FROM fingerprint_replacement_recovery_intents");
                var failedClaim = await WriteClaimRequestAsync(secondOperation);
                Assert.Null(await protocol.ClaimReplacementWriteAsync(failedClaim with { Tag = new string('0', 64) }));
                await ExecuteAsync(connection, """
                    CREATE TRIGGER reject_replacement_claim BEFORE INSERT ON fingerprint_replacement_write_claims
                    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated claim failure'
                    """);
                await Assert.ThrowsAsync<MySqlException>(() => protocol.ClaimReplacementWriteAsync(failedClaim));
                await ExecuteAsync(connection, "DROP TRIGGER reject_replacement_claim");
                var competing = await WriteClaimRequestAsync(secondOperation);
                var claimResults = await Task.WhenAll(protocol.ClaimReplacementWriteAsync(failedClaim), protocol.ClaimReplacementWriteAsync(competing));
                var writeReceipt = Assert.Single(claimResults.OfType<EnrollmentRecoveryResponse>());
                var winningClaimRequest = claimResults[0] is not null ? failedClaim : competing;
                Assert.Equal($"{secondPreparation.Id}\n{secondOperation}\n4\n{nonce}\ngranted\n", writeReceipt.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_write_claim",
                    Convert.FromHexString(winningClaimRequest.Challenge!), Encoding.ASCII.GetBytes(writeReceipt.Body), Convert.FromHexString(writeReceipt.Tag)));
                Assert.Null(await protocol.ClaimReplacementWriteAsync(winningClaimRequest));
                Assert.Null(await protocol.ClaimReplacementWriteAsync(await WriteClaimRequestAsync(secondOperation)));
                Assert.False((await intents.RecordAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation)).Succeeded);
                Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));
                async Task<EnrollmentRecoveryRequest> FenceRequestAsync(string op, ushort slot = 4, string purpose = "replacement_write_fence")
                {
                    var fenceChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", purpose)).Challenge);
                    var fenceBody = Encoding.ASCII.GetBytes($"{secondPreparation.Id}\n{op}\n{slot}\n{nonce}\nfenced\n");
                    var fenceTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_write_fence", fenceChallenge, fenceBody);
                    return new("1000000001", secondPreparation.Id, op, slot, Convert.ToHexString(fenceChallenge).ToLowerInvariant(),
                        nonce, Convert.ToHexString(fenceTag).ToLowerInvariant());
                }
                Assert.Null(await protocol.FenceReplacementWriteAsync(await FenceRequestAsync(secondOperation, 3)));
                Assert.Null(await protocol.FenceReplacementWriteAsync(await FenceRequestAsync(Guid.NewGuid().ToString("D"))));
                Assert.Null(await protocol.FenceReplacementWriteAsync(await FenceRequestAsync(secondOperation, purpose: "finish")));
                var fenceRequest = await FenceRequestAsync(secondOperation);
                Assert.Null(await protocol.FenceReplacementWriteAsync(fenceRequest with { Tag = new string('0', 64) }));
                await ExecuteAsync(connection, """
                    CREATE TRIGGER reject_replacement_fence BEFORE INSERT ON fingerprint_replacement_write_fences
                    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated fence failure'
                    """);
                await Assert.ThrowsAsync<MySqlException>(() => protocol.FenceReplacementWriteAsync(fenceRequest));
                await ExecuteAsync(connection, "DROP TRIGGER reject_replacement_fence");
                using var fenceHttp = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-fence", fenceRequest);
                Assert.Equal(HttpStatusCode.OK, fenceHttp.StatusCode);
                Assert.True(fenceHttp.Headers.CacheControl?.NoStore);
                var fenceResponse = Assert.IsType<EnrollmentRecoveryResponse>(await fenceHttp.Content.ReadFromJsonAsync<EnrollmentRecoveryResponse>());
                Assert.Equal($"{secondPreparation.Id}\n{secondOperation}\n4\n{nonce}\nfenced\nfenced_only\n", fenceResponse.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_write_fence",
                    Convert.FromHexString(fenceRequest.Challenge!), Encoding.ASCII.GetBytes(fenceResponse.Body), Convert.FromHexString(fenceResponse.Tag)));
                using var fenceReplay = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-fence", fenceRequest);
                Assert.Equal(HttpStatusCode.Forbidden, fenceReplay.StatusCode);
                Assert.NotNull(await protocol.FenceReplacementWriteAsync(await FenceRequestAsync(secondOperation)));
                Assert.Null(await protocol.ClaimReplacementWriteAsync(await WriteClaimRequestAsync(secondOperation)));
                Assert.False((await guards.FinalizeConfirmedReplacementAsync(secondPreparation.Id, "1000000001", secondOperation, 4)).Succeeded);
                Assert.True((await intents.RecordAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation)).Succeeded);
                Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));
                Guid recoveryOperation;
                await using (var recoveryQuery = new MySqlCommand("SELECT recovery_operation_id FROM fingerprint_replacement_recovery_intents WHERE preparation_id = @id", connection))
                {
                    recoveryQuery.Parameters.AddWithValue("@id", secondPreparation.Id);
                    recoveryOperation = Guid.Parse((string)(await recoveryQuery.ExecuteScalarAsync())!);
                }
                var preparationGuid = Guid.Parse(secondPreparation.Id);
                var guardedGuid = Guid.Parse(secondOperation);
                // Simulate upgrading an existing guarded intent and retrying an interrupted migration.
                await ExecuteAsync(connection, "DELETE FROM fingerprint_recovery_intent_history; DELETE FROM schema_versions WHERE version_key = '034_fingerprint_recovery_intent_history'");
                await migrator.RunMigrationsAsync();
                await ExecuteAsync(connection, "DELETE FROM schema_versions WHERE version_key = '034_fingerprint_recovery_intent_history'");
                await migrator.RunMigrationsAsync();
                await using (var backfilled = new MySqlCommand("SELECT recovery_operation_id FROM fingerprint_recovery_intent_history WHERE preparation_id = @id", connection))
                {
                    backfilled.Parameters.AddWithValue("@id", secondPreparation.Id);
                    Assert.Equal(recoveryOperation.ToString("D"), (string)(await backfilled.ExecuteScalarAsync())!);
                }
                using (var attemptFenceChallengeHttp = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-fence/challenge",
                    new EnrollmentRecoveryChallengeRequest("1000000001")))
                {
                    Assert.Equal(HttpStatusCode.OK, attemptFenceChallengeHttp.StatusCode);
                    Assert.True(attemptFenceChallengeHttp.Headers.CacheControl?.NoStore);
                    Assert.Equal(64, (await attemptFenceChallengeHttp.Content.ReadFromJsonAsync<EnrollmentRecoveryChallengeResponse>())!.Challenge.Length);
                }
                async Task<ReplacementAttemptReserveRequest> RecoveryClaimRequestAsync(Guid recovery, ushort slot = 4, string purpose = "replacement_attempt_claim")
                {
                    var claimChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", purpose)).Challenge);
                    var claimBody = Encoding.ASCII.GetBytes($"{secondPreparation.Id}\n{secondOperation}\n{recovery:D}\n{slot}\n{nonce}\n");
                    var claimTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_attempt_claim", claimChallenge, claimBody);
                    return new("1000000001", secondPreparation.Id, secondOperation, recovery.ToString("D"), slot,
                        Convert.ToHexString(claimChallenge).ToLowerInvariant(), nonce, Convert.ToHexString(claimTag).ToLowerInvariant());
                }
                async Task<ReplacementAttemptReserveRequest> RecoveryFinishRequestAsync(Guid recovery, ushort slot = 4, string purpose = "replacement_attempt_finish")
                {
                    var finishChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", purpose)).Challenge);
                    var finishBody = Encoding.ASCII.GetBytes($"{secondPreparation.Id}\n{secondOperation}\n{recovery:D}\n{slot}\n{nonce}\nconfirmed\n");
                    var finishTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_attempt_finish", finishChallenge, finishBody);
                    return new("1000000001", secondPreparation.Id, secondOperation, recovery.ToString("D"), slot,
                        Convert.ToHexString(finishChallenge).ToLowerInvariant(), nonce, Convert.ToHexString(finishTag).ToLowerInvariant());
                }
                async Task<ReplacementAttemptReserveRequest> AttemptFenceRequestAsync(Guid recovery, ushort slot = 4, string purpose = "replacement_attempt_fence")
                {
                    var fenceChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", purpose)).Challenge);
                    var fenceBody = Encoding.ASCII.GetBytes($"{secondPreparation.Id}\n{secondOperation}\n{recovery:D}\n{slot}\n{nonce}\nfenced\n");
                    var fenceTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "replacement_attempt_fence", fenceChallenge, fenceBody);
                    return new("1000000001", secondPreparation.Id, secondOperation, recovery.ToString("D"), slot,
                        Convert.ToHexString(fenceChallenge).ToLowerInvariant(), nonce, Convert.ToHexString(fenceTag).ToLowerInvariant());
                }
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation)));
                Assert.False((await intents.RecordRetryAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation, recoveryOperation.ToString("D"))).Succeeded);
                Assert.Null(await protocol.FenceReplacementAttemptAsync(await AttemptFenceRequestAsync(Guid.NewGuid())));
                Assert.Null(await protocol.FenceReplacementAttemptAsync(await AttemptFenceRequestAsync(recoveryOperation, 3)));
                var preReserveFence = await AttemptFenceRequestAsync(recoveryOperation);
                Assert.Null(await protocol.FenceReplacementAttemptAsync(preReserveFence with { Tag = new string('0', 64) }));
                await ExecuteAsync(connection, "UPDATE fingerprint_replacement_recovery_intents SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
                Assert.True((await intents.RecordAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation)).Succeeded);
                Guid renewedRecovery;
                await using (var renewedIntent = new MySqlCommand("SELECT recovery_operation_id FROM fingerprint_replacement_recovery_intents WHERE preparation_id = @id", connection))
                {
                    renewedIntent.Parameters.AddWithValue("@id", secondPreparation.Id);
                    renewedRecovery = Guid.Parse((string)(await renewedIntent.ExecuteScalarAsync())!);
                }
                await using (var historyCount = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_recovery_intent_history WHERE preparation_id = @id", connection))
                {
                    historyCount.Parameters.AddWithValue("@id", secondPreparation.Id);
                    Assert.True(Convert.ToInt32(await historyCount.ExecuteScalarAsync()) >= 2);
                }
                Assert.True((await intents.ReserveAttemptAsync("1000000001", preparationGuid, guardedGuid, renewedRecovery, 4)).Succeeded);
                Assert.Null(await protocol.FenceReplacementAttemptAsync(await AttemptFenceRequestAsync(recoveryOperation)));
                await ExecuteAsync(connection, "DELETE FROM fingerprint_replacement_recovery_attempts");
                await ExecuteAsync(connection, """
                    CREATE TRIGGER reject_attempt_fence BEFORE INSERT ON fingerprint_recovery_attempt_fences
                    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated attempt fence failure'
                    """);
                await Assert.ThrowsAsync<MySqlException>(() => protocol.FenceReplacementAttemptAsync(preReserveFence));
                await ExecuteAsync(connection, "DROP TRIGGER reject_attempt_fence");
                using (var preReserveResponse = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-fence", preReserveFence))
                {
                    Assert.Equal(HttpStatusCode.OK, preReserveResponse.StatusCode);
                    Assert.True(preReserveResponse.Headers.CacheControl?.NoStore);
                    var fenceReceipt = Assert.IsType<EnrollmentRecoveryResponse>(await preReserveResponse.Content.ReadFromJsonAsync<EnrollmentRecoveryResponse>());
                    Assert.Equal($"{secondPreparation.Id}\n{secondOperation}\n{recoveryOperation:D}\n4\n{nonce}\nfenced\nfenced_only\n", fenceReceipt.Body);
                    Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_attempt_fence",
                        Convert.FromHexString(preReserveFence.Challenge!), Encoding.ASCII.GetBytes(fenceReceipt.Body), Convert.FromHexString(fenceReceipt.Tag)));
                }
                Assert.Null(await protocol.FenceReplacementAttemptAsync(preReserveFence));
                Assert.NotNull(await protocol.FenceReplacementAttemptAsync(await AttemptFenceRequestAsync(recoveryOperation)));
                await using (var restoreExpiredIntent = new MySqlCommand("UPDATE fingerprint_replacement_recovery_intents SET recovery_operation_id = @id, expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND) WHERE preparation_id = @preparation", connection))
                {
                    restoreExpiredIntent.Parameters.AddWithValue("@id", recoveryOperation.ToString("D"));
                    restoreExpiredIntent.Parameters.AddWithValue("@preparation", secondPreparation.Id);
                    Assert.Equal(1, await restoreExpiredIntent.ExecuteNonQueryAsync());
                }
                Assert.True((await intents.RecordRetryAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation, recoveryOperation.ToString("D"))).Succeeded);
                await using (var checkSuccessor = new MySqlCommand("SELECT recovery_operation_id FROM fingerprint_replacement_recovery_intents WHERE preparation_id = @preparation", connection))
                {
                    checkSuccessor.Parameters.AddWithValue("@preparation", secondPreparation.Id);
                    Assert.NotEqual(recoveryOperation.ToString("D"), (string)(await checkSuccessor.ExecuteScalarAsync())!);
                }
                Assert.NotNull(await protocol.FenceReplacementAttemptAsync(await AttemptFenceRequestAsync(recoveryOperation)));
                await using (var restoreIntent = new MySqlCommand("UPDATE fingerprint_replacement_recovery_intents SET recovery_operation_id = @id, expires_utc = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 1 MINUTE) WHERE preparation_id = @preparation", connection))
                {
                    restoreIntent.Parameters.AddWithValue("@id", recoveryOperation.ToString("D"));
                    restoreIntent.Parameters.AddWithValue("@preparation", secondPreparation.Id);
                    Assert.Equal(1, await restoreIntent.ExecuteNonQueryAsync());
                }
                Assert.False((await intents.ReserveAttemptAsync("1000000001", preparationGuid, guardedGuid, recoveryOperation, 4)).Succeeded);
                Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));
                // Reset only the disposable integration-test database to exercise a separate successful attempt.
                await ExecuteAsync(connection, "DELETE FROM fingerprint_recovery_attempt_fences");
                var racingFence = await AttemptFenceRequestAsync(recoveryOperation);
                var attemptFenceTask = protocol.FenceReplacementAttemptAsync(racingFence);
                var attemptReserveTask = intents.ReserveAttemptAsync("1000000001", preparationGuid, guardedGuid, recoveryOperation, 4);
                await Task.WhenAll(attemptFenceTask, attemptReserveTask);
                Assert.NotNull(await attemptFenceTask);
                Assert.False((await intents.ReserveAttemptAsync("1000000001", preparationGuid, guardedGuid, recoveryOperation, 4)).Succeeded);
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation)));
                await ExecuteAsync(connection, "DELETE FROM fingerprint_replacement_recovery_attempts; DELETE FROM fingerprint_recovery_attempt_fences");
                Assert.True((await intents.ReserveAttemptAsync("1000000001", preparationGuid, guardedGuid, recoveryOperation, 4)).Succeeded);
                var concurrentAttemptFence = await AttemptFenceRequestAsync(recoveryOperation);
                var concurrentAttemptClaim = await RecoveryClaimRequestAsync(recoveryOperation);
                var fenceTask = protocol.FenceReplacementAttemptAsync(concurrentAttemptFence);
                var claimTask = protocol.ClaimReplacementAttemptAsync(concurrentAttemptClaim);
                await Task.WhenAll(fenceTask, claimTask);
                Assert.NotNull(await fenceTask);
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation)));
                Assert.Null(await protocol.FinishReplacementAttemptAsync(await RecoveryFinishRequestAsync(recoveryOperation)));
                Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));
                await ExecuteAsync(connection, "DELETE FROM fingerprint_recovery_write_claims; DELETE FROM fingerprint_recovery_attempt_fences");
                Assert.Null(await protocol.FinishReplacementAttemptAsync(await RecoveryFinishRequestAsync(recoveryOperation)));
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(Guid.NewGuid())));
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation, 3)));
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation, purpose: "finish")));
                await ExecuteAsync(connection, "UPDATE fingerprint_replacement_recovery_intents SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)");
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation)));
                await ExecuteAsync(connection, "UPDATE fingerprint_replacement_recovery_intents SET expires_utc = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 1 MINUTE)");
                await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 2");
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation)));
                await ExecuteAsync(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 2");
                await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 4)");
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation)));
                await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 4)");
                var recoveryClaim = await RecoveryClaimRequestAsync(recoveryOperation);
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(recoveryClaim with { Tag = new string('0', 64) }));
                await ExecuteAsync(connection, """
                    CREATE TRIGGER reject_recovery_claim BEFORE INSERT ON fingerprint_recovery_write_claims
                    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated recovery claim failure'
                    """);
                await Assert.ThrowsAsync<MySqlException>(() => protocol.ClaimReplacementAttemptAsync(recoveryClaim));
                await ExecuteAsync(connection, "DROP TRIGGER reject_recovery_claim");
                var recoveryClaim2 = await RecoveryClaimRequestAsync(recoveryOperation);
                var recoveryClaims = await Task.WhenAll(
                    client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-claim", recoveryClaim),
                    client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-claim", recoveryClaim2));
                using var firstRecoveryResponse = recoveryClaims[0];
                using var secondRecoveryResponse = recoveryClaims[1];
                var winningRecoveryResponse = Assert.Single(recoveryClaims, response => response.StatusCode == HttpStatusCode.OK);
                Assert.Single(recoveryClaims, response => response.StatusCode == HttpStatusCode.Forbidden);
                Assert.True(winningRecoveryResponse.Headers.CacheControl?.NoStore);
                var recoveryReceipt = Assert.IsType<EnrollmentRecoveryResponse>(await winningRecoveryResponse.Content.ReadFromJsonAsync<EnrollmentRecoveryResponse>());
                var successfulRecoveryClaim = ReferenceEquals(winningRecoveryResponse, firstRecoveryResponse) ? recoveryClaim : recoveryClaim2;
                Assert.Equal($"{secondPreparation.Id}\n{secondOperation}\n{recoveryOperation:D}\n4\n{nonce}\ngranted\n", recoveryReceipt.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_attempt_claim",
                    Convert.FromHexString(successfulRecoveryClaim.Challenge!), Encoding.ASCII.GetBytes(recoveryReceipt.Body), Convert.FromHexString(recoveryReceipt.Tag)));
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(successfulRecoveryClaim));
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation)));
                Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));
                Assert.NotNull(await protocol.FenceReplacementAttemptAsync(await AttemptFenceRequestAsync(recoveryOperation)));
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation)));
                Assert.Null(await protocol.FinishReplacementAttemptAsync(await RecoveryFinishRequestAsync(recoveryOperation)));
                Assert.False((await intents.RecordAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation)).Succeeded);
                Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));
                Assert.False((await intents.RecordRetryAsync(1, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation, recoveryOperation.ToString("D"))).Succeeded);
                Assert.False((await intents.RecordRetryAsync(2, 1, 1, "Left thumb", 3, secondPreparation.Id, secondOperation, recoveryOperation.ToString("D"))).Succeeded);
                Assert.False((await intents.RecordRetryAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation, Guid.NewGuid().ToString("D"))).Succeeded);
                await ExecuteAsync(connection, """
                    CREATE TRIGGER reject_retry_intent BEFORE UPDATE ON fingerprint_replacement_recovery_intents
                    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated retry intent failure'
                    """);
                await Assert.ThrowsAsync<MySqlException>(() => intents.RecordRetryAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation, recoveryOperation.ToString("D")));
                await ExecuteAsync(connection, "DROP TRIGGER reject_retry_intent");
                await ExecuteAsync(connection, """
                    CREATE TRIGGER reject_retry_history BEFORE INSERT ON fingerprint_recovery_intent_history
                    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated retry history failure'
                    """);
                await Assert.ThrowsAsync<MySqlException>(() => intents.RecordRetryAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation, recoveryOperation.ToString("D")));
                await ExecuteAsync(connection, "DROP TRIGGER reject_retry_history");
                await using (var retryRollback = new MySqlCommand("""
                    SELECT (SELECT COUNT(*) FROM fingerprint_recovery_write_claims),
                           (SELECT COUNT(*) FROM fingerprint_replacement_recovery_attempts),
                           (SELECT COUNT(*) FROM fingerprint_recovery_attempt_fences),
                           (SELECT COUNT(*) FROM fingerprint_slot_blocks)
                    """, connection))
                await using (var reader = await retryRollback.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(1L, reader.GetInt64(0));
                    Assert.Equal(1L, reader.GetInt64(1));
                    Assert.Equal(1L, reader.GetInt64(2));
                    Assert.Equal(1L, reader.GetInt64(3));
                }
                var retry1 = intents.RecordRetryAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation, recoveryOperation.ToString("D"));
                var retry2 = intents.RecordRetryAsync(2, 1, 1, "Left thumb", 4, secondPreparation.Id, secondOperation, recoveryOperation.ToString("D"));
                var retryResults = await Task.WhenAll(retry1, retry2);
                Assert.Single(retryResults, result => result.Succeeded);
                Guid nextRecovery;
                await using (var nextQuery = new MySqlCommand("SELECT recovery_operation_id FROM fingerprint_replacement_recovery_intents WHERE preparation_id = @id", connection))
                {
                    nextQuery.Parameters.AddWithValue("@id", secondPreparation.Id);
                    nextRecovery = Guid.Parse((string)(await nextQuery.ExecuteScalarAsync())!);
                }
                Assert.NotEqual(recoveryOperation, nextRecovery);
                Assert.Null(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(recoveryOperation)));
                Assert.Null(await protocol.FinishReplacementAttemptAsync(await RecoveryFinishRequestAsync(recoveryOperation)));
                Assert.True((await intents.ReserveAttemptAsync("1000000001", preparationGuid, guardedGuid, nextRecovery, 4)).Succeeded);
                Assert.NotNull(await protocol.ClaimReplacementAttemptAsync(await RecoveryClaimRequestAsync(nextRecovery)));
                Assert.NotNull(await protocol.FenceReplacementAttemptAsync(await AttemptFenceRequestAsync(recoveryOperation)));
                Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));
                // Reset only the disposable integration-test database for the original completion checks below.
                await ExecuteAsync(connection, "DELETE FROM fingerprint_recovery_write_claims; DELETE FROM fingerprint_replacement_recovery_attempts; DELETE FROM fingerprint_recovery_attempt_fences");
                await using (var restoreAttempt = new MySqlCommand("""
                    UPDATE fingerprint_replacement_recovery_intents SET recovery_operation_id = @recovery
                        WHERE preparation_id = @preparation;
                    INSERT INTO fingerprint_replacement_recovery_attempts (preparation_id, guarded_operation_id, recovery_operation_id)
                        VALUES (@preparation, @guarded, @recovery);
                    INSERT INTO fingerprint_recovery_write_claims (preparation_id, guarded_operation_id, recovery_operation_id)
                        VALUES (@preparation, @guarded, @recovery);
                    """, connection))
                {
                    restoreAttempt.Parameters.AddWithValue("@preparation", secondPreparation.Id);
                    restoreAttempt.Parameters.AddWithValue("@guarded", secondOperation);
                    restoreAttempt.Parameters.AddWithValue("@recovery", recoveryOperation.ToString("D"));
                    await restoreAttempt.ExecuteNonQueryAsync();
                }
                Assert.Null(await protocol.FinishReplacementAttemptAsync(await RecoveryFinishRequestAsync(Guid.NewGuid())));
                Assert.Null(await protocol.FinishReplacementAttemptAsync(await RecoveryFinishRequestAsync(recoveryOperation, 3)));
                Assert.Null(await protocol.FinishReplacementAttemptAsync(await RecoveryFinishRequestAsync(recoveryOperation, purpose: "finish")));
                await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000002' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 4)");
                Assert.Null(await protocol.FinishReplacementAttemptAsync(await RecoveryFinishRequestAsync(recoveryOperation)));
                await ExecuteAsync(connection, "UPDATE fingerprint_template_device_bindings SET device_id = '1000000001' WHERE fingerprint_template_id = (SELECT id FROM fingerprint_templates WHERE template_identifier = 4)");
                var recoveryFinish = await RecoveryFinishRequestAsync(recoveryOperation);
                Assert.Null(await protocol.FinishReplacementAttemptAsync(recoveryFinish with { Tag = new string('0', 64) }));
                Assert.Null(await protocol.FinishReplacementAttemptAsync(recoveryFinish with { ClientNonce = new string('0', 64) }));
                Assert.Null(await protocol.FinishReplacementAttemptAsync(recoveryFinish with { DeviceId = "1000000002" }));
                var expiredRecoveryFinish = await RecoveryFinishRequestAsync(recoveryOperation);
                await using (var expireFinish = new MySqlCommand("UPDATE fingerprint_enrollment_challenges SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND) WHERE challenge_hash = @hash", connection))
                {
                    expireFinish.Parameters.AddWithValue("@hash", SHA256.HashData(Convert.FromHexString(expiredRecoveryFinish.Challenge!)));
                    Assert.Equal(1, await expireFinish.ExecuteNonQueryAsync());
                }
                Assert.Null(await protocol.FinishReplacementAttemptAsync(expiredRecoveryFinish));
                await ExecuteAsync(connection, """
                    CREATE TRIGGER reject_recovery_cleanup BEFORE DELETE ON fingerprint_slot_blocks
                    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated recovery cleanup failure'
                    """);
                await Assert.ThrowsAsync<MySqlException>(() => protocol.FinishReplacementAttemptAsync(recoveryFinish));
                await ExecuteAsync(connection, "DROP TRIGGER reject_recovery_cleanup");
                Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));
                await using (var rollbackRecovery = new MySqlCommand("""
                    SELECT (SELECT COUNT(*) FROM fingerprint_enrollment_write_guards),
                           (SELECT COUNT(*) FROM fingerprint_replacement_recovery_attempts),
                           (SELECT COUNT(*) FROM fingerprint_recovery_write_claims),
                           (SELECT COUNT(*) FROM fingerprint_slot_blocks),
                           (SELECT COUNT(*) FROM fingerprint_recovery_completions)
                    """, connection))
                await using (var rollbackReader = await rollbackRecovery.ExecuteReaderAsync())
                {
                    Assert.True(await rollbackReader.ReadAsync());
                    Assert.Equal(1L, rollbackReader.GetInt64(0));
                    Assert.Equal(1L, rollbackReader.GetInt64(1));
                    Assert.Equal(1L, rollbackReader.GetInt64(2));
                    Assert.Equal(1L, rollbackReader.GetInt64(3));
                    Assert.Equal(0L, rollbackReader.GetInt64(4));
                }
                var secondFinish = await RecoveryFinishRequestAsync(recoveryOperation);
                var concurrentFinishes = await Task.WhenAll(
                    client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-finish", recoveryFinish),
                    client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-finish", secondFinish));
                using var finishHttp = concurrentFinishes[0];
                using var parallelFinishHttp = concurrentFinishes[1];
                Assert.Equal(HttpStatusCode.OK, finishHttp.StatusCode);
                Assert.Equal(HttpStatusCode.OK, parallelFinishHttp.StatusCode);
                Assert.True(finishHttp.Headers.CacheControl?.NoStore);
                var finishReceipt = Assert.IsType<EnrollmentRecoveryResponse>(await finishHttp.Content.ReadFromJsonAsync<EnrollmentRecoveryResponse>());
                Assert.Equal($"{secondPreparation.Id}\n{secondOperation}\n{recoveryOperation:D}\n4\n{nonce}\nconfirmed\ncompleted\n", finishReceipt.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "replacement_attempt_finish",
                    Convert.FromHexString(recoveryFinish.Challenge!), Encoding.ASCII.GetBytes(finishReceipt.Body), Convert.FromHexString(finishReceipt.Tag)));
                using var finishReplay = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-finish", recoveryFinish);
                Assert.Equal(HttpStatusCode.Forbidden, finishReplay.StatusCode);
                var retryFinish = await RecoveryFinishRequestAsync(recoveryOperation);
                Assert.Equal(finishReceipt.Body, Assert.IsType<EnrollmentRecoveryResponse>(await protocol.FinishReplacementAttemptAsync(retryFinish)).Body);
                Assert.NotNull(await repository.FindTeacherByFingerprintTemplateIdAsync(4, "1000000001"));
                await using (var clearedHistory = new MySqlCommand("SELECT COUNT(*) FROM fingerprint_recovery_intent_history WHERE preparation_id = @id", connection))
                {
                    clearedHistory.Parameters.AddWithValue("@id", secondPreparation.Id);
                    Assert.Equal(0L, Convert.ToInt64(await clearedHistory.ExecuteScalarAsync()));
                }
                await using (var remaining = new MySqlCommand("""
                    SELECT (SELECT COUNT(*) FROM fingerprint_recovery_completions),
                           (SELECT COUNT(*) FROM fingerprint_enrollment_write_guards),
                           (SELECT COUNT(*) FROM fingerprint_replacement_recovery_attempts),
                           (SELECT COUNT(*) FROM fingerprint_recovery_write_claims),
                           (SELECT COUNT(*) FROM fingerprint_slot_blocks),
                           (SELECT COUNT(*) FROM fingerprint_templates)
                    """, connection))
                await using (var remainingReader = await remaining.ExecuteReaderAsync())
                {
                    Assert.True(await remainingReader.ReadAsync());
                    Assert.Equal(1L, remainingReader.GetInt64(0));
                    Assert.Equal(0L, remainingReader.GetInt64(1));
                    Assert.Equal(0L, remainingReader.GetInt64(2));
                    Assert.Equal(0L, remainingReader.GetInt64(3));
                    Assert.Equal(0L, remainingReader.GetInt64(4));
                    Assert.Equal(2L, remainingReader.GetInt64(5));
                }
                await app.StopAsync();
            }
        }
        finally
        {
            if (created) await ExecuteAsync(admin, $"DROP DATABASE `{database}`");
        }
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record EnrollmentRecoveryChallengeResponse(string Challenge);
}
