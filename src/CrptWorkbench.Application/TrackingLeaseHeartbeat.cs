namespace CrptWorkbench.Application;

// Renew independently of the HTTP call, including time spent waiting for an API slot.
internal sealed class TrackingLeaseHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _operation;
    private readonly Task _heartbeat;
    public CancellationToken Token => _operation.Token;
    public bool OwnershipLost { get; private set; }

    public TrackingLeaseHeartbeat(TimeProvider clock, Func<CancellationToken, Task<bool>> renew, CancellationToken cancellationToken)
    {
        _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _heartbeat = RunAsync(clock, renew);
    }

    private async Task RunAsync(TimeProvider clock, Func<CancellationToken, Task<bool>> renew)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                if (await renew(_stop.Token)) continue;
                OwnershipLost = true;
                _operation.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            // If ownership cannot be proved, do not publish a result or send another request.
            OwnershipLost = true;
            _operation.Cancel();
        }
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        await _heartbeat;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
        _operation.Dispose();
    }
}
