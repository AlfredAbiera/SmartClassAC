using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class ClassroomTemperaturePolicyTests
{
    [MariaDbFact]
    public async Task TeacherNeedsActiveClassAndCannotGoBelowRoomMinimum()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCLASS_TEST_CONNECTION")!)
        {
            Database = string.Empty
        };
        var database = "smartclass_temp_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await Execute(admin, $"CREATE DATABASE `{database}`;");
        try
        {
            builder.Database = database;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString
            }).Build();
            var migrations = new DatabaseMigrationRunner(configuration, NullLogger<DatabaseMigrationRunner>.Instance);
            await migrations.RunMigrationsAsync();
            await migrations.RunMigrationsAsync();
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await Execute(connection, """
                INSERT INTO classrooms (id, name, capacity, minimum_temperature) VALUES (1, 'Room', 30, 22), (2, 'Other', 30, 16);
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (1, 'teacher', 'Teacher', 'hash', 'Teacher', 1),
                       (2, 'other', 'Other', 'hash', 'Teacher', 1),
                       (3, 'admin', 'Admin', 'hash', 'Admin', 1);
                """);
            var repository = new MariaDbUserAccountRepository(configuration, Options.Create(new SchoolTimeOptions()));
            Assert.Equal(22, (await repository.GetClassroomsAsync()).Single(room => room.Id == 1).MinimumTemperature);
            Assert.True((await repository.CreateClassroomAsync(new CreateClassroomRequest("New", 24, 23))).Succeeded);
            var created = (await repository.GetClassroomsAsync()).Single(room => room.Name == "New");
            Assert.Equal(23, created.MinimumTemperature);
            Assert.Null(created.CurrentTemperature);
            Assert.True((await repository.UpdateClassroomAsync(new UpdateClassroomRequest(created.Id, "New", 24, true, 25))).Succeeded);
            Assert.Equal(25, (await repository.GetClassroomsAsync()).Single(room => room.Id == created.Id).MinimumTemperature);
            Assert.False((await repository.SetTemperatureAsync(1, new SetTemperatureRequest(1, 22, "TargetSet"))).Succeeded);
            await Execute(connection, "INSERT INTO attendance_logs (teacher_account_id, classroom_id) VALUES (1, 1);");
            Assert.False((await repository.SetTemperatureAsync(1, new SetTemperatureRequest(1, 21, "TargetSet"))).Succeeded);
            await Execute(connection, "UPDATE classrooms SET current_temperature = 27.5 WHERE id = 1;");
            Assert.True((await repository.SetTemperatureAsync(1, new SetTemperatureRequest(1, 22, "TargetSet"))).Succeeded);
            Assert.Equal(27.5m, (await repository.GetClassroomsAsync()).Single(room => room.Id == 1).CurrentTemperature);
            Assert.False((await repository.SetTemperatureAsync(2, new SetTemperatureRequest(1, 22, "TargetSet"))).Succeeded);
            Assert.False((await repository.SetTemperatureAsync(1, new SetTemperatureRequest(2, 22, "TargetSet"))).Succeeded);
            Assert.True((await repository.SetTemperatureAsync(3, new SetTemperatureRequest(1, 16, "TargetSet"))).Succeeded);
            await Execute(connection, "UPDATE attendance_logs SET status = 'Completed', time_out_utc = UTC_TIMESTAMP(6) WHERE teacher_account_id = 1;");
            Assert.False((await repository.SetTemperatureAsync(1, new SetTemperatureRequest(1, 22, "TargetSet"))).Succeeded);
            await Execute(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 3;");
            Assert.False((await repository.SetTemperatureAsync(3, new SetTemperatureRequest(1, 22, "TargetSet"))).Succeeded);
        }
        finally
        {
            await Execute(admin, $"DROP DATABASE `{database}`;");
        }
    }

    private static async Task Execute(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
