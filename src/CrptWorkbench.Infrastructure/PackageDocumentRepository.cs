using CrptWorkbench.Domain;
using Microsoft.EntityFrameworkCore;

namespace CrptWorkbench.Infrastructure;

public sealed class PackageDocumentRepository(IDbContextFactory<WorkbenchDbContext> factory) : IPackageDocumentRepository
{
    public async Task<RecordPage<PackageDocument>> ListPageAsync(Guid profileId, PackageOperationKind kind, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.PackageDocuments.AsNoTracking().Where(x => x.ProfileId == profileId && x.OperationKind == kind);
        var total = await query.LongCountAsync(cancellationToken);
        if (cursor is not null)
        {
            var boundaryIds = cursor.BoundaryIds.ToArray();
            query = query.Where(x => x.CreatedAtUnixMs < cursor.CreatedAtUnixMs ||
                (x.CreatedAtUnixMs == cursor.CreatedAtUnixMs && !boundaryIds.Contains(x.Id)));
        }
        var rows = await query.OrderByDescending(x => x.CreatedAtUnixMs).ThenBy(x => x.Id)
            .Take(RecordPage<PackageDocument>.Size + 1).ToListAsync(cancellationToken);
        var more = rows.Count > RecordPage<PackageDocument>.Size;
        var items = rows.Take(RecordPage<PackageDocument>.Size).ToArray();
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

    public async Task<bool> ScheduleAsync(Guid profileId, Guid id, PackageOperationKind kind, long nowUnixMs, bool resume,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.PackageDocuments.Where(x => x.Id == id && x.ProfileId == profileId &&
            x.OperationKind == kind && x.DocumentId != null && x.State == (resume ? "Paused" : "Tracking"));
        var changed = resume
            ? await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Tracking")
                .SetProperty(x => x.TrackingUntilUnixMs, nowUnixMs + 86_400_000)
                .SetProperty(x => x.NextCheckAtUnixMs, nowUnixMs).SetProperty(x => x.LeaseUntilUnixMs, (long?)null), cancellationToken)
            : await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextCheckAtUnixMs, nowUnixMs), cancellationToken);
        return changed == 1;
    }

    public async Task AddAsync(PackageDocument document, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.PackageDocuments.Add(document);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(PackageDocument document, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.PackageDocuments.Update(document);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> CompleteCheckAsync(PackageDocument document, long leaseUntilUnixMs,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // A slow response may arrive after another worker has reclaimed the document.
        // Never overwrite that worker's result or a manually renewed tracking window.
        var changed = await db.PackageDocuments.Where(x => x.Id == document.Id && x.State == "Tracking" &&
                x.LeaseUntilUnixMs == leaseUntilUnixMs)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, document.State)
                .SetProperty(x => x.RemoteStatus, document.RemoteStatus)
                .SetProperty(x => x.Error, document.Error)
                .SetProperty(x => x.LastCheckedAtUnixMs, document.LastCheckedAtUnixMs)
                .SetProperty(x => x.NextCheckAtUnixMs, document.NextCheckAtUnixMs)
                .SetProperty(x => x.PollCount, document.PollCount)
                .SetProperty(x => x.LeaseUntilUnixMs, (long?)null), cancellationToken);
        return changed == 1;
    }

    public async Task<IReadOnlyList<PackageDocument>> ListAsync(Guid profileId, PackageOperationKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.PackageDocuments.AsNoTracking().Where(x => x.ProfileId == profileId && x.OperationKind == kind)
            .OrderByDescending(x => x.CreatedAtUnixMs).ThenBy(x => x.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PackageDocument>> GetManyAsync(Guid profileId, PackageOperationKind kind, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var result = new List<PackageDocument>();
        foreach (var chunk in ids.Chunk(500))
            result.AddRange(await db.PackageDocuments.AsNoTracking().Where(x => x.ProfileId == profileId && x.OperationKind == kind && chunk.Contains(x.Id)).ToListAsync(cancellationToken));
        return result.OrderByDescending(x => x.CreatedAtUnixMs).ThenBy(x => x.Id).ToArray();
    }

    public async Task<PackageDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.PackageDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<PackageDocument>> ClaimDueAsync(long nowUnixMs, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.PackageDocuments.AsNoTracking()
            .Where(x => x.State == "Tracking" && (x.NextCheckAtUnixMs <= nowUnixMs || x.TrackingUntilUnixMs <= nowUnixMs) &&
                (x.LeaseUntilUnixMs == null || x.LeaseUntilUnixMs <= nowUnixMs))
            .OrderBy(x => x.NextCheckAtUnixMs).Take(take).Select(x => x.Id).ToListAsync(cancellationToken);
        var claimed = new List<PackageDocument>();
        foreach (var id in candidates)
        {
            var changed = await db.PackageDocuments.Where(x => x.Id == id && x.State == "Tracking" &&
                    (x.NextCheckAtUnixMs <= nowUnixMs || x.TrackingUntilUnixMs <= nowUnixMs) &&
                    (x.LeaseUntilUnixMs == null || x.LeaseUntilUnixMs <= nowUnixMs))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntilUnixMs, nowUnixMs + 120_000), cancellationToken);
            if (changed == 1)
            {
                var entry = await db.PackageDocuments.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
                claimed.Add(entry);
            }
        }
        return claimed;
    }
}
