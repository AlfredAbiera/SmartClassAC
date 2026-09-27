using Microsoft.Extensions.Options;

namespace SmartClassAC.Services;

/// <summary>
/// Periodically derives Absent / MissingTimeOut from schedule windows vs punches.
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
}
