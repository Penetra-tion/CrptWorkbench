using CrptWorkbench.Domain;
using Microsoft.EntityFrameworkCore;

namespace CrptWorkbench.Infrastructure;

public sealed class BulkCodeJobRepository(IDbContextFactory<WorkbenchDbContext> factory) : IBulkCodeJobRepository
{
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

    public async Task<BulkCodeJob?> GetAsync(Guid profileId, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BulkCodeJobs.AsNoTracking().SingleOrDefaultAsync(x => x.ProfileId == profileId && x.Id == id, cancellationToken);
    }

    public async Task<BulkCodeJob?> NextRunnableAsync(CrptIntegrationMode mode, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BulkCodeJobs.AsNoTracking().Where(x => x.Mode == mode && (x.State == "Queued" || x.State == "Running" || x.State == "CancelRequested"))
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
        var job = await db.BulkCodeJobs.SingleAsync(x => x.Id == batch.JobId, cancellationToken);
        job.NextOffset = batch.EndOffset;
        job.NextBatchNumber = batch.Number + 1;
        job.LinesRead = linesRead;
        job.State = "Running";
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
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

    public async Task RetryFailuresAsync(Guid profileId, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var job = await db.BulkCodeJobs.SingleOrDefaultAsync(x => x.ProfileId == profileId && x.Id == id, cancellationToken);
        if (job is null || job.State is "Running" or "Queued") return;
        await db.BulkCodeBatches.Where(x => x.JobId == id && x.State == "Failed")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Queued").SetProperty(x => x.Error, (string?)null), cancellationToken);
        job.FailedBatches = 0;
        job.State = "Queued";
        job.Error = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid profileId, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.BulkCodeJobs.Where(x => x.ProfileId == profileId && x.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}
