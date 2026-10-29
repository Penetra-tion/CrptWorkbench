using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

internal static class ApiDispatchWait
{
    public static async ValueTask<IDisposable> AcquireAsync(IApiRequestLimiter limiter, ApiConcurrencyService service,
        Func<CancellationToken, Task<bool>>? canDispatch, Func<CancellationToken, Task>? beforeDispatch,
        CancellationToken cancellationToken)
    {
        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IDisposable>? waiting = null;
        IDisposable? slot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canDispatch is not null && !await canDispatch(cancellationToken)) throw new OperationCanceledException(cancellationToken);
            waiting = limiter.AcquireAsync(service, waitingCancellation.Token).AsTask();
            if (canDispatch is not null)
            {
                while (!waiting.IsCompleted)
                {
                    await Task.WhenAny(waiting, Task.Delay(100, waitingCancellation.Token));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await canDispatch(cancellationToken)) throw new OperationCanceledException(cancellationToken);
                }
            }
            slot = await waiting;
            cancellationToken.ThrowIfCancellationRequested();
            if (canDispatch is not null && !await canDispatch(cancellationToken)) throw new OperationCanceledException(cancellationToken);
            if (beforeDispatch is not null) await beforeDispatch(cancellationToken);
            return slot;
        }
        catch (Exception exception)
        {
            await waitingCancellation.CancelAsync();
            // An acquisition racing cancellation may have succeeded: reclaim it.
            if (slot is null && waiting is not null)
            {
                try { slot = await waiting; }
                catch (Exception) { }
            }
            slot?.Dispose();
            if (exception is OperationCanceledException cancelled) throw new ApiDispatchNotStartedException(cancelled);
            throw;
        }
        finally { await waitingCancellation.CancelAsync(); }
    }
}
