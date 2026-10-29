using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class BulkShipmentPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PopulatedPreviousSchemaKeepsJobAndStartsUnchecked()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = await f.Factory.CreateDbContextAsync(Ct);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("Старый файл.txt", (await db.BulkCodeJobs.SingleAsync(Ct)).FileName);
        Assert.Equal(17, (await db.BulkCodeBatches.SingleAsync(Ct)).EndOffset);
        Assert.Empty(await f.Repository.GetResultsAsync(f.Scope.ProfileId, f.Scope.JobId, f.Scope.Mode, f.Scope.Inn, ["code"], Ct));
        Assert.Null(await f.Repository.GetProgressAsync(f.Scope, Ct));
    }

    [Fact]
    public async Task PartialResultsSurviveReopenAndScopesCannotReadOrWriteOtherJob()
    {
        await using var f = await Fixture.CreateAsync();
        var checkedAt = DateTimeOffset.UtcNow;
        await f.Repository.SaveAsync(f.Scope, new("code", ShipmentCheckState.Shipped, checkedAt), Ct);
        var reopened = new BulkShipmentCheckRepository(f.Factory);
        Assert.Equal(checkedAt, (await reopened.GetResultsAsync(f.Scope.ProfileId, f.Scope.JobId, f.Scope.Mode, f.Scope.Inn, ["code"], Ct))["code"].CheckedAt);
        foreach (var other in new[] { f.Scope with { ProfileId = Guid.NewGuid() }, f.Scope with { JobId = Guid.NewGuid() },
                     f.Scope with { Mode = CrptIntegrationMode.Production }, f.Scope with { Inn = "7800000000" } })
        {
            Assert.Empty(await reopened.GetResultsAsync(other.ProfileId, other.JobId, other.Mode, other.Inn, ["code"], Ct));
            Assert.Empty(await reopened.CountAsync(other, Ct));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveAsync(f.Scope with { ProfileId = Guid.NewGuid() }, new("code", ShipmentCheckState.Shipped, checkedAt), Ct));
    }

    [Fact]
    public async Task PageCountersIncludeDuplicatesAndCheckpointPreventsDoubleCounting()
    {
        await using var f = await Fixture.CreateAsync();
        var shipped = new ShipmentCheckResult("one", ShipmentCheckState.Shipped, DateTimeOffset.UtcNow);
        var unknown = new ShipmentCheckResult("two", ShipmentCheckState.Unknown, DateTimeOffset.UtcNow, "Нет данных");
        BulkShipmentCheckedRow[] rows = [new(shipped, "INTRODUCED"), new(shipped, "INTRODUCED"), new(unknown, "EMITTED")];
        var progress = new BulkShipmentCheckProgress(0, 50, ReviewedRows: 3);
        await f.Repository.SavePageAsync(f.Scope, rows, progress, Ct);
        await f.Repository.SavePageAsync(f.Scope, rows, progress, Ct);
        Assert.Equal(2, (await f.Repository.CountByStatusAsync(f.Scope, ShipmentCheckState.Shipped, Ct))["INTRODUCED"]);
        Assert.Equal(1, (await f.Repository.CountAsync(f.Scope, Ct))[ShipmentCheckState.Shipped]);
        Assert.Equal(progress, await f.Repository.GetProgressAsync(f.Scope, Ct));
        await f.Repository.SavePageAsync(f.Scope, [], progress with { Completed = true }, Ct);
        Assert.True((await f.Repository.GetProgressAsync(f.Scope, Ct))!.Completed);
        await new BulkCodeJobRepository(f.Factory).DeleteAsync(f.Scope.ProfileId, f.Scope.JobId, Ct);
        await using var db = await f.Factory.CreateDbContextAsync(Ct);
        Assert.Empty(await db.BulkShipmentChecks.ToListAsync(Ct));
        Assert.Empty(await db.BulkShipmentCheckCounters.ToListAsync(Ct));
        Assert.Empty(await db.BulkShipmentCheckProgresses.ToListAsync(Ct));
    }

    [Fact]
    public async Task CountersUsePersistedFirstResultWhenAnotherSessionSuppliesConflictingOutcome()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Repository.SaveAsync(f.Scope, new("one", ShipmentCheckState.Shipped, DateTimeOffset.UtcNow), Ct);
        var conflicting = new ShipmentCheckResult("one", ShipmentCheckState.NotShipped, DateTimeOffset.UtcNow);
        await f.Repository.SavePageAsync(f.Scope, [new(conflicting, "INTRODUCED")],
            new BulkShipmentCheckProgress(1, 0, Completed: true, ReviewedRows: 1), Ct);
        var persisted = await f.Repository.GetResultsAsync(f.Scope.ProfileId, f.Scope.JobId, f.Scope.Mode,
            f.Scope.Inn, ["one"], Ct);
        Assert.Equal(ShipmentCheckState.Shipped, persisted["one"].State);
        Assert.Equal(1, (await f.Repository.CountByStatusAsync(f.Scope, ShipmentCheckState.Shipped, Ct))["INTRODUCED"]);
        Assert.Empty(await f.Repository.CountByStatusAsync(f.Scope, ShipmentCheckState.NotShipped, Ct));
    }

    [Fact]
    public async Task ReadsAreBatchedAndCancellationDoesNotSave()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Repository.SaveAsync(f.Scope, new("saved", ShipmentCheckState.NotShipped, DateTimeOffset.UtcNow), Ct);
        var codes = Enumerable.Range(0, 1200).Select(x => $"missing{x}").Append("saved").Append("saved").ToArray();
        Assert.Single(await f.Repository.GetResultsAsync(f.Scope.ProfileId, f.Scope.JobId, f.Scope.Mode, f.Scope.Inn, codes, Ct));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Repository.SaveAsync(f.Scope, new("cancelled", ShipmentCheckState.Shipped, DateTimeOffset.UtcNow), cts.Token));
        Assert.Equal(1, (await f.Repository.CountAsync(f.Scope, Ct))[ShipmentCheckState.NotShipped]);
    }

    private sealed class Fixture(SqliteConnection anchor, PooledDbContextFactory<WorkbenchDbContext> factory, BulkShipmentCheckScope scope) : IAsyncDisposable
    {
        public PooledDbContextFactory<WorkbenchDbContext> Factory { get; } = factory;
        public BulkShipmentCheckScope Scope { get; } = scope;
        public BulkShipmentCheckRepository Repository { get; } = new(factory);
        public static async Task<Fixture> CreateAsync()
        {
            var cs = $"Data Source=file:bulk-shipment-{Guid.NewGuid():N}?mode=memory&cache=shared";
            var anchor = new SqliteConnection(cs); await anchor.OpenAsync(Ct);
            var factory = new PooledDbContextFactory<WorkbenchDbContext>(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite(cs).Options);
            var scope = new BulkShipmentCheckScope(Guid.NewGuid(), Guid.NewGuid(), CrptIntegrationMode.Mock, "7700000000");
            await using var db = await factory.CreateDbContextAsync(Ct);
            await db.GetService<IMigrator>().MigrateAsync("20261003090000_AddSuzBusinessPlace", Ct);
            db.Profiles.Add(new CrptProfile { Id = scope.ProfileId, Name = "Профиль", Inn = scope.Inn });
            db.BulkCodeJobs.Add(new BulkCodeJob { Id = scope.JobId, ProfileId = scope.ProfileId, Mode = scope.Mode, FileName = "Старый файл.txt" });
            db.BulkCodeBatches.Add(new BulkCodeBatch { JobId = scope.JobId, Number = 0, EndOffset = 17 });
            await db.SaveChangesAsync(Ct);
            await db.Database.MigrateAsync(Ct);
            return new(anchor, factory, scope);
        }
        public ValueTask DisposeAsync() => anchor.DisposeAsync();
    }
}
