using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SuzUtilisationPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task PayloadProtectedClaimsExclusiveRestartNeverResendsAndProfileModeIsolated()
    {
        await using var f = await Fixture.CreateAsync();
        var job = f.Job(); var report = new SuzUtilisationReport { JobId = job.Id, Number = 1, CodeCount = 1 };
        const string json = "{\"code\":\"FULL-CODE-CRYPTO-SECRET\"}";
        await f.Repository.AddAsync(job, [new(report, json)], Ct);
        await using (var db = await f.Factory.CreateDbContextAsync(Ct))
        {
            Assert.False(db.Database.HasPendingModelChanges());
            var raw = await db.SuzUtilisationReports.SingleAsync(Ct);
            Assert.DoesNotContain("CRYPTO-SECRET", raw.ProtectedPayload);
            Assert.NotEqual(json, raw.ProtectedPayload);
        }
        Assert.Null(await f.Repository.GetAsync(Guid.NewGuid(), CrptIntegrationMode.Mock, job.Id, Ct));
        Assert.Null(await f.Repository.GetAsync(f.Profile.Id, CrptIntegrationMode.Production, job.Id, Ct));
        Assert.Empty((await f.Repository.GetAsync(f.Profile.Id, CrptIntegrationMode.Mock, job.Id, Ct))!.Reports[0].ProtectedPayload);
        var owner = Guid.NewGuid();
        Assert.True(await f.Repository.ClaimSubmissionAsync(job.Id, owner, Ct));
        Assert.False(await f.Repository.ClaimSubmissionAsync(job.Id, Guid.NewGuid(), Ct));
        Assert.Equal(json, await f.Repository.StartReportAsync(job.Id, report.Id, owner, Ct));
        Assert.Null(await f.Repository.StartReportAsync(job.Id, report.Id, owner, Ct));
        await f.Repository.RecoverSendingAsync(Ct);
        Assert.Equal("Unknown", (await f.Repository.GetAsync(f.Profile.Id, CrptIntegrationMode.Mock, job.Id, Ct))!.Reports[0].State);
        Assert.True(await f.Repository.ClaimSubmissionAsync(job.Id, owner, Ct));
        Assert.Null(await f.Repository.StartReportAsync(job.Id, report.Id, owner, Ct));
    }
    [Fact]
    public async Task StaleTrackingOwnerCannotOverwriteTerminalResultAndResumeIsScoped()
    {
        await using var f = await Fixture.CreateAsync();
        var job = f.Job();
        var report = new SuzUtilisationReport { JobId = job.Id, Number = 1, CodeCount = 1, State = "Tracking", ReportId = Guid.NewGuid().ToString(), NextCheckAtUnixMs = 1, TrackingUntilUnixMs = 1000000 };
        await f.Repository.AddAsync(job, [new(report, "{}")], Ct);
        var stale = Assert.Single(await f.Repository.ClaimDueAsync(2, 1, Ct));
        var fresh = Assert.Single(await f.Repository.ClaimDueAsync(120003, 1, Ct));
        fresh.State = "Finished"; fresh.ReceiptState = "SUCCESS";
        Assert.True(await f.Repository.CompleteCheckAsync(fresh, fresh.TrackingOwner!.Value, Ct));
        stale.State = "Paused";
        Assert.False(await f.Repository.CompleteCheckAsync(stale, stale.TrackingOwner!.Value, Ct));
        Assert.False(await f.Repository.ScheduleAsync(Guid.NewGuid(), CrptIntegrationMode.Mock, report.Id, 8, true, Ct));
        Assert.Equal("Finished", (await f.Repository.GetAsync(f.Profile.Id, CrptIntegrationMode.Mock, job.Id, Ct))!.Reports[0].State);
    }
    [Fact]
    public async Task TrackingLeaseRenewalExcludesOthersAndRejectsExpiredOrReplacedOwner()
    {
        await using var f = await Fixture.CreateAsync();
        var job = f.Job();
        var report = new SuzUtilisationReport { JobId = job.Id, Number = 1, CodeCount = 1, State = "Tracking", ReportId = Guid.NewGuid().ToString(), NextCheckAtUnixMs = 1, TrackingUntilUnixMs = 1000000 };
        await f.Repository.AddAsync(job, [new(report, "{}")], Ct);
        var claimed = Assert.Single(await f.Repository.ClaimDueAsync(2, 1, Ct));
        var owner = claimed.TrackingOwner!.Value;
        Assert.True(await f.Repository.RenewCheckLeaseAsync(report.Id, owner, 90002, 210002, Ct));
        Assert.Empty(await f.Repository.ClaimDueAsync(120003, 1, Ct));
        Assert.False(await f.Repository.RenewCheckLeaseAsync(report.Id, Guid.NewGuid(), 100002, 220002, Ct));
        Assert.False(await f.Repository.RenewCheckLeaseAsync(report.Id, owner, 210002, 330002, Ct));
        var replacement = Assert.Single(await f.Repository.ClaimDueAsync(210003, 1, Ct));
        Assert.False(await f.Repository.RenewCheckLeaseAsync(report.Id, owner, 210004, 330004, Ct));
        Assert.False(await f.Repository.CompleteCheckAsync(claimed, owner, Ct));
        Assert.True(await f.Repository.CompleteCheckAsync(replacement, replacement.TrackingOwner!.Value, Ct));
    }

    [Fact]
    public async Task EqualTimestampPagesStableAtHundred()
    {
        await using var f = await Fixture.CreateAsync();
        for (var i = 0; i < 101; i++) await f.Repository.AddAsync(f.Job(), [], Ct);
        var first = await f.Repository.ListPageAsync(f.Profile.Id, CrptIntegrationMode.Mock, cancellationToken: Ct);
        Assert.Equal(100, first.Items.Count); Assert.NotNull(first.NextCursor);
        var second = await f.Repository.ListPageAsync(f.Profile.Id, CrptIntegrationMode.Mock, first.NextCursor, Ct);
        Assert.Single(second.Items); Assert.DoesNotContain(second.Items[0].Id, first.Items.Select(x => x.Id));
        Assert.Null(second.NextCursor);
    }
    [Fact]
    public async Task BusinessPlaceFieldsSurviveRepositoryReopenAndStayProfileScoped()
    {
        await using var f = await Fixture.CreateAsync();
        f.Profile.Kpp = "123456789";
        f.Profile.FiasId = Guid.NewGuid().ToString();
        await f.Profiles.UpsertAsync(f.Profile, Ct);
        var another = new CrptProfile { Name = "Другой" };
        await f.Profiles.UpsertAsync(another, Ct);
        var reopened = new ProfileRepository(f.Factory);
        var saved = await reopened.GetAsync(f.Profile.Id, Ct);
        Assert.Equal("123456789", saved!.Kpp);
        Assert.Equal(f.Profile.FiasId, saved.FiasId);
        Assert.Null((await reopened.GetAsync(another.Id, Ct))!.Kpp);
    }
    [Fact]
    public async Task BusinessPlaceMigrationPreservesExistingProfileAndAddsNullableFields()
    {
        var cs = $"Data Source=file:suz-profile-upgrade-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(cs);
        await anchor.OpenAsync(Ct);
        var factory = new PooledDbContextFactory<WorkbenchDbContext>(
            new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite(cs).Options);
        var profileId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261002230000_AddSuzUtilisation", Ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Profiles (Id, Name, Inn) VALUES ({profileId}, {"Старый профиль"}, {"7700000000"})", Ct);
            await db.Database.MigrateAsync(Ct);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        var profiles = new ProfileRepository(factory);
        var saved = await profiles.GetAsync(profileId, Ct);
        Assert.NotNull(saved);
        Assert.Equal("Старый профиль", saved.Name);
        Assert.Equal("7700000000", saved.Inn);
        Assert.Null(saved.Kpp);
        Assert.Null(saved.FiasId);
        saved.Kpp = "123456789";
        saved.FiasId = "00000000-0000-0000-0000-000000000001";
        await profiles.UpsertAsync(saved, Ct);
        var reopened = new ProfileRepository(factory);
        Assert.Equal("123456789", (await reopened.GetAsync(profileId, Ct))!.Kpp);
        Assert.Equal(saved.FiasId, (await reopened.GetAsync(profileId, Ct))!.FiasId);
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection anchor;
        public PooledDbContextFactory<WorkbenchDbContext> Factory { get; }
        public SuzUtilisationRepository Repository { get; }
        public CrptProfile Profile { get; } = new() { Name = "СУЗ тест", Inn = "7700000000" };
        public ProfileRepository Profiles { get; }
        public ApiRequestLogRepository Logs { get; }
        private Fixture(SqliteConnection anchor, PooledDbContextFactory<WorkbenchDbContext> factory)
        { this.anchor = anchor; Factory = factory; Repository = new(factory, new EphemeralDataProtectionProvider()); Profiles = new(factory); Logs = new(factory); }
        public static async Task<Fixture> CreateAsync()
        {
            var cs = $"Data Source=file:suz-utilisation-{Guid.NewGuid():N}?mode=memory&cache=shared";
            var anchor = new SqliteConnection(cs); await anchor.OpenAsync(Ct);
            var factory = new PooledDbContextFactory<WorkbenchDbContext>(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite(cs).Options);
            await using (var db = await factory.CreateDbContextAsync(Ct)) { await db.Database.MigrateAsync(Ct); }
            var f = new Fixture(anchor, factory);
            await f.Profiles.UpsertAsync(f.Profile, Ct);
            Assert.NotNull(await f.Profiles.GetAsync(f.Profile.Id, Ct));
            return f;
        }
        public SuzUtilisationJob Job() => new() { ProfileId = Profile.Id, Mode = CrptIntegrationMode.Mock, ProductGroup = "milk", PackageType = "UNIT", Scenario = "STANDARD", CreatedAtUnixMs = 1 };
        public ValueTask DisposeAsync() => anchor.DisposeAsync();
    }
}
