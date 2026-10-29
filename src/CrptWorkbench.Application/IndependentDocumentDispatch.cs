namespace CrptWorkbench.Application;

// A bounded wave keeps memory and the number of unfinished submissions predictable.
internal static class IndependentDocumentDispatch
{
    public static bool WasNotDispatched(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is CrptWorkbench.Domain.ApiDispatchNotStartedException) return true;
        return false;
    }

    public static async Task RunAsync<T>(IReadOnlyList<T> items, Func<T, Task<IReadOnlySet<string>?>> keys,
        Func<T, Func<bool>, Action, Task<bool>> submit, CancellationToken cancellationToken)
    {
        var stopped = 0;
        var index = 0;
        while (index < items.Count && Volatile.Read(ref stopped) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wave = new List<T>(4);
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            while (index < items.Count && wave.Count < 4)
            {
                var identifiers = await keys(items[index]);
                if (wave.Count > 0 && (identifiers is null || occupied.Overlaps(identifiers))) break;
                wave.Add(items[index++]);
                if (identifiers is null) break;
                occupied.UnionWith(identifiers);
            }
            await Task.WhenAll(wave.Select(async item =>
            {
                if (Volatile.Read(ref stopped) != 0) return;
                try
                {
                    if (!await submit(item, () => Volatile.Read(ref stopped) == 0, () => Interlocked.Exchange(ref stopped, 1))) Interlocked.Exchange(ref stopped, 1);
                }
                catch
                {
                    Interlocked.Exchange(ref stopped, 1);
                    throw;
                }
            }));
        }
    }
}
