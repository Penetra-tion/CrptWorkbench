using CrptWorkbench.Domain;
using Microsoft.EntityFrameworkCore;

namespace CrptWorkbench.Infrastructure;

public sealed class BulkCodeJobRepository(IDbContextFactory<WorkbenchDbContext> factory) : IBulkCodeJobRepository
{
    public async Task<RecordPage<BulkCodeJob>> ListPageAsync(Guid profileId, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.BulkCodeJobs.AsNoTracking().Where(x => x.ProfileId == profileId);
        var total = await query.LongCountAsync(cancellationToken);
        if (cursor is not null)
        {
            var boundaryIds = cursor.BoundaryIds.ToArray();
            query = query.Where(x => x.CreatedAtUnixMs < cursor.CreatedAtUnixMs ||
                (x.CreatedAtUnixMs == cursor.CreatedAtUnixMs && !boundaryIds.Contains(x.Id)));
        }
        var rows = await query.OrderByDescending(x => x.CreatedAtUnixMs).ThenBy(x => x.Id)
            .Take(RecordPage<BulkCodeJob>.Size + 1).ToListAsync(cancellationToken);
        var more = rows.Count > RecordPage<BulkCodeJob>.Size;
        var items = rows.Take(RecordPage<BulkCodeJob>.Size).ToArray();
        RecordPageCursor? next = null;
        if (more)
        {
            var timestamp = items[^1].CreatedAtUnixMs;
            var ids = items.Where(x => x.CreatedAtUnixMs == timestamp).Select(x => x.Id);
            if (cursor?.CreatedAtUnixMs == timestamp) ids = cursor.BoundaryIds.Concat(ids);
            next = new(timestamp, ids.ToArray());
        }
        return new(items, next, total);
    }

    public async Task AddAsync(BulkCodeJob job, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.BulkCodeJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BulkCodeJob>> ListAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BulkCodeJobs.AsNoTracking().Where(x => x.ProfileId == profileId)
            .OrderByDescending(x => x.CreatedAtUnixMs).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BulkCodeJob>> GetManyAsync(Guid profileId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var result = new List<BulkCodeJob>();
        foreach (var chunk in ids.Chunk(500))
            result.AddRange(await db.BulkCodeJobs.AsNoTracking().Where(x => x.ProfileId == profileId && chunk.Contains(x.Id)).ToListAsync(cancellationToken));
        return result.OrderByDescending(x => x.CreatedAtUnixMs).ThenBy(x => x.Id).ToArray();
    }

    public async Task<BulkCodeJob?> GetAsync(Guid profileId, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BulkCodeJobs.AsNoTracking().SingleOrDefaultAsync(x => x.ProfileId == profileId && x.Id == id, cancellationToken);
    }

    public async Task<BulkCodeJob?> NextRunnableAsync(CrptIntegrationMode mode, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BulkCodeJobs.AsNoTracking().Where(x => x.Mode == mode && (x.State == "Queued" || x.State == "Running" || x.State == "CancelRequested" || x.State == "PauseRequested"))
            .OrderBy(x => x.CreatedAtUnixMs).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BulkCodeBatch>> ListBatchesAsync(Guid jobId, string state, int take, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BulkCodeBatches.AsNoTracking().Where(x => x.JobId == jobId && x.State == state)
            .OrderBy(x => x.Number).Take(take).ToListAsync(cancellationToken);
    }

    public async Task AddBatchAsync(BulkCodeBatch batch, long linesRead, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.BulkCodeBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        await db.BulkCodeJobs.Where(x => x.Id == batch.JobId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.NextOffset, batch.EndOffset)
            .SetProperty(x => x.NextBatchNumber, batch.Number + 1)
            .SetProperty(x => x.LinesRead, linesRead)
            .SetProperty(x => x.State, x => x.State == "Queued" ? "Running" : x.State)
            .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteBatchAsync(Guid jobId, long number, int found, int errors, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var batch = await db.BulkCodeBatches.SingleAsync(x => x.JobId == jobId && x.Number == number, cancellationToken);
        if (batch.State == "Completed") return;
        batch.State = "Completed";
        batch.Error = null;
        var job = await db.BulkCodeJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        job.CodesCompleted += batch.CodeCount;
        job.CodesFound += found;
        job.CodeErrors += errors;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FailBatchAsync(Guid jobId, long number, string error, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var batch = await db.BulkCodeBatches.SingleAsync(x => x.JobId == jobId && x.Number == number, cancellationToken);
        if (batch.State is "Completed" or "Failed") return;
        batch.State = "Failed";
        batch.Error = error;
        var job = await db.BulkCodeJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        job.FailedBatches++;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetJobStateAsync(Guid profileId, Guid id, string state, string? error, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var job = await db.BulkCodeJobs.SingleOrDefaultAsync(x => x.ProfileId == profileId && x.Id == id, cancellationToken);
        if (job is null) return;
        job.State = state;
        job.Error = error;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> StartReadyAsync(Guid profileId, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BulkCodeJobs.Where(x => x.ProfileId == profileId && x.Id == id && x.State == "Ready")
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, "Queued")
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken) == 1;
    }

    public async Task<bool> TransitionStateAsync(Guid profileId, Guid id, CrptIntegrationMode mode,
        IReadOnlyList<string> expectedStates, string state, string? error, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BulkCodeJobs.Where(x => x.ProfileId == profileId && x.Id == id && x.Mode == mode && expectedStates.Contains(x.State))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state).SetProperty(x => x.Error, error)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken) == 1;
    }

    public async Task<bool> TryBeginBatchAsync(Guid profileId, Guid id, CrptIntegrationMode mode, long number, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // The conditional claim is the dispatch boundary: pause and dispatch serialize in SQLite.
        // Running batches remain recoverable after a process stops before publishing their fragment.
        return await db.BulkCodeBatches.Where(x => x.JobId == id && x.Number == number && (x.State == "Queued" || x.State == "Running") &&
                db.BulkCodeJobs.Any(j => j.Id == id && j.ProfileId == profileId && j.Mode == mode && (j.State == "Queued" || j.State == "Running")))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Running"), cancellationToken) == 1;
    }

    public async Task RetryFailuresAsync(Guid profileId, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.BulkCodeJobs.Where(x => x.ProfileId == profileId && x.Id == id &&
            (x.State == "Partial" || x.State == "Failed" || x.State == "Canceled"))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedBatches, 0).SetProperty(x => x.State, "Queued")
                .SetProperty(x => x.Error, (string?)null).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken) == 0) return;
        await db.BulkCodeBatches.Where(x => x.JobId == id && x.State == "Failed")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Queued").SetProperty(x => x.Error, (string?)null), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid profileId, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.BulkCodeJobs.Where(x => x.ProfileId == profileId && x.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}
