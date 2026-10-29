namespace CrptWorkbench.Domain;

public enum ApiConcurrencyService { TrueApi, NationalCatalog, Suz, PermitSgr, PermitEaeu, Mercury }

public interface IApiRequestLimiter
{
    ValueTask<IDisposable> AcquireAsync(ApiConcurrencyService service, CancellationToken cancellationToken = default);
    void Defer(ApiConcurrencyService service, TimeSpan delay) { }
}

public sealed class ApiDispatchNotStartedException(OperationCanceledException inner)
    : OperationCanceledException("Request cancelled before dispatch.", inner, inner.CancellationToken)
{
    public static bool IsPresent(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is ApiDispatchNotStartedException) return true;
        return false;
    }
}
