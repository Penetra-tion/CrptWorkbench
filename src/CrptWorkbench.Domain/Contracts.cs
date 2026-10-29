namespace CrptWorkbench.Domain;

public interface IProfileRepository
{
    Task<IReadOnlyList<CrptProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task<CrptProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpsertAsync(CrptProfile profile, CancellationToken cancellationToken = default);
}

public interface IApiRequestLogRepository
{
    Task<RecordPage<ApiRequestLogSummary>> ListSummaryPageAsync(Guid profileId, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<RecordPage<ApiRequestLog>> ListPageAsync(Guid profileId, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task AddAsync(ApiRequestLog entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApiRequestLog>> ListAsync(Guid profileId, int take, CancellationToken cancellationToken = default);
    Task<ApiRequestLog?> GetAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default);
    Task UpsertBulkSummaryAsync(ApiRequestLog entry, CancellationToken cancellationToken = default) => AddAsync(entry, cancellationToken);
}

public interface IFavoriteApiOperationRepository
{
    Task<IReadOnlyList<string>> ListOperationIdsAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SetAsync(Guid profileId, string operationId, bool isFavorite, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface ITraceXConnectionTester
{
    Task<TraceXConnectionCheckResult> CheckAsync(
        TraceXDatabaseProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default);
}

public interface ITraceXConnectionStringValidator
{
    InputValidationResult Validate(TraceXDatabaseProvider provider, string connectionString);
}

public interface ISensitiveDataMasker
{
    string? MaskHeaders(string? value);
    string? MaskBody(string? value);
}

public interface ICertificateService
{
    Task<IReadOnlyList<CertificateInfo>> GetAvailableCertificatesAsync(CancellationToken cancellationToken = default);
    Task<CertificateInfo?> GetCertificateAsync(string thumbprint, CancellationToken cancellationToken = default);
    Task<CertificateValidationResult> ValidateAsync(string thumbprint, CancellationToken cancellationToken = default);
}

public interface ICertificateSignatureService
{
    Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default);
    Task<string> SignDetachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Detached CMS signing is not available.");
}

public interface IPackageDocumentRepository
{
    Task<RecordPage<PackageDocument>> ListPageAsync(Guid profileId, PackageOperationKind kind, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<PackageDocument>> GetManyAsync(Guid profileId, PackageOperationKind kind, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task AddAsync(PackageDocument document, CancellationToken cancellationToken = default);
    Task UpdateAsync(PackageDocument document, CancellationToken cancellationToken = default);
    Task<bool> CompleteCheckAsync(PackageDocument document, long leaseUntilUnixMs, CancellationToken cancellationToken = default);
    Task<bool> RenewCheckLeaseAsync(Guid id, long expectedLeaseUntilUnixMs, long nowUnixMs, long leaseUntilUnixMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<PackageDocument>> ListAsync(Guid profileId, PackageOperationKind kind, CancellationToken cancellationToken = default);
    Task<PackageDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PackageDocument>> ClaimDueAsync(long nowUnixMs, int take, CancellationToken cancellationToken = default);
    Task<bool> ScheduleAsync(Guid profileId, Guid id, PackageOperationKind kind, long nowUnixMs, bool resume, CancellationToken cancellationToken = default);
}

public sealed record IntegrationAuthenticationContext(
    Guid ProfileId,
    string? Inn,
    string? CertificateThumbprint);

public interface ITrueApiTokenProvider
{
    Task<string> GetTokenAsync(
        IntegrationAuthenticationContext authentication,
        string correlationId,
        CancellationToken cancellationToken = default);

    void Invalidate(IntegrationAuthenticationContext authentication);
    void Invalidate(IntegrationAuthenticationContext authentication, string rejectedToken) => Invalidate(authentication);
}

public sealed record IntegrationRequest(
    string OperationId,
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    string CorrelationId,
    IntegrationAuthenticationContext? Authentication = null,
    SuzConnectionContext? SuzConnection = null)
{
    public Func<CancellationToken, Task>? BeforeDispatchAsync { get; init; }
    // Read-only eligibility check: unlike BeforeDispatchAsync it may run while
    // waiting. It must not claim sending state or perform other side effects.
    public Func<CancellationToken, Task<bool>>? CanDispatchAsync { get; init; }
}

public sealed record IntegrationExchange(
    int StatusCode,
    string RequestMethod,
    string RequestUrl,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string? RequestBody,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    string? ResponseBody,
    TimeSpan Duration,
    IReadOnlyList<IntegrationExchange>? Exchanges = null);

public interface ITrueApiClient
{
    Uri BaseAddress { get; }
    Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default);
    async Task<IntegrationExchange> ExecuteToStreamAsync(IntegrationRequest request, Stream destination, CancellationToken cancellationToken = default)
    {
        var exchange = await ExecuteAsync(request, cancellationToken);
        if (exchange.ResponseBody is not null)
            await destination.WriteAsync(System.Text.Encoding.UTF8.GetBytes(exchange.ResponseBody), cancellationToken);
        return exchange with { ResponseBody = null, Exchanges = null };
    }
}

public interface INationalCatalogClient
{
    Uri BaseAddress { get; }
    Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default);
}

public interface ISuzClient
{
    Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default);
}

public interface IMercuryClient
{
    Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default);
}

public interface IPermitDocumentClient
{
    Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default);
}
