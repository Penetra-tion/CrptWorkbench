namespace CrptWorkbench.Domain;

public sealed record AggregationTreeJob(
    Guid Id, Guid ProfileId, CrptIntegrationMode Mode, string Name, string State, bool IncludeSiblings,
    long SourceBytes, long SourceOffset, long LinesRead, long RejectedRows, long NodesTotal,
    long NodesLoaded, long NodeErrors, long Revision, DateTimeOffset CreatedAt, string? Error);

public sealed record AggregationJobNode(string Code, string? PackageType, string? Status,
    string State, string? Error, long ChildCount, bool Valid, bool Requested, bool HasChildren);
public sealed record AggregationNodePage(IReadOnlyList<AggregationJobNode> Items, string? NextCursor);
public sealed record AggregationRejectedRow(long Line, string Value, string Reason);
public sealed record AggregationRejectedPage(IReadOnlyList<AggregationRejectedRow> Items, long? NextCursor);
public sealed record AggregationSeed(long Line, string? Code, string? Value = null, string? Error = null);
public sealed record AggregationFetchBatch(Guid Id, IReadOnlyList<string> Codes);
public sealed record AggregationExportOptions(bool FullTree = true, IReadOnlyList<string>? PackageTypes = null);
public sealed record AggregationExportFile(Guid Id, Guid JobId, Guid ProfileId, CrptIntegrationMode Mode,
    string Path, long Lines);

public interface IAggregationTreeSourceStore
{
    Task<long> SaveAsync(Guid jobId, Stream source, CancellationToken cancellationToken);
    Stream OpenRead(Guid jobId, long offset = 0);
    Stream CreateResponse(Guid jobId, Guid batchId);
    void DeleteResponse(Guid jobId, Guid batchId);
    string ExportPath(Guid jobId, Guid exportId);
    Stream OpenExport(Guid jobId, Guid exportId);
    void CleanupResponses();
    void Delete(Guid jobId);
}

public interface IAggregationTreeRepository
{
    Task CreateAsync(Guid id, Guid profileId, CrptIntegrationMode mode, string name, long bytes, CancellationToken ct);
    Task<AggregationTreeJob?> GetAsync(Guid profileId, Guid id, CrptIntegrationMode mode, CancellationToken ct);
    Task<IReadOnlyList<AggregationTreeJob>> GetManyAsync(Guid profileId, IReadOnlyList<Guid> ids, CrptIntegrationMode mode, CancellationToken ct) => throw new NotSupportedException();
    Task<IReadOnlyList<AggregationTreeJob>> ListAsync(Guid profileId, CrptIntegrationMode mode, long? before, CancellationToken ct);
    Task<AggregationTreeJob?> NextAsync(CrptIntegrationMode mode, CancellationToken ct);
    Task<bool> TransitionAsync(Guid profileId, Guid id, CrptIntegrationMode mode, IReadOnlyList<string> from, string to, string? error, CancellationToken ct);
    Task<bool> StartAsync(Guid profileId, Guid id, CrptIntegrationMode mode, bool includeSiblings, CancellationToken ct);
    Task RecoverAsync(CrptIntegrationMode mode, CancellationToken ct);
    Task ImportAsync(Guid id, long offset, IReadOnlyList<AggregationSeed> rows, CancellationToken ct);
    Task<AggregationFetchBatch?> TakeBatchAsync(Guid id, CancellationToken ct);
    Task StageChildrenAsync(Guid jobId, Guid batchId, string code, IReadOnlyList<string> children, CancellationToken ct);
    Task StageNodeAsync(Guid jobId, Guid batchId, AggregationResponseNode node, CancellationToken ct);
    async Task StageNodesAsync(Guid jobId, Guid batchId, IReadOnlyList<AggregationResponseNode> nodes, CancellationToken ct)
    {
        foreach (var node in nodes) await StageNodeAsync(jobId, batchId, node, ct);
    }
    Task PublishAsync(Guid jobId, AggregationFetchBatch batch, string? error, CancellationToken ct);
    Task ResetBatchAsync(Guid jobId, AggregationFetchBatch batch, CancellationToken ct);
    Task ValidateAsync(Guid jobId, CancellationToken ct);
    Task<AggregationNodePage> NodesAsync(Guid jobId, string? parent, string? cursor, CancellationToken ct);
    Task<IReadOnlyList<string>> TypesAsync(Guid jobId, CancellationToken ct);
    Task<AggregationRejectedPage> RejectedAsync(Guid jobId, long? after, CancellationToken ct);
    Task<AggregationExportFile> ExportAsync(Guid profileId, Guid jobId, CrptIntegrationMode mode, AggregationExportOptions options, CancellationToken ct);
    Task DeleteAsync(Guid jobId, CancellationToken ct);
}
