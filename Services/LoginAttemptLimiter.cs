using System.Collections.Concurrent;

namespace SmartClassAC.Services;

public sealed class LoginAttemptLimiter
{
    private const int MaximumFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, AttemptState> attempts = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBlocked(string identifier)
    {
        var key = Normalize(identifier);
        if (!attempts.TryGetValue(key, out var state))
        {
            return false;
        }

        if (state.BlockedUntil > DateTimeOffset.UtcNow)
        {
            return true;
        }

        if (state.FirstFailureAt + Window <= DateTimeOffset.UtcNow)
        {
            attempts.TryRemove(key, out _);
        }

        return false;
    }

    public void RecordFailure(string identifier)
    {
        var key = Normalize(identifier);
        var now = DateTimeOffset.UtcNow;
        attempts.AddOrUpdate(
            key,
            _ => new AttemptState(1, now, DateTimeOffset.MinValue),
            (_, current) => current.FirstFailureAt + Window <= now
                ? new AttemptState(1, now, DateTimeOffset.MinValue)
                : current with
                {
                    FailureCount = current.FailureCount + 1,
                    BlockedUntil = current.FailureCount + 1 >= MaximumFailures ? now + Lockout : current.BlockedUntil
                });
    }

    public void RecordSuccess(string identifier) => attempts.TryRemove(Normalize(identifier), out _);

    private static string Normalize(string identifier) => identifier.Trim().ToUpperInvariant();

    private sealed record AttemptState(int FailureCount, DateTimeOffset FirstFailureAt, DateTimeOffset BlockedUntil);
}
