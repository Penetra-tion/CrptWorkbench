using System.Globalization;

namespace CrptWorkbench.Domain;

public static class ApiRateLimitDelay
{
    // Retry-After is either integer seconds or an HTTP date. Never retry earlier
    // than a valid server deadline; invalid headers use bounded exponential backoff.
    public static TimeSpan ForRetry(IReadOnlyDictionary<string, string> headers, int attempt,
        DateTimeOffset? now = null)
    {
        var value = headers.FirstOrDefault(x => x.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value;
        var delay = TimeSpan.FromSeconds(1 << Math.Clamp(attempt, 0, 5));
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            delay = TimeSpan.FromSeconds(Math.Min(seconds, (long)TimeSpan.MaxValue.TotalSeconds - 1));
        else if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var deadline))
            delay = deadline - (now ?? DateTimeOffset.UtcNow);
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return delay + TimeSpan.FromMilliseconds(Random.Shared.Next(50, 251));
    }
}
