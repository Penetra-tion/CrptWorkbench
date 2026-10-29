using CrptWorkbench.Domain;
using Microsoft.EntityFrameworkCore;

namespace CrptWorkbench.Infrastructure;

public sealed class BulkShipmentCheck
{
    public Guid ProfileId { get; set; }
    public Guid JobId { get; set; }
    public CrptIntegrationMode Mode { get; set; }
    public string Inn { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public ShipmentCheckState State { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public string? Reason { get; set; }
}

public sealed class BulkShipmentCheckProgressEntity
{
    public Guid ProfileId { get; set; }
    public Guid JobId { get; set; }
    public CrptIntegrationMode Mode { get; set; }
    public string Inn { get; set; } = string.Empty;
    public long NextBatchNumber { get; set; }
    public long ByteOffset { get; set; }
    public bool Completed { get; set; }
    public long ReviewedRows { get; set; }
}

public sealed class BulkShipmentCheckCounter
{
    public Guid ProfileId { get; set; }
    public Guid JobId { get; set; }
    public CrptIntegrationMode Mode { get; set; }
    public string Inn { get; set; } = string.Empty;
    public ShipmentCheckState State { get; set; }
    public string Status { get; set; } = string.Empty;
    public long Count { get; set; }
}

public sealed class BulkShipmentCheckRepository(IDbContextFactory<WorkbenchDbContext> factory) : IBulkShipmentCheckRepository
{
    public async Task<IReadOnlyDictionary<string, ShipmentCheckResult>> GetResultsAsync(Guid profileId, Guid jobId,
        CrptIntegrationMode mode, string inn, IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var scope = new BulkShipmentCheckScope(profileId, jobId, mode, inn);
        var results = new Dictionary<string, ShipmentCheckResult>(StringComparer.Ordinal);
        if (!await OwnsJobAsync(db, scope, cancellationToken)) return results;
        foreach (var chunk in codes.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            var rows = await Results(db, scope).AsNoTracking().Where(x => chunk.Contains(x.Code)).ToListAsync(cancellationToken);
            foreach (var row in rows) results[row.Code] = new(row.Code, row.State, row.CheckedAt, row.Reason);
        }
        return results;
    }

    public async Task SaveAsync(BulkShipmentCheckScope scope, ShipmentCheckResult result, CancellationToken cancellationToken)
    {
        Validate(scope, result);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await RequireJobAsync(db, scope, cancellationToken);
        await UpsertResultAsync(db, scope, result, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<ShipmentCheckState, long>> CountAsync(BulkShipmentCheckScope scope, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsJobAsync(db, scope, cancellationToken)) return new Dictionary<ShipmentCheckState, long>();
        return await Results(db, scope).GroupBy(x => x.State).Select(g => new { State = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.State, x => x.Count, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, long>> CountByStatusAsync(BulkShipmentCheckScope scope, ShipmentCheckState state, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsJobAsync(db, scope, cancellationToken)) return new Dictionary<string, long>();
        return await Counters(db, scope).AsNoTracking().Where(x => x.State == state)
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);
    }

    public async Task<BulkShipmentCheckProgress?> GetProgressAsync(BulkShipmentCheckScope scope, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsJobAsync(db, scope, cancellationToken)) return null;
        var p = await Progress(db, scope).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return p is null ? null : new(p.NextBatchNumber, p.ByteOffset, p.Completed, p.ReviewedRows);
    }

    public async Task SavePageAsync(BulkShipmentCheckScope scope, IReadOnlyList<BulkShipmentCheckedRow> rows,
        BulkShipmentCheckProgress progress, CancellationToken cancellationToken)
    {
        if (progress.NextBatchNumber < 0 || progress.ByteOffset < 0 || progress.ReviewedRows < 0)
            throw new ArgumentOutOfRangeException(nameof(progress));
        foreach (var row in rows)
        {
            Validate(scope, row.Result);
            if (string.IsNullOrWhiteSpace(row.Status) || row.Status.Length > 64) throw new ArgumentException("Invalid status.", nameof(rows));
        }
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RequireJobAsync(db, scope, cancellationToken);
        var previous = await Progress(db, scope).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (previous is not null && (previous.NextBatchNumber > progress.NextBatchNumber ||
            (previous.NextBatchNumber == progress.NextBatchNumber && previous.ByteOffset > progress.ByteOffset) ||
            previous.ReviewedRows > progress.ReviewedRows)) return;
        if (previous is null || previous.ReviewedRows < progress.ReviewedRows)
        {
            if (progress.ReviewedRows - (previous?.ReviewedRows ?? 0) != rows.Count)
                throw new ArgumentException("Page rows must match checkpoint advancement.", nameof(progress));
            foreach (var result in rows.Select(x => x.Result).DistinctBy(x => x.Code))
                await UpsertResultAsync(db, scope, result, cancellationToken);
            // Another checking session may have saved this code first. Counters must use
            // the same persisted outcome as previews and exports, within this transaction.
            var persistedStates = new Dictionary<string, ShipmentCheckState>(StringComparer.Ordinal);
            foreach (var chunk in rows.Select(x => x.Result.Code).Distinct(StringComparer.Ordinal).Chunk(500))
            {
                var savedResults = await Results(db, scope).AsNoTracking().Where(x => chunk.Contains(x.Code))
                    .Select(x => new { x.Code, x.State }).ToListAsync(cancellationToken);
                foreach (var savedResult in savedResults) persistedStates.Add(savedResult.Code, savedResult.State);
            }
            foreach (var group in rows.GroupBy(x => (State: persistedStates[x.Result.Code], x.Status)))
            {
                var count = group.LongCount();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO BulkShipmentCheckCounters (ProfileId, JobId, Mode, Inn, State, Status, Count) VALUES ({scope.ProfileId}, {scope.JobId}, {(int)scope.Mode}, {scope.Inn}, {(int)group.Key.State}, {group.Key.Status}, {count}) ON CONFLICT (ProfileId, JobId, Mode, Inn, State, Status) DO UPDATE SET Count = Count + excluded.Count", cancellationToken);
            }
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO BulkShipmentCheckProgresses (ProfileId, JobId, Mode, Inn, NextBatchNumber, ByteOffset, Completed, ReviewedRows) VALUES ({scope.ProfileId}, {scope.JobId}, {(int)scope.Mode}, {scope.Inn}, {progress.NextBatchNumber}, {progress.ByteOffset}, {progress.Completed}, {progress.ReviewedRows}) ON CONFLICT (ProfileId, JobId, Mode, Inn) DO UPDATE SET NextBatchNumber = excluded.NextBatchNumber, ByteOffset = excluded.ByteOffset, Completed = excluded.Completed, ReviewedRows = excluded.ReviewedRows", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static Task<int> UpsertResultAsync(WorkbenchDbContext db, BulkShipmentCheckScope scope, ShipmentCheckResult result, CancellationToken token) =>
        db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO BulkShipmentChecks (ProfileId, JobId, Mode, Inn, Code, State, CheckedAt, Reason) VALUES ({scope.ProfileId}, {scope.JobId}, {(int)scope.Mode}, {scope.Inn}, {result.Code}, {(int)result.State}, {result.CheckedAt}, {result.Reason}) ON CONFLICT (ProfileId, JobId, Mode, Inn, Code) DO NOTHING", token);

    private static IQueryable<BulkShipmentCheck> Results(WorkbenchDbContext db, BulkShipmentCheckScope s) =>
        db.BulkShipmentChecks.Where(x => x.ProfileId == s.ProfileId && x.JobId == s.JobId && x.Mode == s.Mode && x.Inn == s.Inn);
    private static IQueryable<BulkShipmentCheckCounter> Counters(WorkbenchDbContext db, BulkShipmentCheckScope s) =>
        db.BulkShipmentCheckCounters.Where(x => x.ProfileId == s.ProfileId && x.JobId == s.JobId && x.Mode == s.Mode && x.Inn == s.Inn);
    private static IQueryable<BulkShipmentCheckProgressEntity> Progress(WorkbenchDbContext db, BulkShipmentCheckScope s) =>
        db.BulkShipmentCheckProgresses.Where(x => x.ProfileId == s.ProfileId && x.JobId == s.JobId && x.Mode == s.Mode && x.Inn == s.Inn);
    private static Task<bool> OwnsJobAsync(WorkbenchDbContext db, BulkShipmentCheckScope scope, CancellationToken token) =>
        db.BulkCodeJobs.AnyAsync(x => x.Id == scope.JobId && x.ProfileId == scope.ProfileId && x.Mode == scope.Mode, token);
    private static async Task RequireJobAsync(WorkbenchDbContext db, BulkShipmentCheckScope scope, CancellationToken token)
    {
        if (!await OwnsJobAsync(db, scope, token)) throw new InvalidOperationException("Shipment job is unavailable in this scope.");
    }
    private static void Validate(BulkShipmentCheckScope scope, ShipmentCheckResult result)
    {
        if (scope.Inn.Length > 12) throw new ArgumentException("Invalid INN scope.", nameof(scope));
        if (string.IsNullOrEmpty(result.Code) || result.Code.Length > 74 || result.State == ShipmentCheckState.NotChecked ||
            !Enum.IsDefined(result.State) || result.CheckedAt is null || result.Reason?.Length > 256)
            throw new ArgumentException("Invalid shipment result.", nameof(result));
    }
}
