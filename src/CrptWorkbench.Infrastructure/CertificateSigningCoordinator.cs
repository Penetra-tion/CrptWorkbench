using System.Collections.Concurrent;

namespace CrptWorkbench.Infrastructure;

public static class CertificateSigningCoordinator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<T> ExecuteAsync<T>(string normalizedThumbprint, Func<Task<T>> sign, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(normalizedThumbprint, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await sign(); }
        finally { gate.Release(); }
    }
}
