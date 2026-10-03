namespace CrptWorkbench.Domain;

public enum ApiSystem
{
    TrueApi,
    NationalCatalog,
    Suz,
    Mercury,
    PermitDocuments
}

public enum CrptEnvironment
{
    Production,
    Sandbox
}

public enum CrptIntegrationMode
{
    Mock,
    Production
}

public enum CrptAuthenticationMode
{
    Auto,
    Manual
}

public enum TraceXDatabaseProvider
{
    SqlServer,
    PostgreSql
}

public sealed class CrptIntegrationOptions
{
    public const string SectionName = "Crpt";
    public const string ProductionTrueApiBaseUrl = "https://markirovka.crpt.ru/api/v3/true-api";
    public const string ProductionNationalCatalogBaseUrl = "https://апи.национальный-каталог.рф";

    public CrptIntegrationMode Mode { get; set; } = CrptIntegrationMode.Mock;
    public CrptAuthenticationMode TrueApiAuthMode { get; set; } = CrptAuthenticationMode.Auto;
    public CrptAuthenticationMode SuzAuthMode { get; set; } = CrptAuthenticationMode.Auto;
    public string TrueApiBaseUrl { get; set; } = ProductionTrueApiBaseUrl;
    public string NationalCatalogBaseUrl { get; set; } = ProductionNationalCatalogBaseUrl;
    public string CryptoProExecutablePath { get; set; } = @"C:\Program Files\Crypto Pro\CSP\csptest.exe";
    public string CertificateStoreName { get; set; } = "My";
    public string CertificateStoreLocation { get; set; } = "LocalMachine";
    public int CryptoProTimeoutSeconds { get; set; } = 30;
    public int TokenCacheHours { get; set; } = 9;
    public string? DataDirectory { get; set; }
}

public sealed class CrptProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Inn { get; set; }
    public string? Kpp { get; set; }
    public string? FiasId { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string? NationalCatalogProductionBaseUrl { get; set; }
    public string? TrueApiProductionBaseUrl { get; set; }
    public string? SuzOmsId { get; set; }
    public string? SuzConnectionId { get; set; }
    public string? SuzProductionBaseUrl { get; set; }
    public TraceXDatabaseProvider TraceXDatabaseProvider { get; set; } = TraceXDatabaseProvider.SqlServer;
}

public sealed class FavoriteApiOperation
{
    public Guid ProfileId { get; set; }
    public string OperationId { get; set; } = string.Empty;
}

public sealed class ApiRequestLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long CreatedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public Guid ProfileId { get; set; }
    public CrptEnvironment Environment { get; set; }
    public ApiSystem System { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? RequestHeaders { get; set; }
    public string? RequestBody { get; set; }
    public string? ResponseHeaders { get; set; }
    public string? ResponseBody { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ExchangesJson { get; set; }
}

public enum PackageOperationKind
{
    Transformation,
    Aggregation,
    CodeCorrection
}

public sealed class PackageDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public CrptIntegrationMode Mode { get; set; }
    public PackageOperationKind OperationKind { get; set; }
    public string? PackageType { get; set; }
    public string ProductGroup { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string PackageCode { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public string DocumentHash { get; set; } = string.Empty;
    public string State { get; set; } = "Prepared";
    public string? DocumentId { get; set; }
    public string? RemoteStatus { get; set; }
    public string? Error { get; set; }
    public long CreatedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long? SubmittedAtUnixMs { get; set; }
    public long? LastCheckedAtUnixMs { get; set; }
    public long? NextCheckAtUnixMs { get; set; }
    public long? TrackingUntilUnixMs { get; set; }
    public long? LeaseUntilUnixMs { get; set; }
    public int PollCount { get; set; }
}

public sealed record CertificateInfo(
    string Subject,
    string Issuer,
    string Thumbprint,
    string SerialNumber,
    DateTime NotBefore,
    DateTime NotAfter,
    bool HasPrivateKey);

public sealed record CertificateValidationResult(
    bool Found,
    bool IsValid,
    bool HasPrivateKey,
    string Message,
    CertificateInfo? Certificate = null);

public sealed record TraceXConnectionCheckResult(bool Success, string Message);
