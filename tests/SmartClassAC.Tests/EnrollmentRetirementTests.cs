using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class EnrollmentRetirementTests
{
    [MariaDbFact]
    public async Task RetirementRequiresDeletedIdentityAndExactDatabaseAndRejectsOrphanCredentials()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCLASS_TEST_CONNECTION")!)
        {
            Database = string.Empty
        };
        var database = "smartclass_retire_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using var create = new MySqlCommand($"CREATE DATABASE `{database}`;", admin);
        await create.ExecuteNonQueryAsync();
        try
        {
            builder.Database = database;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString
            }).Build();
            await new DatabaseMigrationRunner(config, NullLogger<DatabaseMigrationRunner>.Instance).RunMigrationsAsync();
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand("""
                INSERT INTO biometric_devices (id, device_id, display_name, is_active)
                VALUES (5, '1000000005', 'Device 5', FALSE);
                """, connection);
            await command.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EnrollmentRetirementCheck.VerifyDeletedAsync(config, "1000000005", database));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EnrollmentRetirementCheck.VerifyDeletedAsync(config, "1000000005", "wrong_database"));
            command.CommandText = "DELETE FROM biometric_devices WHERE id=5;";
            await command.ExecuteNonQueryAsync();
            await EnrollmentRetirementCheck.VerifyDeletedAsync(config, "1000000005", database);
            // Simulate an inconsistent restore without touching any real portal database.
            command.CommandText = """
                SET FOREIGN_KEY_CHECKS=0;
                INSERT INTO fingerprint_enrollment_device_credentials (device_id, protected_key, key_fingerprint)
                VALUES (5, REPEAT(CHAR(0), 61), REPEAT(CHAR(0), 32));
                SET FOREIGN_KEY_CHECKS=1;
                """;
            await command.ExecuteNonQueryAsync();
            var credentialsError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EnrollmentRetirementCheck.VerifyDeletedAsync(config, "1000000005", database));
            Assert.Contains("credentials", credentialsError.Message);
            command.CommandText = "DELETE FROM fingerprint_enrollment_device_credentials WHERE device_id=5;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = """
                SET FOREIGN_KEY_CHECKS=0;
                INSERT INTO fingerprint_enrollment_write_guards (preparation_id, operation_id)
                VALUES ('missing-preparation', 'missing-operation');
                SET FOREIGN_KEY_CHECKS=1;
                """;
            await command.ExecuteNonQueryAsync();
            var guardError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EnrollmentRetirementCheck.VerifyDeletedAsync(config, "1000000005", database));
            Assert.Contains("fingerprint guards", guardError.Message);
            command.CommandText = "DELETE FROM fingerprint_enrollment_write_guards WHERE preparation_id='missing-preparation';";
            await command.ExecuteNonQueryAsync();
            command.CommandText = """
                SET FOREIGN_KEY_CHECKS=0;
                INSERT INTO ac_ir_state (device_id, classroom_id, paired_at, boot_id, state)
                VALUES (5, 5, UTC_TIMESTAMP(6), REPEAT(CHAR(0),32), 'inflight');
                SET FOREIGN_KEY_CHECKS=1;
                """;
            await command.ExecuteNonQueryAsync();
            var irError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EnrollmentRetirementCheck.VerifyDeletedAsync(config, "1000000005", database));
            Assert.Contains("IR commands", irError.Message);
        }
        finally
        {
            await using var drop = new MySqlCommand($"DROP DATABASE `{database}`;", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Device 5")]
    [InlineData("1000000005'")]
    public async Task RetirementRejectsInvalidIdentityBeforeDatabaseAccess(string deviceId)
    {
        var config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnrollmentRetirementCheck.VerifyDeletedAsync(config, deviceId, "smartclassac"));
    }
}
