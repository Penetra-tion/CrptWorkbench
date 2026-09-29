namespace CrptWorkbench.Domain;

public sealed class BulkCodeJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public CrptIntegrationMode Mode { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string State { get; set; } = "Queued";
    public long FileBytes { get; set; }
    public long NextOffset { get; set; }
    public long NextBatchNumber { get; set; }
    public long LinesRead { get; set; }
    public long CodesCompleted { get; set; }
    public long CodesFound { get; set; }
    public long CodeErrors { get; set; }
    public long FailedBatches { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long CreatedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BulkCodeBatch
{
    public Guid JobId { get; set; }
    public long Number { get; set; }
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
    public int CodeCount { get; set; }
    public string State { get; set; } = "Queued";
    public string? Error { get; set; }
}

public interface IBulkCodeJobRepository
{
    Task AddAsync(BulkCodeJob job, CancellationToken cancellationToken);
    Task<IReadOnlyList<BulkCodeJob>> ListAsync(Guid profileId, CancellationToken cancellationToken);
    Task<BulkCodeJob?> GetAsync(Guid profileId, Guid id, CancellationToken cancellationToken);
    Task<BulkCodeJob?> NextRunnableAsync(CrptIntegrationMode mode, CancellationToken cancellationToken);
    Task<IReadOnlyList<BulkCodeBatch>> ListBatchesAsync(Guid jobId, string state, int take, CancellationToken cancellationToken);
    Task AddBatchAsync(BulkCodeBatch batch, long linesRead, CancellationToken cancellationToken);
    Task CompleteBatchAsync(Guid jobId, long number, int found, int errors, CancellationToken cancellationToken);
    Task FailBatchAsync(Guid jobId, long number, string error, CancellationToken cancellationToken);
    Task SetJobStateAsync(Guid profileId, Guid id, string state, string? error, CancellationToken cancellationToken);
    Task<bool> StartReadyAsync(Guid profileId, Guid id, CancellationToken cancellationToken);
    Task RetryFailuresAsync(Guid profileId, Guid id, CancellationToken cancellationToken);
    Task DeleteAsync(Guid profileId, Guid id, CancellationToken cancellationToken);
}

public interface IBulkCodeFileStore
{
    Task SaveSourceAsync(Guid jobId, Stream source, CancellationToken cancellationToken);
    Stream OpenSource(Guid jobId, long offset);
    long SourceLength(Guid jobId);
    Task WriteFragmentAsync(Guid jobId, long batchNumber, IReadOnlyList<(string Status, string Code)> entries, int found, int errors, CancellationToken cancellationToken);
    bool FragmentExists(Guid jobId, long batchNumber);
    (int Found, int Errors) FragmentSummary(Guid jobId, long batchNumber);
    IAsyncEnumerable<string> ReadCodesAsync(Guid jobId, long batchCount, IReadOnlySet<string> statuses, CancellationToken cancellationToken);
    void Delete(Guid jobId);
}
