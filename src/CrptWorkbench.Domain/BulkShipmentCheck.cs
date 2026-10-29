namespace CrptWorkbench.Domain;

public sealed record BulkShipmentCheckScope(Guid ProfileId, Guid JobId, CrptIntegrationMode Mode, string Inn);
public sealed record BulkShipmentCheckProgress(long NextBatchNumber, long ByteOffset, bool Completed = false, long ReviewedRows = 0);
public sealed record BulkShipmentCheckedRow(ShipmentCheckResult Result, string Status);

public interface IBulkShipmentCheckRepository
{
    Task<IReadOnlyDictionary<string, ShipmentCheckResult>> GetResultsAsync(Guid profileId, Guid jobId,
        CrptIntegrationMode mode, string inn, IReadOnlyList<string> codes, CancellationToken cancellationToken);
    Task SaveAsync(BulkShipmentCheckScope scope, ShipmentCheckResult result, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<ShipmentCheckState, long>> CountAsync(BulkShipmentCheckScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, long>> CountByStatusAsync(BulkShipmentCheckScope scope, ShipmentCheckState state, CancellationToken cancellationToken);
    Task<BulkShipmentCheckProgress?> GetProgressAsync(BulkShipmentCheckScope scope, CancellationToken cancellationToken);
    Task SavePageAsync(BulkShipmentCheckScope scope, IReadOnlyList<BulkShipmentCheckedRow> rows,
        BulkShipmentCheckProgress progress, CancellationToken cancellationToken);
}
