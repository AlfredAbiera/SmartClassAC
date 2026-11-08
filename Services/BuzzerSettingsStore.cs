using System.Globalization;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace SmartClassAC.Services;

public sealed record BuzzerSettings(bool Enabled, int LeadMinutes, int RepeatMinutes);
internal sealed record BuzzerReminder(string? ReminderId);

public sealed class BuzzerSettingsStore(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("SmartClassMariaDb")
        ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before using buzzer settings.");

    public async Task<BuzzerSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled, lead_minutes, repeat_minutes FROM buzzer_settings WHERE singleton_id = 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Buzzer settings are missing; apply migration 040.");
        return new(reader.GetBoolean(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public async Task<OperationResult> SaveAsync(BuzzerSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.LeadMinutes is < 1 or > 180 || settings.RepeatMinutes is < 1 or > 180)
            return OperationResult.Failure("Both reminder times must be between 1 and 180 minutes.");
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO buzzer_settings (singleton_id, enabled, lead_minutes, repeat_minutes)
            VALUES (1, @enabled, @lead, @repeat)
            ON DUPLICATE KEY UPDATE enabled = VALUES(enabled), lead_minutes = VALUES(lead_minutes),
                                    repeat_minutes = VALUES(repeat_minutes);
            """;
        command.Parameters.Add("@enabled", MySqlDbType.Bool).Value = settings.Enabled;
        command.Parameters.Add("@lead", MySqlDbType.Int32).Value = settings.LeadMinutes;
        command.Parameters.Add("@repeat", MySqlDbType.Int32).Value = settings.RepeatMinutes;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> QueueTestAsync(long deviceId, long adminId, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var account = connection.CreateCommand();
        account.Transaction = transaction;
        account.CommandText = "SELECT id FROM user_accounts WHERE id = @admin AND role = 'Admin' AND is_active = TRUE FOR UPDATE";
        account.Parameters.Add("@admin", MySqlDbType.Int64).Value = adminId;
        if (await account.ExecuteScalarAsync(cancellationToken) is null)
            return OperationResult.Failure("An active administrator account is required.");
        await using var device = connection.CreateCommand();
        device.Transaction = transaction;
        device.CommandText = "SELECT paired_at FROM biometric_devices WHERE id = @id AND is_active = TRUE AND paired_at IS NOT NULL FOR UPDATE";
        device.Parameters.Add("@id", MySqlDbType.Int64).Value = deviceId;
        var pairedAt = await device.ExecuteScalarAsync(cancellationToken);
        if (pairedAt is null or DBNull) return OperationResult.Failure("Select an active, paired device.");
        await using var pending = connection.CreateCommand();
        pending.Transaction = transaction;
        pending.CommandText = "SELECT EXISTS(SELECT 1 FROM buzzer_test_requests WHERE device_id = @id AND expires_utc > UTC_TIMESTAMP(6) AND paired_at = @paired)";
        pending.Parameters.Add("@id", MySqlDbType.Int64).Value = deviceId;
        pending.Parameters.Add("@paired", MySqlDbType.DateTime).Value = pairedAt;
        if (Convert.ToInt32(await pending.ExecuteScalarAsync(cancellationToken)) != 0)
            return OperationResult.Failure("A test is already pending for this device. Wait up to 30 seconds before trying again.");
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO buzzer_test_requests (device_id, paired_at, expires_utc)
            VALUES (@id, @paired, DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 30 SECOND))
            ON DUPLICATE KEY UPDATE paired_at = VALUES(paired_at), expires_utc = VALUES(expires_utc);
            """;
        insert.Parameters.Add("@id", MySqlDbType.Int64).Value = deviceId;
        insert.Parameters.Add("@paired", MySqlDbType.DateTime).Value = pairedAt;
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    internal async Task<bool> ClaimTestAsync(string deviceId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE t FROM buzzer_test_requests t
            INNER JOIN biometric_devices d ON d.id = t.device_id
            WHERE d.device_id = @device AND d.is_active = TRUE AND d.paired_at IS NOT NULL
              AND d.paired_at = t.paired_at AND t.expires_utc > UTC_TIMESTAMP(6);
            """;
        command.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    internal static string? ReminderId(long attendanceId, DateTime end, DateTime now, BuzzerSettings settings)
    {
        if (!settings.Enabled || settings.LeadMinutes is < 1 or > 180 || settings.RepeatMinutes is < 1 or > 180)
            return null;
        var first = end.AddMinutes(-settings.LeadMinutes);
        if (now < first || now >= end) return null;
        var slot = (long)((now - first).TotalSeconds / (settings.RepeatMinutes * 60));
        var due = first.AddMinutes(slot * settings.RepeatMinutes);
        if ((now - due).TotalSeconds >= 20) return null;
        return attendanceId.ToString(CultureInfo.InvariantCulture) + "-" + due.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    }

    internal async Task<BuzzerReminder> GetReminderAsync(string deviceId, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        BuzzerSettings settings;
        await using (var settingsCommand = connection.CreateCommand())
        {
            settingsCommand.CommandText = "SELECT enabled, lead_minutes, repeat_minutes FROM buzzer_settings WHERE singleton_id = 1";
            await using var settingsReader = await settingsCommand.ExecuteReaderAsync(cancellationToken);
            if (!await settingsReader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Buzzer settings are missing; apply migration 040.");
            settings = new(settingsReader.GetBoolean(0), settingsReader.GetInt32(1), settingsReader.GetInt32(2));
        }
        if (settings.LeadMinutes is < 1 or > 180 || settings.RepeatMinutes is < 1 or > 180)
            throw new InvalidOperationException("Stored buzzer settings are invalid.");
        if (!settings.Enabled) return new(null);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, s.schedule_date, s.end_time
            FROM biometric_devices d
            JOIN classrooms c ON c.id = d.classroom_id AND c.is_active = TRUE
            JOIN attendance_logs a ON a.classroom_id = c.id AND a.status = 'Active' AND a.time_out_utc IS NULL
            JOIN class_schedules s ON s.id = a.class_schedule_id AND s.classroom_id = c.id
            WHERE d.device_id = @device AND d.is_active = TRUE AND d.paired_at IS NOT NULL AND s.schedule_date = @date
              AND @time >= s.start_time AND @time < s.end_time
            ORDER BY s.end_time, a.id
            LIMIT 1;
            """;
        command.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        command.Parameters.Add("@date", MySqlDbType.Date).Value = now.Date;
        command.Parameters.Add("@time", MySqlDbType.Time).Value = now.TimeOfDay;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(null);
        var end = reader.GetDateTime(1).Date.Add(reader.GetTimeSpan(2));
        return new(ReminderId(reader.GetInt64(0), end, now, settings));
    }
}

internal static class BuzzerRoutes
{
    internal static void MapBuzzerRoutes(this WebApplication app, Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        app.MapPost("/api/device/buzzer", async (DeviceStatusRequest request, HttpContext context,
            IConfiguration configuration, BuzzerSettingsStore store, IOptions<SchoolTimeOptions> schoolTime,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration)) return Results.Unauthorized();
            if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId)) return Results.BadRequest();
            var reminder = await store.GetReminderAsync(request.DeviceId!, SchoolClock.GetNow(schoolTime.Value), cancellationToken);
            return Results.Json(new { ok = true, reminderId = reminder.ReminderId });
        });
        app.MapPost("/api/device/buzzer/v2", async (DeviceStatusRequest request, HttpContext context,
            IConfiguration configuration, BuzzerSettingsStore store, IOptions<SchoolTimeOptions> schoolTime,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration)) return Results.Unauthorized();
            if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId)) return Results.BadRequest();
            var reminder = await store.GetReminderAsync(request.DeviceId!, SchoolClock.GetNow(schoolTime.Value), cancellationToken);
            var test = await store.ClaimTestAsync(request.DeviceId!, cancellationToken);
            return Results.Json(new { ok = true, reminderId = reminder.ReminderId, test });
        });
    }
}
