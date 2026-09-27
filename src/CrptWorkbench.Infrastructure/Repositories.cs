using CrptWorkbench.Domain;
using Microsoft.EntityFrameworkCore;

namespace CrptWorkbench.Infrastructure;

public sealed class ProfileRepository(IDbContextFactory<WorkbenchDbContext> factory) : IProfileRepository
{
    public async Task<IReadOnlyList<CrptProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Profiles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<CrptProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Profiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task UpsertAsync(CrptProfile profile, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Profiles.SingleOrDefaultAsync(x => x.Id == profile.Id, cancellationToken);
        if (existing is null)
        {
            db.Profiles.Add(profile);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(profile);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ApiRequestLogRepository(IDbContextFactory<WorkbenchDbContext> factory) : IApiRequestLogRepository
{
    public async Task AddAsync(ApiRequestLog entry, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.ApiRequestLogs.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApiRequestLog>> ListAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ApiRequestLogs.AsNoTracking().OrderByDescending(x => x.CreatedAtUnixMs).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<ApiRequestLog?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ApiRequestLogs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }
}

public sealed class FavoriteApiOperationRepository(IDbContextFactory<WorkbenchDbContext> factory) : IFavoriteApiOperationRepository
{
    public async Task<IReadOnlyList<string>> ListOperationIdsAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.FavoriteApiOperations.AsNoTracking()
            .Where(x => x.ProfileId == profileId)
            .Select(x => x.OperationId)
            .ToListAsync(cancellationToken);
    }

    public async Task SetAsync(Guid profileId, string operationId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.FavoriteApiOperations.SingleOrDefaultAsync(
            x => x.ProfileId == profileId && x.OperationId == operationId,
            cancellationToken);

        if (isFavorite && existing is null)
            db.FavoriteApiOperations.Add(new FavoriteApiOperation { ProfileId = profileId, OperationId = operationId });
        else if (!isFavorite && existing is not null)
            db.FavoriteApiOperations.Remove(existing);
        else
            return;

        await db.SaveChangesAsync(cancellationToken);
    }
}
