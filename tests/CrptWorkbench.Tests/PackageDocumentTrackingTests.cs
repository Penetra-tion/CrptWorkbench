using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class PackageDocumentTrackingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AdditiveMigrationPreservesTransformationGuidAndTrackingFields()
    {
        await using var f = await Fixture.CreateAsync(migrate: false);
        await using (var db = await f.Factory.CreateDbContextAsync(Ct))
        {
            await db.GetService<IMigrator>().MigrateAsync("20260925230000_AddPackageTransformationDocuments", Ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO PackageTransformationDocuments
                (Id, ProfileId, Mode, ProductGroup, Action, PackageCode, ItemCount, DocumentHash, State,
                 DocumentId, CreatedAtUnixMs, SubmittedAtUnixMs, NextCheckAtUnixMs, TrackingUntilUnixMs, PollCount)
                VALUES ({f.Id}, {f.Profile.Id}, 0, 'milk', 'ADDING', '000000000000000001', 2, 'HASH', 'Tracking',
                 '00000000-0000-0000-0000-000000000009', 1, 2, 3, 86400002, 7)
                """, Ct);
            await db.Database.MigrateAsync(Ct);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        var record = await f.Repository.GetAsync(f.Id, Ct);
        Assert.NotNull(record);
        Assert.Equal(PackageOperationKind.Transformation, record.OperationKind);
        Assert.Null(record.PackageType);
        Assert.Equal("00000000-0000-0000-0000-000000000009", record.DocumentId);
        Assert.Equal("Tracking", record.State);
        Assert.Equal(7, record.PollCount);
        Assert.Equal(3, record.NextCheckAtUnixMs);
        Assert.Equal(86400002, record.TrackingUntilUnixMs);
        Assert.Single(await f.Repository.ListAsync(f.Profile.Id, PackageOperationKind.Transformation, Ct));
        Assert.Empty(await f.Repository.ListAsync(f.Profile.Id, PackageOperationKind.Aggregation, Ct));
    }

    [Fact]
    public async Task TrackerUsesBackoffThenPausesAndCanResumeForAnotherDay()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Repository.AddAsync(f.Document(), Ct);
        foreach (var delay in new[] { 15, 30, 60 })
        {
            await f.Tracker.PollDueAsync(Ct);
            var record = (await f.Repository.GetAsync(f.Id, Ct))!;
            Assert.Equal(f.Clock.Now.AddSeconds(delay).ToUnixTimeMilliseconds(), record.NextCheckAtUnixMs);
            f.Clock.Now = DateTimeOffset.FromUnixTimeMilliseconds(record.NextCheckAtUnixMs!.Value);
        }
        f.Clock.Now = f.Start.AddHours(2);
        await f.Tracker.PollDueAsync(Ct);
        Assert.Equal(f.Clock.Now.AddMinutes(5).ToUnixTimeMilliseconds(), (await f.Repository.GetAsync(f.Id, Ct))!.NextCheckAtUnixMs);
        var calls = f.Client.Calls;
        f.Clock.Now = f.Start.AddHours(24);
        await f.Tracker.PollDueAsync(Ct);
        var paused = (await f.Repository.GetAsync(f.Id, Ct))!;
        Assert.Equal("Paused", paused.State);
        Assert.Null(paused.NextCheckAtUnixMs);
        Assert.Equal(calls, f.Client.Calls);
        await f.Service.ResumeAsync(f.Profile.Id, f.Id, PackageOperationKind.Aggregation, Ct);
        var resumed = (await f.Repository.GetAsync(f.Id, Ct))!;
        Assert.Equal("Tracking", resumed.State);
        Assert.Equal(f.Clock.Now.AddHours(24).ToUnixTimeMilliseconds(), resumed.TrackingUntilUnixMs);
        Assert.Equal(f.Clock.Now.ToUnixTimeMilliseconds(), resumed.NextCheckAtUnixMs);
    }

    [Fact]
    public async Task TerminalFailureStopsPollingAndPersistsMaskedHistory()
    {
        await using var f = await Fixture.CreateAsync();
        f.Client.Body = "[{\"status\":\"CHECKED_NOT_OK\",\"errors\":[\"Состав упаковки отклонён\"]}]";
        await f.Repository.AddAsync(f.Document(), Ct);
        await f.Tracker.PollDueAsync(Ct);
        var record = (await new PackageDocumentRepository(f.Factory).GetAsync(f.Id, Ct))!;
        Assert.Equal("Finished", record.State);
        Assert.Equal("CHECKED_NOT_OK", record.RemoteStatus);
        Assert.Contains("Состав", record.Error);
        Assert.Null(record.NextCheckAtUnixMs);
        await f.Tracker.PollDueAsync(Ct);
        Assert.Equal(1, f.Client.Calls);
        var history = Assert.Single(await f.Logs.ListAsync(10, Ct));
        Assert.DoesNotContain("private-token", history.RequestHeaders);
        Assert.Contains("Bearer ********", history.RequestHeaders);
        Assert.Equal(f.Client.LastRequest!.CorrelationId, history.CorrelationId);
        Assert.Contains("X-CRPT-Workbench-Mock", history.ResponseHeaders);
    }

    [Fact]
    public async Task ChangedModePausesWithoutSendingAnyStatusRequest()
    {
        await using var f = await Fixture.CreateAsync();
        var document = f.Document();
        document.Mode = CrptIntegrationMode.Production;
        await f.Repository.AddAsync(document, Ct);
        await f.Tracker.PollDueAsync(Ct);
        Assert.Equal(0, f.Client.Calls);
        var paused = (await f.Repository.GetAsync(f.Id, Ct))!;
        Assert.Equal("Paused", paused.State);
        Assert.Contains("Режим", paused.Error);
        Assert.Null(paused.NextCheckAtUnixMs);
    }

    [Fact]
    public async Task ManualControlsAreAtomicScopedAndPreserveAnotherWorkersLease()
    {
        await using var f = await Fixture.CreateAsync();
        var record = f.Document();
        record.LeaseUntilUnixMs = f.Clock.Now.AddMinutes(2).ToUnixTimeMilliseconds();
        record.NextCheckAtUnixMs = f.Clock.Now.AddMinutes(5).ToUnixTimeMilliseconds();
        await f.Repository.AddAsync(record, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CheckNowAsync(Guid.NewGuid(), f.Id, PackageOperationKind.Aggregation, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CheckNowAsync(f.Profile.Id, f.Id, PackageOperationKind.Transformation, Ct));
        await f.Service.CheckNowAsync(f.Profile.Id, f.Id, PackageOperationKind.Aggregation, Ct);
        var updated = (await f.Repository.GetAsync(f.Id, Ct))!;
        Assert.Equal(record.LeaseUntilUnixMs, updated.LeaseUntilUnixMs);
        Assert.Equal(f.Clock.Now.ToUnixTimeMilliseconds(), updated.NextCheckAtUnixMs);
        Assert.Empty(await f.Repository.ClaimDueAsync(f.Clock.Now.ToUnixTimeMilliseconds(), 10, Ct));
        Assert.Single(await new PackageDocumentRepository(f.Factory).ClaimDueAsync(f.Clock.Now.AddMinutes(3).ToUnixTimeMilliseconds(), 10, Ct));
        updated.State = "Finished";
        await f.Repository.UpdateAsync(updated, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CheckNowAsync(f.Profile.Id, f.Id, PackageOperationKind.Aggregation, Ct));
        Assert.Equal("Finished", (await f.Repository.GetAsync(f.Id, Ct))!.State);
    }

    [Fact]
    public async Task ExpiredWorkerCannotOverwriteANewerTerminalResult()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Repository.AddAsync(f.Document(), Ct);
        var now = f.Clock.Now.ToUnixTimeMilliseconds();
        var old = Assert.Single(await f.Repository.ClaimDueAsync(now, 1, Ct));
        var oldLease = old.LeaseUntilUnixMs!.Value;
        var recent = Assert.Single(await f.Repository.ClaimDueAsync(now + 120_001, 1, Ct));
        recent.State = "Finished";
        recent.RemoteStatus = "CHECKED_OK";
        recent.NextCheckAtUnixMs = null;
        Assert.True(await f.Repository.CompleteCheckAsync(recent, recent.LeaseUntilUnixMs!.Value, Ct));
        old.RemoteStatus = "IN_PROGRESS";
        old.NextCheckAtUnixMs = now + 300_000;
        Assert.False(await f.Repository.CompleteCheckAsync(old, oldLease, Ct));
        var saved = (await f.Repository.GetAsync(f.Id, Ct))!;
        Assert.Equal("Finished", saved.State);
        Assert.Equal("CHECKED_OK", saved.RemoteStatus);
        Assert.Null(saved.NextCheckAtUnixMs);
        Assert.Empty(await f.Repository.ClaimDueAsync(now + 600_000, 1, Ct));
    }

    [Fact]
    public async Task RestartNeverClaimsUncertainCreationAndBothKindsKeepTheirOwnLists()
    {
        await using var f = await Fixture.CreateAsync();
        foreach (var state in new[] { "Sending", "Unknown", "Prepared", "Rejected" })
        {
            var doc = f.Document();
            doc.Id = Guid.NewGuid(); doc.State = state; doc.DocumentId = null;
            await f.Repository.AddAsync(doc, Ct);
        }
        var transformation = f.Document();
        transformation.OperationKind = PackageOperationKind.Transformation;
        transformation.PackageType = null;
        await f.Repository.AddAsync(transformation, Ct);
        var reopened = new PackageDocumentRepository(f.Factory);
        Assert.Equal(4, (await reopened.ListAsync(f.Profile.Id, PackageOperationKind.Aggregation, Ct)).Count);
        Assert.Single(await reopened.ListAsync(f.Profile.Id, PackageOperationKind.Transformation, Ct));
        var claimed = Assert.Single(await reopened.ClaimDueAsync(f.Clock.Now.ToUnixTimeMilliseconds(), 10, Ct));
        Assert.Equal(transformation.Id, claimed.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        public PooledDbContextFactory<WorkbenchDbContext> Factory { get; }
        public PackageDocumentRepository Repository { get; }
        public ApiRequestLogRepository Logs { get; }
        public CrptProfile Profile { get; } = new() { Inn = "7700000000" };
        public Guid Id { get; } = Guid.NewGuid();
        public DateTimeOffset Start { get; } = DateTimeOffset.Parse("2026-09-27T00:00:00Z");
        public FixedClock Clock { get; }
        public StatusClient Client { get; } = new();
        public PackageDocumentTracker Tracker { get; }
        public PackageDocumentService Service { get; }

        private Fixture(SqliteConnection anchor, PooledDbContextFactory<WorkbenchDbContext> factory)
        {
            _anchor = anchor; Factory = factory; Repository = new(factory); Logs = new(factory); Clock = new(Start);
            var history = new PackageDocumentHistory(Logs, new SensitiveDataMasker(), Clock);
            Tracker = new(Repository, new Profiles(Profile), Client, Clock, history, new SensitiveDataMasker());
            Service = new(Repository, new Profiles(Profile), Client, new NoSigner(), Clock, history);
        }

        public static async Task<Fixture> CreateAsync(bool migrate = true)
        {
            var cs = $"Data Source=file:package-tracking-{Guid.NewGuid():N}?mode=memory&cache=shared";
            var anchor = new SqliteConnection(cs);
            await anchor.OpenAsync(Ct);
            var factory = new PooledDbContextFactory<WorkbenchDbContext>(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite(cs).Options);
            if (migrate) await DatabaseInitializer.InitializeAsync(factory, Ct);
            return new(anchor, factory);
        }

        public PackageDocument Document() => new()
        {
            Id = Id, ProfileId = Profile.Id, Mode = CrptIntegrationMode.Mock, OperationKind = PackageOperationKind.Aggregation,
            PackageType = "BOX", ProductGroup = "milk", PackageCode = "000000000000000001", ItemCount = 1,
            State = "Tracking", DocumentId = Guid.NewGuid().ToString(), SubmittedAtUnixMs = Start.ToUnixTimeMilliseconds(),
            TrackingUntilUnixMs = Start.AddHours(24).ToUnixTimeMilliseconds(), NextCheckAtUnixMs = Start.ToUnixTimeMilliseconds()
        };
        public ValueTask DisposeAsync() => _anchor.DisposeAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Profiles(CrptProfile profile) : IProfileRepository
    {
        public Task<IReadOnlyList<CrptProfile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CrptProfile>>([profile]);
        public Task<CrptProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<CrptProfile?>(id == profile.Id ? profile : null);
        public Task UpsertAsync(CrptProfile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class NoSigner : ICertificateSignatureService
    {
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class StatusClient : ITrueApiClient
    {
        public Uri BaseAddress { get; } = new("https://trueapi.mock.invalid");
        public int Calls { get; private set; }
        public string Body { get; set; } = "[{\"status\":\"IN_PROGRESS\",\"errors\":[]}]";
        public IntegrationRequest? LastRequest { get; private set; }
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; LastRequest = request;
            var headers = request.Headers.ToDictionary(x => x.Key, x => x.Value);
            headers["Authorization"] = "Bearer private-token";
            return Task.FromResult(new IntegrationExchange(200, request.Method, request.Url, headers, request.Body,
                new Dictionary<string, string> { ["X-CRPT-Workbench-Mock"] = "true" }, Body, TimeSpan.FromMilliseconds(5)));
        }
    }
}
