using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class BuzzerSettingsTests
{
    [Theory]
    [InlineData("Development", HttpStatusCode.Unauthorized)]
    [InlineData("Production", HttpStatusCode.UpgradeRequired)]
    public async Task DeviceRouteRequiresKeyAndSecureTransportOutsideDevelopment(string environment, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOptions();
        builder.Services.AddScoped<BuzzerSettingsStore>();
        await using var app = builder.Build();
        app.MapBuzzerRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        foreach (var route in new[] { "/api/device/buzzer", "/api/device/buzzer/v2" })
        {
            client.DefaultRequestHeaders.Remove("X-Device-Api-Key");
            using var unauthorized = await client.PostAsJsonAsync(route, new DeviceStatusRequest("1000000001"));
            Assert.Equal(expected, unauthorized.StatusCode);
            Assert.True(unauthorized.Headers.CacheControl?.NoStore);
            client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
            using var invalid = await client.PostAsJsonAsync(route, new DeviceStatusRequest("invalid"));
            Assert.Equal(environment == "Development" ? HttpStatusCode.BadRequest : HttpStatusCode.UpgradeRequired, invalid.StatusCode);
        }
    }

    [Fact]
    public void RemindersRepeatFromLeadTimeAndStopAtEnd()
    {
        var end = new DateTime(2026, 1, 1, 12, 0, 0);
        var settings = new BuzzerSettings(true, 15, 5);
        Assert.Null(BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-15).AddSeconds(-1), settings));
        Assert.Equal("1-20260101114500", BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-15), settings));
        Assert.NotNull(BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-15).AddSeconds(19), settings));
        Assert.Null(BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-15).AddSeconds(20), settings));
        Assert.Equal("1-20260101115000", BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-10), settings));
        Assert.Equal("1-20260101115500", BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-5), settings));
        Assert.Null(BuzzerSettingsStore.ReminderId(1, end, end, settings));
        Assert.Null(BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-5), settings with { Enabled = false }));
    }

    [Fact]
    public void LongRepeatAndInvalidSettingsAreSafe()
    {
        var end = new DateTime(2026, 1, 1, 12, 0, 0);
        Assert.NotNull(BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-5), new(true, 5, 10)));
        Assert.Null(BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-1), new(true, 5, 10)));
        Assert.Null(BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-1), new(true, 0, 1)));
        Assert.Null(BuzzerSettingsStore.ReminderId(1, end, end.AddMinutes(-1), new(true, 1, 0)));
    }

    [MariaDbFact]
    public async Task StoreUsesActiveSessionAndAssignedRoomAndPersistsSettings()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCLASS_TEST_CONNECTION")!) { Database = "" };
        var database = "smartclass_buzzer_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await new MySqlCommand($"CREATE DATABASE `{database}`", admin).ExecuteNonQueryAsync();
        try
        {
            builder.Database = database;
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "040_buzzer_settings.sql"));
            await new MySqlCommand(migration, connection).ExecuteNonQueryAsync();
            await new MySqlCommand(migration, connection).ExecuteNonQueryAsync();
            await new MySqlCommand("""
                CREATE TABLE user_accounts (id BIGINT PRIMARY KEY, role VARCHAR(20), is_active BOOL);
                CREATE TABLE biometric_devices (id BIGINT PRIMARY KEY, device_id VARCHAR(32), classroom_id BIGINT, is_active BOOL, paired_at DATETIME(6));
                CREATE TABLE classrooms (id BIGINT, is_active BOOL);
                CREATE TABLE attendance_logs (id BIGINT, classroom_id BIGINT, status VARCHAR(24), time_out_utc DATETIME NULL, class_schedule_id BIGINT);
                CREATE TABLE class_schedules (id BIGINT, classroom_id BIGINT, schedule_date DATE, start_time TIME, end_time TIME);
                INSERT INTO classrooms VALUES (1, 1), (2, 1);
                INSERT INTO user_accounts VALUES (1, 'Admin', 1), (2, 'Teacher', 1), (3, 'Admin', 0);
                INSERT INTO biometric_devices VALUES (1, '1000000001', 1, 1, NOW(6)), (2, '1000000002', NULL, 1, NOW(6)),
                    (3, '1000000003', 1, 0, NOW(6)), (4, '1000000004', 1, 1, NULL);
                """, connection).ExecuteNonQueryAsync();
            var testMigration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "041_buzzer_test_requests.sql"));
            await new MySqlCommand(testMigration, connection).ExecuteNonQueryAsync();
            await new MySqlCommand(testMigration, connection).ExecuteNonQueryAsync();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString
            }).Build();
            var store = new BuzzerSettingsStore(configuration);
            Assert.Equal(new BuzzerSettings(true, 15, 5), await store.GetAsync());
            Assert.False((await store.SaveAsync(new(true, 0, 5))).Succeeded);
            Assert.False((await store.SaveAsync(new(true, 15, 181))).Succeeded);
            Assert.True((await store.SaveAsync(new(true, 15, 5))).Succeeded);
            Assert.Equal(new BuzzerSettings(true, 15, 5), await store.GetAsync());
            var now = new DateTime(2026, 1, 1, 11, 45, 0);
            var end = now.AddMinutes(15);
            await using (var insert = new MySqlCommand("""
                INSERT INTO class_schedules VALUES (1, 1, @date, @start, @end);
                INSERT INTO attendance_logs VALUES (1, 1, 'Active', NULL, 1);
                """, connection))
            {
                insert.Parameters.AddWithValue("@date", now.Date);
                insert.Parameters.AddWithValue("@start", now.AddMinutes(-1).TimeOfDay);
                insert.Parameters.AddWithValue("@end", end.TimeOfDay);
                await insert.ExecuteNonQueryAsync();
            }
            Assert.NotNull((await store.GetReminderAsync("1000000001", now, default)).ReminderId);
            Assert.Null((await store.GetReminderAsync("1000000002", now, default)).ReminderId);
            Assert.Null((await store.GetReminderAsync("1000000001", end, default)).ReminderId);
            Assert.Null((await store.GetReminderAsync("1000000001", now.AddSeconds(20), default)).ReminderId);
            await new MySqlCommand("UPDATE attendance_logs SET status = 'Completed'", connection).ExecuteNonQueryAsync();
            Assert.Null((await store.GetReminderAsync("1000000001", now, default)).ReminderId);
            await new MySqlCommand("UPDATE attendance_logs SET status = 'Active'; UPDATE class_schedules SET classroom_id = 2", connection).ExecuteNonQueryAsync();
            Assert.Null((await store.GetReminderAsync("1000000001", now, default)).ReminderId);
            Assert.True((await store.SaveAsync(new(false, 15, 5))).Succeeded);
            await new MySqlCommand("UPDATE class_schedules SET classroom_id = 1", connection).ExecuteNonQueryAsync();
            Assert.Null((await store.GetReminderAsync("1000000001", now, default)).ReminderId);
            Assert.False((await store.QueueTestAsync(1, 2)).Succeeded);
            Assert.False((await store.QueueTestAsync(1, 3)).Succeeded);
            Assert.False((await store.QueueTestAsync(3, 1)).Succeeded);
            Assert.False((await store.QueueTestAsync(4, 1)).Succeeded);
            Assert.True((await store.QueueTestAsync(1, 1)).Succeeded);
            Assert.False((await store.QueueTestAsync(1, 1)).Succeeded);
            Assert.False(await store.ClaimTestAsync("1000000002", default));
            var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.ClaimTestAsync("1000000001", default)));
            Assert.Single(claims, value => value);
            Assert.True((await store.QueueTestAsync(1, 1)).Succeeded);
            await new MySqlCommand("UPDATE buzzer_test_requests SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND)", connection).ExecuteNonQueryAsync();
            Assert.False(await store.ClaimTestAsync("1000000001", default));
            Assert.True((await store.QueueTestAsync(1, 1)).Succeeded);
            await new MySqlCommand("UPDATE biometric_devices SET paired_at = DATE_ADD(paired_at, INTERVAL 1 SECOND) WHERE id = 1", connection).ExecuteNonQueryAsync();
            Assert.False(await store.ClaimTestAsync("1000000001", default));
            Assert.True((await store.QueueTestAsync(1, 1)).Succeeded);
            Assert.True(await store.ClaimTestAsync("1000000001", default));
            Assert.True((await store.QueueTestAsync(1, 1)).Succeeded);
            var web = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            web.Logging.ClearProviders();
            web.WebHost.UseUrls("http://127.0.0.1:0");
            web.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString
            });
            web.Services.AddOptions();
            web.Services.AddScoped<BuzzerSettingsStore>();
            await using var app = web.Build();
            app.MapBuzzerRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
            await app.StartAsync();
            var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
            using var legacy = await client.PostAsJsonAsync("/api/device/buzzer", new DeviceStatusRequest("1000000001"));
            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
            using var response = await client.PostAsJsonAsync("/api/device/buzzer/v2", new DeviceStatusRequest("1000000001"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(payload.RootElement.GetProperty("test").GetBoolean());
            using var duplicate = await client.PostAsJsonAsync("/api/device/buzzer/v2", new DeviceStatusRequest("1000000001"));
            using var duplicatePayload = System.Text.Json.JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
            Assert.False(duplicatePayload.RootElement.GetProperty("test").GetBoolean());
        }
        finally
        {
            await new MySqlCommand($"DROP DATABASE `{database}`", admin).ExecuteNonQueryAsync();
        }
    }
}
