using CrptWorkbench.Application;

namespace CrptWorkbench.Web;

public sealed class PackageDocumentWorker(IServiceScopeFactory scopes, ILogger<PackageDocumentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PackageDocumentTracker>().PollDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Package status polling failed: {ExceptionType}", exception.GetType().Name);
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
