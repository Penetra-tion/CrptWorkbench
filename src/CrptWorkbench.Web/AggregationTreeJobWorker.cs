using CrptWorkbench.Application;

namespace CrptWorkbench.Web;

public sealed class AggregationTreeJobWorker(IServiceScopeFactory scopes, ILogger<AggregationTreeJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<AggregationTreeJobProcessor>();
                if (!recovered) { await processor.RecoverAsync(stoppingToken); recovered = true; }
                await processor.ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError("Aggregation worker failed: {ExceptionType}", exception.GetType().Name); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
