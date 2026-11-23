using Microsoft.Extensions.Options;

namespace SmartClassAC.Services;

/// <summary>
/// Periodically derives attendance outcomes and notifies teachers about missing time-outs.
/// </summary>
public sealed class AttendanceReconciliationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<SchoolTimeOptions> _schoolTime;
    private readonly ILogger<AttendanceReconciliationService> _logger;

    public AttendanceReconciliationService(
        IServiceScopeFactory scopeFactory,
        IOptions<SchoolTimeOptions> schoolTime,
        ILogger<AttendanceReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _schoolTime = schoolTime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var accounts = scope.ServiceProvider.GetRequiredService<IUserAccountRepository>();
                var schoolNow = SchoolClock.GetNow(_schoolTime.Value);
                var changed = await accounts.ReconcileAttendanceOutcomesAsync(
                    DateOnly.FromDateTime(schoolNow),
                    TimeOnly.FromDateTime(schoolNow),
                    stoppingToken);
                if (changed > 0)
                {
                    _logger.LogInformation("Attendance reconciliation updated {Count} record(s).", changed);
                }

                await SendMissingTimeOutNotificationsAsync(scope.ServiceProvider, accounts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Attendance reconciliation failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SendMissingTimeOutNotificationsAsync(
        IServiceProvider services, IUserAccountRepository accounts, CancellationToken cancellationToken)
    {
        var notifications = await accounts.ClaimMissingTimeOutNotificationsAsync(cancellationToken);
        if (notifications.Count == 0)
        {
            return;
        }

        GsmModemSettings? settings;
        try
        {
            settings = await services.GetRequiredService<GsmModemSettingsStore>().GetAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not load GSM modem settings for missing-time-out SMS notifications.");
            foreach (var notification in notifications)
            {
                await accounts.CompleteMissingTimeOutNotificationAsync(notification.AttendanceId, "Failed", cancellationToken);
            }
            return;
        }

        var transport = services.GetRequiredService<GsmSmsTransport>();
        foreach (var notification in notifications)
        {
            if (!GsmSmsTransport.IsAsciiMobile(notification.TeacherPhone))
            {
                await accounts.CompleteMissingTimeOutNotificationAsync(notification.AttendanceId, "Skipped", cancellationToken);
                _logger.LogWarning(
                    "Missing-time-out SMS skipped for attendance {AttendanceId}: teacher mobile number is unavailable or invalid.",
                    notification.AttendanceId);
                continue;
            }

            var result = await transport.SendAttendanceReminderAsync(settings, notification.TeacherPhone!, cancellationToken);
            var status = result switch
            {
                GsmSmsResult.Accepted => "Sent",
                GsmSmsResult.DeliveryUnknown => "Unknown",
                _ => "Failed"
            };
            await accounts.CompleteMissingTimeOutNotificationAsync(notification.AttendanceId, status, cancellationToken);

            if (result == GsmSmsResult.Accepted)
            {
                _logger.LogInformation("Missing-time-out SMS accepted for attendance {AttendanceId}.", notification.AttendanceId);
            }
            else
            {
                _logger.LogWarning(
                    "Missing-time-out SMS for attendance {AttendanceId} was not confirmed: {Result}.",
                    notification.AttendanceId, result);
            }
        }
    }
}
