using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class BulkCodePauseTests
{
    private static readonly Guid DemoProfile = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherProfile = Guid.NewGuid();
    private static readonly CrptIntegrationOptions MockOptions = new() { Mode = CrptIntegrationMode.Mock };
    private static string Code(int number) => $"010460000000000021{number:D12}";

    [Fact]
    public async Task PauseDrainsRequestsAndResumeDoesNotRepeatSavedBatches()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 5000).Select(Code)));
        await using var source = new MemoryStream(bytes);
        var job = await fixture.Service.UploadAsync(DemoProfile, "pause.txt", bytes.Length, source, ct);
        var client = new FakeClient();
        client.OnRequest = async () =>
        {
            await fixture.Service.PauseAsync(DemoProfile, job.Id, ct);
            Assert.Equal("PauseRequested", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        };
        await fixture.Processor(client).ProcessNextAsync(ct);
        var paused = (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!;
        Assert.Equal("Paused", paused.State);
        Assert.InRange(client.Requests.Count, 1, 4);
        Assert.Equal(client.Requests.Count * 1000, paused.CodesCompleted);
        await fixture.Service.PauseAsync(DemoProfile, job.Id, ct);
        Assert.Null(await fixture.Jobs.NextRunnableAsync(CrptIntegrationMode.Mock, ct));
        client.OnRequest = null;
        await fixture.Service.ResumeAsync(DemoProfile, job.Id, ct);
        await fixture.Processor(client).ProcessNextAsync(ct);
        var completed = (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!;
        Assert.Equal("Completed", completed.State);
        Assert.Equal(5000, completed.CodesCompleted);
        Assert.Equal(5, client.Requests.Count);
    }

    [Fact]
    public async Task QueuedPauseSurvivesRestartAndIsProfileAndModeScoped()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes(Code(1));
        await using var source = new MemoryStream(bytes);
        var job = await fixture.Service.UploadAsync(DemoProfile, "pause.txt", bytes.Length, source, ct);
        await fixture.Service.PauseAsync(OtherProfile, job.Id, ct);
        var production = new BulkCodeFileService(fixture.Jobs, fixture.Files, new ProfileRepository(fixture.Factory), new() { Mode = CrptIntegrationMode.Production });
        await production.PauseAsync(DemoProfile, job.Id, ct);
        Assert.Equal("Queued", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        await fixture.Service.PauseAsync(DemoProfile, job.Id, ct);
        var client = new FakeClient();
        await fixture.Processor(client).ProcessNextAsync(ct);
        Assert.Equal("Paused", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        var restarted = new BulkCodeJobRepository(fixture.Factory);
        Assert.Null(await restarted.NextRunnableAsync(CrptIntegrationMode.Mock, ct));
        await production.ResumeAsync(DemoProfile, job.Id, ct);
        await fixture.Service.ResumeAsync(OtherProfile, job.Id, ct);
        Assert.Equal("Paused", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        await fixture.Service.CancelAsync(DemoProfile, job.Id, ct);
        await fixture.Processor(client).ProcessNextAsync(ct);
        Assert.Equal("Canceled", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        Assert.Empty(client.Requests);
        await fixture.Service.DeleteAsync(DemoProfile, job.Id, ct);
        Assert.Null(await fixture.Service.GetAsync(DemoProfile, job.Id, ct));
    }
    [Fact]
    public async Task PreparationPreservesPauseAndOtherJobsKeepRunning()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes(Code(1));
        await using var source = new MemoryStream(bytes);
        var paused = await fixture.Service.UploadAsync(DemoProfile, "pause.txt", bytes.Length, source, ct);
        await fixture.Service.PauseAsync(DemoProfile, paused.Id, ct);
        await fixture.Jobs.AddBatchAsync(new BulkCodeBatch
        {
            JobId = paused.Id, Number = 0, StartOffset = 0, EndOffset = bytes.Length, CodeCount = 1
        }, 1, ct);
        Assert.Equal("PauseRequested", (await fixture.Service.GetAsync(DemoProfile, paused.Id, ct))!.State);
        var client = new FakeClient();
        await fixture.Processor(client).ProcessNextAsync(ct);
        Assert.Equal("Paused", (await fixture.Service.GetAsync(DemoProfile, paused.Id, ct))!.State);
        await using var otherSource = new MemoryStream(bytes);
        var other = await fixture.Service.UploadAsync(DemoProfile, "other.txt", bytes.Length, otherSource, ct);
        await fixture.Processor(client).ProcessNextAsync(ct);
        Assert.Equal("Completed", (await fixture.Service.GetAsync(DemoProfile, other.Id, ct))!.State);
        Assert.Single(client.Requests);
        await fixture.Service.DeleteAsync(DemoProfile, paused.Id, ct);
        Assert.Null(await fixture.Service.GetAsync(DemoProfile, paused.Id, ct));
    }

    [Fact]
    public async Task PreparedJobKeepsDuplicateRowsAndRejectionsAcrossPause()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var rows = Enumerable.Repeat(Code(1), 2500).Prepend("bad#").Append(Code(2)).ToArray();
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', rows));
        await using var source = new MemoryStream(bytes);
        var job = await fixture.Service.PrepareAsync(DemoProfile, bytes.Length, source, ct);
        Assert.True(await fixture.Service.StartReadyAsync(DemoProfile, job.Id, ct));
        var client = new FakeClient { OnRequest = () => fixture.Service.PauseAsync(DemoProfile, job.Id, ct) };
        await fixture.Processor(client).ProcessNextAsync(ct);
        Assert.Equal("Paused", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        Assert.Contains("Строка 1:", Assert.Single((await fixture.Service.RejectedPageAsync(DemoProfile, job.Id, cancellationToken: ct)).Items));
        client.OnRequest = null;
        await fixture.Service.ResumeAsync(DemoProfile, job.Id, ct);
        await fixture.Service.ResumeAsync(DemoProfile, job.Id, ct);
        await fixture.Processor(client).ProcessNextAsync(ct);
        var completed = (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!;
        Assert.Equal("Completed", completed.State);
        Assert.Equal(2502, completed.LinesRead);
        Assert.Equal(2501, completed.CodesCompleted);
        Assert.Equal(3, client.Requests.Count);
        var exported = new List<string>();
        await foreach (var code in fixture.Service.ExportAsync(DemoProfile, job.Id, new HashSet<string>(), ct)) exported.Add(code);
        Assert.Equal(rows.Skip(1), exported);
        Assert.Single((await fixture.Service.RejectedPageAsync(DemoProfile, job.Id, cancellationToken: ct)).Items);
    }

    [Fact]
    public async Task InterruptedDispatchAndPublishedFragmentAreRecoveredAfterPausedRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes(Code(1));
        await using var source = new MemoryStream(bytes);
        var job = await fixture.Service.UploadAsync(DemoProfile, "restart.txt", bytes.Length, source, ct);
        await fixture.Jobs.AddBatchAsync(new BulkCodeBatch
        {
            JobId = job.Id, Number = 0, StartOffset = 0, EndOffset = bytes.Length, CodeCount = 1
        }, 1, ct);
        Assert.True(await fixture.Jobs.TryBeginBatchAsync(DemoProfile, job.Id, CrptIntegrationMode.Mock, 0, ct));
        await fixture.Files.WriteFragmentAsync(job.Id, 0, [("INTRODUCED", Code(1))], 1, 0, ct);
        await fixture.Service.PauseAsync(DemoProfile, job.Id, ct);
        var client = new FakeClient();
        await fixture.Processor(client).ProcessNextAsync(ct);
        Assert.Equal("Paused", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        await fixture.Service.ResumeAsync(DemoProfile, job.Id, ct);
        await fixture.Processor(client).ProcessNextAsync(ct);
        var completed = (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!;
        Assert.Equal("Completed", completed.State);
        Assert.Equal(1, completed.CodesCompleted);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task PauseStopsRateLimitRetryAndResumeRetriesUnfinishedBatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes(Code(1));
        await using var source = new MemoryStream(bytes);
        var job = await fixture.Service.UploadAsync(DemoProfile, "rate-limit.txt", bytes.Length, source, ct);
        var client = new FakeClient { RateLimitFirst = true, OnRequest = () => fixture.Service.PauseAsync(DemoProfile, job.Id, ct) };
        await fixture.Processor(client).ProcessNextAsync(ct);
        Assert.Equal("Paused", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        Assert.Single(client.Requests);
        Assert.Equal(0, (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.CodesCompleted);
        client.OnRequest = null;
        await fixture.Service.ResumeAsync(DemoProfile, job.Id, ct);
        await fixture.Processor(client).ProcessNextAsync(ct);
        Assert.Equal("Completed", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        Assert.Equal(1, (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.CodesCompleted);
        Assert.Equal(2, client.Requests.Count);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Deleting")]
    public async Task DeleteWhileExportIsOpenRemovesJobAndAllowsInterruptedDeletionRetry(string state)
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes(Code(1));
        await using var source = new MemoryStream(bytes);
        var job = await fixture.Service.UploadAsync(DemoProfile, "delete.txt", bytes.Length, source, ct);
        await fixture.Files.WriteOwnedFragmentAsync(job.Id, 0, [new("INTRODUCED", Code(1), "7800000000", true)], 1, 0, ct);
        await fixture.Jobs.SetJobStateAsync(DemoProfile, job.Id, state, null, ct);
        await using var export = fixture.Files.ReadCodesAsync(job.Id, 1, new HashSet<string>(), ct).GetAsyncEnumerator(ct);
        Assert.True(await export.MoveNextAsync());
        await fixture.Service.DeleteAsync(DemoProfile, job.Id, ct);
        Assert.Null(await fixture.Service.GetAsync(DemoProfile, job.Id, ct));
        Assert.False(fixture.Files.FragmentExists(job.Id, 0));
        await fixture.Service.DeleteAsync(DemoProfile, job.Id, ct);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task FinalAuthorizationFailurePausesWithoutFailingRemainingBatchesAndResumeRetriesThem(int status)
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 12000).Select(Code)));
        await using var source = new MemoryStream(bytes);
        var job = await fixture.Service.UploadAsync(DemoProfile, "auth.txt", bytes.Length, source, ct);
        var client = new FakeClient { ForcedStatus = status };
        await fixture.Processor(client).ProcessNextAsync(ct);
        var paused = (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!;
        Assert.Equal("Paused", paused.State);
        Assert.Equal(0, paused.FailedBatches);
        Assert.Equal(0, paused.CodesCompleted);
        Assert.InRange(client.Requests.Count, 1, 4);
        Assert.Contains(status.ToString(), paused.Error);
        client.ForcedStatus = 0;
        await fixture.Service.ResumeAsync(DemoProfile, job.Id, ct);
        await fixture.Processor(client).ProcessNextAsync(ct);
        var completed = (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!;
        Assert.Equal("Completed", completed.State);
        Assert.Equal(12000, completed.CodesCompleted);
        Assert.Equal(0, completed.FailedBatches);
    }

    [Fact]
    public async Task SlowFirstBatchDoesNotBlockFollowingIndependentBatches()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 6000).Select(Code)));
        await using var source = new MemoryStream(bytes);
        var job = await fixture.Service.UploadAsync(DemoProfile, "rolling.txt", bytes.Length, source, ct);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fifth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            RequestGate = async (request, token) =>
            {
                var first = JsonSerializer.Deserialize<string[]>(request.Body!)![0];
                if (first == Code(1)) await release.Task.WaitAsync(token);
                if (first == Code(4001)) fifth.TrySetResult();
            }
        };
        var processing = fixture.Processor(client).ProcessNextAsync(ct);
        try { await fifth.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        finally { release.TrySetResult(); await processing; }
        Assert.Equal("Completed", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        Assert.InRange(client.PeakConcurrent, 2, 4);
    }

    [Fact]
    public async Task PauseStopsAnUndispatchedBatchWaitingForServiceSlots()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var slots = new List<IDisposable>();
        var client = new WaitingMockClient();
        var bytes = Encoding.UTF8.GetBytes(Code(1));
        await using var source = new MemoryStream(bytes);
        var job = await fixture.Service.UploadAsync(DemoProfile, "waiting.txt", bytes.Length, source, ct);
        Task processing = Task.CompletedTask;
        try
        {
            for (var i = 0; i < 4; i++) slots.Add(await ApiRequestLimiter.Shared.AcquireAsync(ApiConcurrencyService.TrueApi, ct));
            processing = fixture.Processor(client).ProcessNextAsync(ct);
            await client.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await fixture.Service.PauseAsync(DemoProfile, job.Id, ct);
            await processing.WaitAsync(TimeSpan.FromSeconds(2), ct);
            var paused = (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!;
            Assert.Equal("Paused", paused.State);
            Assert.Equal(0, paused.CodesCompleted);
        }
        finally
        {
            foreach (var slot in slots) slot.Dispose();
            await processing;
        }
    }

    [Fact]
    public async Task UnrequestedPredispatchCancellationPausesInsteadOfResubmitting()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes(Code(1));
        await using var source = new MemoryStream(bytes);
        var job = await fixture.Service.UploadAsync(DemoProfile, "timeout.txt", bytes.Length, source, ct);
        var attempts = 0;
        var client = new FakeClient
        {
            OnRequest = () => Interlocked.Increment(ref attempts) == 1
                ? throw new ApiDispatchNotStartedException(new OperationCanceledException("Simulated predispatch timeout"))
                : Task.CompletedTask
        };
        await fixture.Processor(client).ProcessNextAsync(ct);
        var paused = (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!;
        Assert.Equal("Paused", paused.State);
        Assert.Equal(0, paused.CodesCompleted);
        Assert.Equal(0, paused.FailedBatches);
        Assert.Contains("вручную", paused.Error);
        Assert.Single(client.Requests);
        await fixture.Service.ResumeAsync(DemoProfile, job.Id, ct);
        await fixture.Processor(client).ProcessNextAsync(ct);
        Assert.Equal("Completed", (await fixture.Service.GetAsync(DemoProfile, job.Id, ct))!.State);
        Assert.Equal(2, client.Requests.Count);
    }

    private sealed class WaitingMockClient : ITrueApiClient
    {
        private readonly MockTrueApiClient _inner = new();
        public Uri BaseAddress => _inner.BaseAddress;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Waiting.TrySetResult();
            return _inner.ExecuteAsync(request, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        public PooledDbContextFactory<WorkbenchDbContext> Factory { get; }
        public BulkCodeJobRepository Jobs { get; }
        public BulkCodeFileStore Files { get; }
        public BulkCodeFileService Service { get; }

        private Fixture(string directory, PooledDbContextFactory<WorkbenchDbContext> factory)
        {
            _directory = directory;
            Factory = factory;
            Jobs = new(factory);
            Files = new(directory);
            Service = new(Jobs, Files, new ProfileRepository(factory), MockOptions);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "crpt-bulk-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var factory = new PooledDbContextFactory<WorkbenchDbContext>(new DbContextOptionsBuilder<WorkbenchDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "test.db")}").Options);
            await DatabaseInitializer.InitializeAsync(factory, TestContext.Current.CancellationToken);
            return new(directory, factory);
        }

        public BulkCodeJobProcessor Processor(ITrueApiClient client) =>
            new(Jobs, Files, new ProfileRepository(Factory), client, new ApiRequestLogRepository(Factory), MockOptions);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClient(bool failSecondOnce = false, string? ownerInn = null) : ITrueApiClient
    {
        public bool RateLimitFirst { get; init; }
        public int ForcedStatus { get; set; }
        public Func<IntegrationRequest, CancellationToken, Task>? RequestGate { get; init; }
        public Func<Task>? OnRequest { get; set; }
        private int _secondAttempts;
        private int _active;
        private int _peak;
        public int PeakConcurrent => _peak;
        public Uri BaseAddress { get; } = new("https://production.trueapi.mock.invalid");
        public ConcurrentBag<IntegrationRequest> Requests { get; } = [];
        public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            if (active > _peak) _peak = active;
            try
            {
                Requests.Add(request);
                if (RequestGate is { } gate) await gate(request, cancellationToken);
                if (OnRequest is { } callback) await callback();
                var codes = JsonSerializer.Deserialize<string[]>(request.Body!)!;
                var status = ForcedStatus != 0 ? ForcedStatus : RateLimitFirst && Requests.Count == 1 ? 429 :
                    failSecondOnce && codes.Length == 1 && Interlocked.Increment(ref _secondAttempts) == 1 ? 503 : 200;
                var body = JsonSerializer.Serialize(codes.Select(code => new CodeInfoItem(new CisInfo
                {
                    RequestedCis = code, Cis = code, Status = "INTRODUCED", OwnerInn = ownerInn
                })));
                await Task.Delay(2, cancellationToken);
                return new IntegrationExchange(status, request.Method, request.Url, request.Headers, request.Body,
                    new Dictionary<string, string>(), body, TimeSpan.Zero);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

}
