using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CrptWorkbench.Web;

public sealed class ClientCircuitLogging(ILoggerFactory factory) : CircuitHandler
{
    private readonly ILogger _logger = factory.CreateLogger(WorkbenchLogging.CircuitCategory);

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Circuit opened {CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Circuit closed {CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) => async context =>
    {
        try { await next(context); }
        catch (Exception exception)
        {
            _logger.LogError("Circuit activity failed {CircuitId} {ExceptionType}", context.Circuit.Id, exception.GetType().Name);
            throw;
        }
    };
}
