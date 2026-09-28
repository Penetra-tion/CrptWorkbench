using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class BulkCodeFileJobTests
{
    private static readonly Guid DemoProfile = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherProfile = Guid.NewGuid();
    private static readonly CrptIntegrationOptions MockOptions = new() { Mode = CrptIntegrationMode.Mock };
    private static string Code(int number) => $"010460000000000021{number:D12}";

    [Fact]
    public async Task LargeFileUploadStaysOutOfTheFormAndIsProfileScoped()
    {
        await using var fixture = await Fixture.CreateAsync();
        var line = Encoding.UTF8.GetBytes(Code(1) + "\n");
        const long size = 64_000_000;
        await using var source = new PatternStream(line, size);
        var job = await fixture.Service.UploadAsync(DemoProfile, "codes.txt", size, source, TestContext.Current.CancellationToken);

        Assert.True(job.FileBytes > 10_000_000);
        Assert.InRange(source.LargestRead, 1, 1024 * 1024);
        Assert.Equal(size, fixture.Files.SourceLength(job.Id));
        Assert.Null(await fixture.Service.GetAsync(OtherProfile, job.Id, TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Service.ListAsync(OtherProfile, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in fixture.Service.ExportAsync(OtherProfile, job.Id, new HashSet<string> { "INTRODUCED" }, TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task FailedBatchCanBeRetriedWithoutResendingCompletedBatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var codes = Enumerable.Range(1, 1001).Select(Code).ToArray();
        var payload = Encoding.UTF8.GetBytes(string.Join('\n', codes));
        await using var source = new MemoryStream(payload);
        var job = await fixture.Service.UploadAsync(DemoProfile, "batch.txt", payload.Length, source, TestContext.Current.CancellationToken);
        var client = new FakeClient(failSecondOnce: true);

        await fixture.Processor(client).ProcessNextAsync(TestContext.Current.CancellationToken);
        var partial = (await fixture.Service.GetAsync(DemoProfile, job.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal("Partial", partial.State);
        Assert.Equal(1000, partial.CodesCompleted);
        Assert.Equal(1, partial.FailedBatches);
        Assert.Equal(2, client.Requests.Count);

        await fixture.Service.RetryAsync(DemoProfile, job.Id, TestContext.Current.CancellationToken);
        await fixture.Processor(client).ProcessNextAsync(TestContext.Current.CancellationToken);
        var completed = (await fixture.Service.GetAsync(DemoProfile, job.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal("Completed", completed.State);
        Assert.Equal(1001, completed.CodesCompleted);
        Assert.Equal(3, client.Requests.Count);
        Assert.All(client.Requests, request => Assert.InRange(JsonSerializer.Deserialize<string[]>(request.Body!)!.Length, 1, 1000));
        Assert.All(client.Requests, request =>
        {
            Assert.Equal(TrueApiBulkCodeInfoHandler.OperationId, request.OperationId);
            Assert.EndsWith("/cises/info", request.Url);
            Assert.Equal(DemoProfile, request.Authentication?.ProfileId);
            Assert.Equal(request.CorrelationId, request.Headers["X-Correlation-Id"]);
        });
        var exported = new List<string>();
        await foreach (var code in fixture.Service.ExportAsync(DemoProfile, job.Id, new HashSet<string> { "INTRODUCED" }, TestContext.Current.CancellationToken)) exported.Add(code);
        Assert.Equal(codes, exported);

        var summary = await new ApiRequestLogRepository(fixture.Factory).GetAsync(DemoProfile, job.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.DoesNotContain(Code(1), summary.ResponseBody);
        Assert.Null(summary.ExchangesJson);
    }

    [Fact]
    public async Task CompletedFragmentSurvivesRestartAndCancellationIsScoped()
    {
        await using var fixture = await Fixture.CreateAsync();
        var payload = Encoding.UTF8.GetBytes(Code(1));
        await using var source = new MemoryStream(payload);
        var job = await fixture.Service.UploadAsync(DemoProfile, "one.txt", payload.Length, source, TestContext.Current.CancellationToken);
        var batch = new BulkCodeBatch { JobId = job.Id, Number = 0, StartOffset = 0, EndOffset = payload.Length, CodeCount = 1 };
        await fixture.Jobs.AddBatchAsync(batch, 1, TestContext.Current.CancellationToken);
        await fixture.Files.WriteFragmentAsync(job.Id, 0, [("INTRODUCED", Code(1))], 1, 0, TestContext.Current.CancellationToken);
        var client = new FakeClient();

        await fixture.Processor(client).ProcessNextAsync(TestContext.Current.CancellationToken);
        Assert.Empty(client.Requests);
        Assert.Equal("Completed", (await fixture.Service.GetAsync(DemoProfile, job.Id, TestContext.Current.CancellationToken))!.State);

        await using var another = new MemoryStream(payload);
        var toCancel = await fixture.Service.UploadAsync(DemoProfile, "cancel.txt", payload.Length, another, TestContext.Current.CancellationToken);
        await fixture.Service.CancelAsync(OtherProfile, toCancel.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Queued", (await fixture.Service.GetAsync(DemoProfile, toCancel.Id, TestContext.Current.CancellationToken))!.State);
        await fixture.Service.CancelAsync(DemoProfile, toCancel.Id, TestContext.Current.CancellationToken);
        await fixture.Processor(client).ProcessNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Canceled", (await fixture.Service.GetAsync(DemoProfile, toCancel.Id, TestContext.Current.CancellationToken))!.State);
        await fixture.Service.DeleteAsync(OtherProfile, toCancel.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(await fixture.Service.GetAsync(DemoProfile, toCancel.Id, TestContext.Current.CancellationToken));
        await fixture.Service.DeleteAsync(DemoProfile, toCancel.Id, TestContext.Current.CancellationToken);
        Assert.Null(await fixture.Service.GetAsync(DemoProfile, toCancel.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidFileStopsBeforeNetworkAndConcurrentBatchesStayBounded()
    {
        await using var fixture = await Fixture.CreateAsync();
        var invalid = Encoding.UTF8.GetBytes(Code(1) + "\n010460000000000021DEMO#00000001\n");
        await using var invalidSource = new MemoryStream(invalid);
        var badJob = await fixture.Service.UploadAsync(DemoProfile, "bad.txt", invalid.Length, invalidSource, TestContext.Current.CancellationToken);
        var client = new FakeClient();
        await fixture.Processor(client).ProcessNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Failed", (await fixture.Service.GetAsync(DemoProfile, badJob.Id, TestContext.Current.CancellationToken))!.State);
        Assert.Empty(client.Requests);

        var codes = Enumerable.Range(1, 5000).Select(Code);
        var data = Encoding.UTF8.GetBytes(string.Join('\n', codes));
        await using var source = new MemoryStream(data);
        var job = await fixture.Service.UploadAsync(DemoProfile, "five-thousand.txt", data.Length, source, TestContext.Current.CancellationToken);
        await fixture.Processor(client).ProcessNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Completed", (await fixture.Service.GetAsync(DemoProfile, job.Id, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(5, client.Requests.Count);
        Assert.InRange(client.PeakConcurrent, 1, 4);
        Assert.All(client.Requests, request => Assert.InRange(JsonSerializer.Deserialize<string[]>(request.Body!)!.Length, 1, 1000));
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

    private sealed class FakeClient(bool failSecondOnce = false) : ITrueApiClient
    {
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
                var codes = JsonSerializer.Deserialize<string[]>(request.Body!)!;
                var status = failSecondOnce && codes.Length == 1 && Interlocked.Increment(ref _secondAttempts) == 1 ? 503 : 200;
                var body = JsonSerializer.Serialize(codes.Select(code => new CodeInfoItem(new CisInfo
                {
                    RequestedCis = code, Cis = code, Status = "INTRODUCED"
                })));
                await Task.Delay(2, cancellationToken);
                return new IntegrationExchange(status, request.Method, request.Url, request.Headers, request.Body,
                    new Dictionary<string, string>(), body, TimeSpan.Zero);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class PatternStream(byte[] pattern, long length) : Stream
    {
        private long _position;
        public int LargestRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, length - _position);
            LargestRead = Math.Max(LargestRead, count);
            for (var i = 0; i < take; i++) buffer[offset + i] = pattern[(_position + i) % pattern.Length];
            _position += take;
            return take;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var take = (int)Math.Min(buffer.Length, length - _position);
            LargestRead = Math.Max(LargestRead, buffer.Length);
            for (var i = 0; i < take; i++) buffer.Span[i] = pattern[(_position + i) % pattern.Length];
            _position += take;
            return ValueTask.FromResult(take);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
