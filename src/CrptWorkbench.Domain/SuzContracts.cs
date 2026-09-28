namespace CrptWorkbench.Domain;

public sealed record SuzConnectionContext(string? OmsId, string? ConnectionId, string? BaseUrl);

public interface ISuzTokenProvider
{
    Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, SuzConnectionContext connection,
        string correlationId, CancellationToken cancellationToken = default);
    void Invalidate(IntegrationAuthenticationContext authentication, SuzConnectionContext connection, string rejectedToken);
}
