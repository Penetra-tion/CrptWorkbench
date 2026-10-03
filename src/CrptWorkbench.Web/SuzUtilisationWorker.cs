using CrptWorkbench.Application;

namespace CrptWorkbench.Web;

public sealed class SuzUtilisationWorker(IServiceScopeFactory scopes, ILogger<SuzUtilisationWorker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SuzUtilisationTracker>().RecoverSendingAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SuzUtilisationTracker>().PollDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError("SUZ report tracking failed: {ExceptionType}", exception.GetType().Name); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
