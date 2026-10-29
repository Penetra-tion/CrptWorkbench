using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

internal static class ApiAuthenticationWait
{
    public static async Task<string> GetTokenAsync(ITrueApiTokenProvider provider,
        IntegrationAuthenticationContext authentication, string correlationId,
        Func<CancellationToken, Task<bool>>? canDispatch, CancellationToken cancellationToken)
    {
        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<string>? waiting = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canDispatch is not null && !await canDispatch(cancellationToken))
                throw new OperationCanceledException(cancellationToken);
            waiting = provider.GetTokenAsync(authentication, correlationId, waitingCancellation.Token);
            if (canDispatch is not null)
            {
                while (!waiting.IsCompleted)
                {
                    await Task.WhenAny(waiting, Task.Delay(100, waitingCancellation.Token));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await canDispatch(cancellationToken)) throw new OperationCanceledException(cancellationToken);
                }
            }
            var token = await waiting;
            cancellationToken.ThrowIfCancellationRequested();
            if (canDispatch is not null && !await canDispatch(cancellationToken))
                throw new OperationCanceledException(cancellationToken);
            return token;
        }
        catch (Exception exception)
        {
            await waitingCancellation.CancelAsync();
            // This acquisition belongs to the request. Observe its cancellation before
            // returning so a paused job cannot leave authentication running in the background.
            if (waiting is not null)
            {
                try { await waiting; }
                catch (Exception) { }
            }
            if (exception is OperationCanceledException cancelled)
                throw new ApiDispatchNotStartedException(cancelled);
            throw;
        }
        finally { await waitingCancellation.CancelAsync(); }
    }
}
