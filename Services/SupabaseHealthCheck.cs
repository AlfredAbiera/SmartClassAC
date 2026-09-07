using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartClassAC.Services;

public sealed class SupabaseHealthCheck : IHealthCheck
{
    private readonly IUserAccountRepository accounts;

    public SupabaseHealthCheck(IUserAccountRepository accounts) => this.accounts = accounts;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await accounts.CheckHealthAsync(cancellationToken);
            return HealthCheckResult.Healthy("Supabase PostgreSQL is reachable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Supabase PostgreSQL is unavailable.", exception);
        }
    }
}