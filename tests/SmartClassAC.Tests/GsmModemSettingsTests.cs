using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class GsmModemSettingsTests
{
    [Fact]
    public async Task InvalidBaudRateIsRejectedBeforeDatabaseAccess()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SmartClassMariaDb"] = "Server=127.0.0.1;Database=unused;User ID=root;"
        }).Build();
        var store = new GsmModemSettingsStore(configuration);
        var result = await store.SaveAsync("COM9", -1);
        Assert.False(result.Succeeded);
        Assert.Contains("baud rate", result.Error);
    }

    [Fact]
    public void StandardRatesIncludeDefault()
    {
        Assert.Contains(9600, GsmModemSettingsStore.BaudRates);
        Assert.Equal(GsmModemSettingsStore.BaudRates.Length, GsmModemSettingsStore.BaudRates.Distinct().Count());
    }

    [MariaDbFact]
    public async Task SettingsPersistAndRejectUnavailablePorts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ports = GsmModemSettingsStore.GetPorts();
        if (ports.Count == 0) return;
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCLASS_TEST_CONNECTION")!)
        {
            Database = string.Empty
        };
        var database = "smartclass_gsm_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new MySqlCommand($"CREATE DATABASE `{database}`", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            builder.Database = database;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString
            }).Build();
            await new DatabaseMigrationRunner(configuration, NullLogger<DatabaseMigrationRunner>.Instance).RunMigrationsAsync();
            var store = new GsmModemSettingsStore(configuration);
            Assert.Null(await store.GetAsync());
            Assert.False((await store.SaveAsync("COM999999999", 9600)).Succeeded);
            Assert.True((await store.SaveAsync(ports[0], 9600)).Succeeded);
            Assert.Equal(new GsmModemSettings(ports[0], 9600), await store.GetAsync());
            Assert.True((await store.SaveAsync(ports[0], 115200)).Succeeded);
            Assert.Equal(new GsmModemSettings(ports[0], 115200), await store.GetAsync());
        }
        finally
        {
            await using var drop = new MySqlCommand($"DROP DATABASE `{database}`", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
