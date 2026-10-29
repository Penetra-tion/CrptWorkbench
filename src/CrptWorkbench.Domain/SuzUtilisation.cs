namespace CrptWorkbench.Domain;

public sealed class SuzUtilisationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public CrptIntegrationMode Mode { get; set; }
    public string ProductGroup { get; set; } = "";
    public string PackageType { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string OmsId { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string CertificateThumbprint { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public int CodeCount { get; set; }
    public int ReportCount { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public Guid? SubmissionOwner { get; set; }
}

public sealed class SuzUtilisationReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public int Number { get; set; }
    public int CodeCount { get; set; }
    public string ProtectedPayload { get; set; } = "";
    public string State { get; set; } = "Prepared";
    public string? ReportId { get; set; }
    public string? RemoteStatus { get; set; }
    public string? ReceiptState { get; set; }
    public string? Error { get; set; }
    public long? SubmittedAtUnixMs { get; set; }
    public long? LastCheckedAtUnixMs { get; set; }
    public long? NextCheckAtUnixMs { get; set; }
    public long? TrackingUntilUnixMs { get; set; }
    public Guid? TrackingOwner { get; set; }
    public long? LeaseUntilUnixMs { get; set; }
    public int PollCount { get; set; }
}

public sealed record SuzUtilisationJobDetails(SuzUtilisationJob Job, IReadOnlyList<SuzUtilisationReport> Reports);
public sealed record SuzUtilisationDraft(SuzUtilisationReport Report, string Json);
public interface ISuzUtilisationRepository
{
    Task AddAsync(SuzUtilisationJob job, IReadOnlyList<SuzUtilisationDraft> drafts, CancellationToken cancellationToken = default);
    Task<SuzUtilisationJobDetails?> GetAsync(Guid profileId, CrptIntegrationMode mode, Guid jobId, CancellationToken cancellationToken = default);
    Task<RecordPage<SuzUtilisationJob>> ListPageAsync(Guid profileId, CrptIntegrationMode mode, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SuzUtilisationJob>> GetManyAsync(Guid profileId, CrptIntegrationMode mode, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);
    Task<bool> ClaimSubmissionAsync(Guid jobId, Guid owner, CancellationToken cancellationToken = default);
    Task ReleaseSubmissionAsync(Guid jobId, Guid owner, CancellationToken cancellationToken = default);
    Task<string?> ReadSubmissionPayloadAsync(Guid jobId, Guid reportId, Guid owner, CancellationToken cancellationToken = default);
    Task<string?> StartReportAsync(Guid jobId, Guid reportId, Guid owner, CancellationToken cancellationToken = default);
    Task CompleteSubmissionAsync(SuzUtilisationReport report, CancellationToken cancellationToken = default);
    Task RecoverSendingAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SuzUtilisationReport>> ClaimDueAsync(long now, int take, CancellationToken cancellationToken = default);
    Task<SuzUtilisationJob?> GetJobForTrackingAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<bool> CompleteCheckAsync(SuzUtilisationReport report, Guid owner, CancellationToken cancellationToken = default);
    Task<bool> RenewCheckLeaseAsync(Guid reportId, Guid owner, long nowUnixMs, long leaseUntilUnixMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<bool> ScheduleAsync(Guid profileId, CrptIntegrationMode mode, Guid reportId, long now, bool resume, CancellationToken cancellationToken = default);
}
