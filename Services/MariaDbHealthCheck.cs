using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartClassAC.Services;

public sealed class MariaDbHealthCheck : IHealthCheck
{
    private readonly IUserAccountRepository accounts;

    public MariaDbHealthCheck(IUserAccountRepository accounts) => this.accounts = accounts;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await accounts.CheckHealthAsync(cancellationToken);
            return HealthCheckResult.Healthy("MariaDB is reachable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("MariaDB is unavailable.", exception);
        }
    }
}
