using CrptWorkbench.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CrptWorkbench.Infrastructure;

public sealed class DataProtectionSecretStore(
    IDbContextFactory<WorkbenchDbContext> factory,
    IDataProtectionProvider protectionProvider) : ISecretStore
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("CrptWorkbench.Secrets.v1");

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entry = await db.ProtectedSecrets.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        return entry is null ? null : _protector.Unprotect(entry.ProtectedValue);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entry = await db.ProtectedSecrets.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (entry is null)
        {
            entry = new ProtectedSecret { Key = key };
            db.ProtectedSecrets.Add(entry);
        }

        entry.ProtectedValue = _protector.Protect(value);
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entry = await db.ProtectedSecrets.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (entry is null) return;
        db.ProtectedSecrets.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
    }
}
