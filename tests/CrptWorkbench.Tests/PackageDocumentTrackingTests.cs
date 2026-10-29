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
        var history = Assert.Single(await f.Logs.ListAsync(f.Profile.Id, 10, Ct));
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

    [Fact]
    public async Task IndependentChecksOverlapWithFourWorkersAndTenRecordPassLimit()
    {
        await using var f = await Fixture.CreateAsync();
        for (var i = 0; i < 12; i++)
        {
            var document = f.Document(); document.Id = Guid.NewGuid();
            await f.Repository.AddAsync(document, Ct);
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        f.Client.BeforeResponse = async token =>
        {
            var count = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximum); }
            while (count > previous && Interlocked.CompareExchange(ref maximum, count, previous) != previous);
            if (count == 4) started.TrySetResult();
            try { await release.Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref active); }
        };
        var polling = f.Tracker.PollDueAsync(Ct);
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); }
        finally { release.TrySetResult(); await polling; }
        Assert.Equal(4, maximum);
        Assert.Equal(10, f.Client.Calls);
        Assert.Equal(10, (await f.Repository.ListAsync(f.Profile.Id, PackageOperationKind.Aggregation, Ct)).Count(x => x.PollCount == 1));
    }

    [Fact]
    public async Task LeaseRenewalExcludesOtherWorkersAndRejectsExpiredOrReplacedTokens()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Repository.AddAsync(f.Document(), Ct);
        var now = f.Clock.Now.ToUnixTimeMilliseconds();
        var claimed = Assert.Single(await f.Repository.ClaimDueAsync(now, 1, Ct));
        var originalLease = claimed.LeaseUntilUnixMs!.Value;
        var renewedLease = now + 210_000;
        Assert.True(await f.Repository.RenewCheckLeaseAsync(claimed.Id, originalLease, now + 90_000, renewedLease, Ct));
        Assert.Empty(await f.Repository.ClaimDueAsync(now + 120_001, 1, Ct));
        Assert.False(await f.Repository.RenewCheckLeaseAsync(claimed.Id, originalLease, now + 100_000, now + 220_000, Ct));
        Assert.False(await f.Repository.CompleteCheckAsync(claimed, originalLease, Ct));
        Assert.False(await f.Repository.RenewCheckLeaseAsync(claimed.Id, renewedLease, renewedLease, renewedLease + 120_000, Ct));
        var replacement = Assert.Single(await f.Repository.ClaimDueAsync(renewedLease + 1, 1, Ct));
        Assert.False(await f.Repository.RenewCheckLeaseAsync(claimed.Id, renewedLease, renewedLease + 2, renewedLease + 120_000, Ct));
        Assert.True(await f.Repository.CompleteCheckAsync(replacement, replacement.LeaseUntilUnixMs!.Value, Ct));
    }

    [Fact]
    public async Task WaitingRequestRenewsLeaseAndCannotBeClaimedAfterOriginalExpiry()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Repository.AddAsync(f.Document(), Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.BeforeResponse = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var polling = f.Tracker.PollDueAsync(Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            f.Clock.Now = f.Start.AddSeconds(90);
            f.Clock.Pulse();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var renewed = f.Start.AddSeconds(210).ToUnixTimeMilliseconds();
            while ((await f.Repository.GetAsync(f.Id, deadline.Token))!.LeaseUntilUnixMs != renewed)
                await Task.Delay(10, deadline.Token);
            f.Clock.Now = f.Start.AddSeconds(121);
            Assert.Empty(await f.Repository.ClaimDueAsync(f.Clock.Now.ToUnixTimeMilliseconds(), 1, Ct));
        }
        finally { release.TrySetResult(); await polling; }
        var saved = (await f.Repository.GetAsync(f.Id, Ct))!;
        Assert.Equal(1, saved.PollCount);
        Assert.Null(saved.LeaseUntilUnixMs);
        Assert.Single(await f.Logs.ListAsync(f.Profile.Id, 10, Ct));
    }

    [Fact]
    public async Task LostLeaseCancelsWaitingRequestAndPublishesNeitherStateNorHistory()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Repository.AddAsync(f.Document(), Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.BeforeResponse = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        var polling = f.Tracker.PollDueAsync(Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        f.Clock.Now = f.Start.AddSeconds(121);
        var replacement = Assert.Single(await f.Repository.ClaimDueAsync(f.Clock.Now.ToUnixTimeMilliseconds(), 1, Ct));
        replacement.State = "Finished";
        replacement.RemoteStatus = "CHECKED_OK";
        Assert.True(await f.Repository.CompleteCheckAsync(replacement, replacement.LeaseUntilUnixMs!.Value, Ct));
        f.Clock.Pulse();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polling.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        var saved = (await f.Repository.GetAsync(f.Id, Ct))!;
        Assert.Equal("Finished", saved.State);
        Assert.Equal("CHECKED_OK", saved.RemoteStatus);
        Assert.Empty(await f.Logs.ListAsync(f.Profile.Id, 10, Ct));
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
        private readonly List<PulseTimer> _timers = [];
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new PulseTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }
        public void Pulse() { foreach (var timer in _timers.ToArray()) timer.Pulse(); }
        private sealed class PulseTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Pulse() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
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
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Func<CancellationToken, Task>? BeforeResponse { get; set; }
        public string Body { get; set; } = "[{\"status\":\"IN_PROGRESS\",\"errors\":[]}]";
        public IntegrationRequest? LastRequest { get; private set; }
        public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls); LastRequest = request;
            if (BeforeResponse is not null) await BeforeResponse(cancellationToken);
            var headers = request.Headers.ToDictionary(x => x.Key, x => x.Value);
            headers["Authorization"] = "Bearer private-token";
            return new IntegrationExchange(200, request.Method, request.Url, headers, request.Body,
                new Dictionary<string, string> { ["X-CRPT-Workbench-Mock"] = "true" }, Body, TimeSpan.FromMilliseconds(5));
        }
    }
}
