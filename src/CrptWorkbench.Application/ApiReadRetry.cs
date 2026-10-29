using System.Diagnostics;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

internal static class ApiReadRetry
{
    public static async Task<bool> WaitAsync(IReadOnlyDictionary<string, string> headers, int attempt,
        CancellationToken cancellationToken, Func<CancellationToken, Task<bool>>? canContinue = null)
    {
        var delay = ApiRateLimitDelay.ForRetry(headers, attempt);
        var watch = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canContinue is not null && !await canContinue(cancellationToken)) return false;
            var remaining = delay - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, canContinue is null ? 60_000 : 250)), cancellationToken);
        } while (true);
    }
}
