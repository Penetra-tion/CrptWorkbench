using System.Text;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class BulkShipmentFileServiceTests
{
    [Fact]
    public async Task LateFragmentWithNewStatusIsCheckedBeforeAdvancingCheckpoint()
    {
        await using var f = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var entries = Enumerable.Range(0, 100).Select(i => ("INTRODUCED", $"010460000000000021L{i:D8}")).ToArray();
        await f.Files.WriteFragmentAsync(f.Job.Id, 0, entries, 100, 0, ct);
        await f.Jobs.AddBatchAsync(new() { JobId = f.Job.Id, Number = 0, CodeCount = 100 }, 100, ct);
        await f.Jobs.AddBatchAsync(new() { JobId = f.Job.Id, Number = 1, CodeCount = 1 }, 101, ct);
        await f.Jobs.CompleteBatchAsync(f.Job.Id, 0, 100, 0, ct);
        var written = false;
        await f.Service.CheckAvailableAsync(f.Profile.Id, f.Job.Id,
            new ShipmentCheckService(new EmptyClient()).CreateSession(new(f.Profile, "late")), async () =>
            {
                if (written) return;
                written = true;
                await f.Files.WriteFragmentAsync(f.Job.Id, 1, [("EMITTED", "010460000000000021L00000100")], 1, 0, ct);
                await f.Jobs.CompleteBatchAsync(f.Job.Id, 1, 1, 0, ct);
            }, ct);
        Assert.Equal(101, (await f.Service.GetSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ReviewedRows);
    }

    [Fact]
    public async Task CancellationAtCheckpointResumesWithoutRepeatingCompletedHistories()
    {
        await using var f = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var entries = Enumerable.Range(0, 126).Select(i => ("INTRODUCED", $"010460000000000021C{i:D8}")).ToArray();
        await f.Files.WriteFragmentAsync(f.Job.Id, 0, entries, 126, 0, ct);
        await f.Jobs.AddBatchAsync(new() { JobId = f.Job.Id, Number = 0, CodeCount = 126 }, 126, ct);
        await f.Jobs.CompleteBatchAsync(f.Job.Id, 0, 126, 0, ct);
        await f.Jobs.SetJobStateAsync(f.Profile.Id, f.Job.Id, "Completed", null, ct);
        var client = new EmptyClient();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.CheckAvailableAsync(f.Profile.Id, f.Job.Id,
            new ShipmentCheckService(client).CreateSession(new(f.Profile, "first")), () => { cancelled.Cancel(); return Task.CompletedTask; }, cancelled.Token));
        Assert.Equal(100, (await f.Service.GetSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ReviewedRows);
        await f.NewService().CheckAvailableAsync(f.Profile.Id, f.Job.Id,
            new ShipmentCheckService(client).CreateSession(new(f.Profile, "resume")), null, ct);
        Assert.Equal(126, (await f.Service.GetSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ReviewedRows);
        Assert.Equal(126, client.Calls);
    }

    [Fact]
    public async Task FilteredPagesCountersAndExportAgreeAfterReopeningWithDuplicates()
    {
        await using var f = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var codes = Enumerable.Range(0, 125).Select(i => $"010460000000000021S{i:D8}").ToArray();
        var entries = codes.Select(x => ("INTRODUCED", x)).Concat(new[] { ("INTRODUCED", codes[0]) }).ToArray();
        await f.Files.WriteFragmentAsync(f.Job.Id, 0, entries, entries.Length, 0, ct);
        await f.Jobs.AddBatchAsync(new() { JobId = f.Job.Id, Number = 0, CodeCount = entries.Length }, entries.Length, ct);
        await f.Jobs.CompleteBatchAsync(f.Job.Id, 0, entries.Length, 0, ct);
        await f.Jobs.SetJobStateAsync(f.Profile.Id, f.Job.Id, "Completed", null, ct);
        // Pre-seeded outcomes isolate selection/checkpoint behavior from CRPT transport.
        var scope = new BulkShipmentCheckScope(f.Profile.Id, f.Job.Id, CrptIntegrationMode.Mock, f.Profile.Inn!);
        foreach (var code in codes)
            await f.Repository.SaveAsync(scope, new(code, ShipmentCheckState.Shipped, DateTimeOffset.UtcNow), ct);
        await f.Service.CheckAvailableAsync(f.Profile.Id, f.Job.Id,
            new ShipmentCheckService(new EmptyClient()).CreateSession(new(f.Profile, "test")), null, ct);
        var reopened = f.NewService();
        var summary = await reopened.GetSummaryAsync(f.Profile.Id, f.Job.Id, ct);
        Assert.Equal(126, summary.ReviewedRows);
        Assert.Equal(126, summary.ShippedCounts["INTRODUCED"]);
        Assert.Equal(0, summary.UnknownRows);
        var statuses = new HashSet<string> { "INTRODUCED" };
        var first = await reopened.PreviewPageAsync(f.Profile.Id, f.Job.Id, statuses, null, ct);
        var second = await reopened.PreviewPageAsync(f.Profile.Id, f.Job.Id, statuses, first.NextCursor, ct);
        Assert.Equal(100, first.Items.Count);
        Assert.Equal(26, second.Items.Count);
        Assert.Null(second.NextCursor);
        var exported = new List<string>();
        await foreach (var code in reopened.ExportAsync(f.Profile.Id, f.Job.Id, statuses, ct)) exported.Add(code);
        Assert.Equal(entries.Select(x => x.Item2), exported);
        Assert.Equal(exported, first.Items.Concat(second.Items));
        await f.Service.CheckAvailableAsync(f.Profile.Id, f.Job.Id,
            new ShipmentCheckService(new EmptyClient()).CreateSession(new(f.Profile, "test")), null, ct);
        Assert.Equal(126, (await f.Service.GetSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ReviewedRows);
    }

    [Fact]
    public async Task LegacyJobsStayUnfilteredUntilRequestedAndInnChangeHidesOldChecks()
    {
        await using var f = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var code = "010460000000000021S00000001";
        await f.Files.WriteFragmentAsync(f.Job.Id, 0, [("INTRODUCED", code)], 1, 0, ct);
        await f.Jobs.AddBatchAsync(new() { JobId = f.Job.Id, Number = 0, CodeCount = 1 }, 1, ct);
        await f.Jobs.CompleteBatchAsync(f.Job.Id, 0, 1, 0, ct);
        await f.Jobs.SetJobStateAsync(f.Profile.Id, f.Job.Id, "Completed", null, ct);
        Assert.Equal(0, (await f.Service.GetSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ReviewedRows);
        var original = new BulkCodeFileService(f.Jobs, f.Files, f.Profiles, new());
        Assert.Equal(new[] { code }, await original.PreviewAsync(f.Profile.Id, f.Job.Id, new HashSet<string> { "INTRODUCED" }, ct));
        await f.Service.CheckAvailableAsync(f.Profile.Id, f.Job.Id,
            new ShipmentCheckService(new EmptyClient()).CreateSession(new(f.Profile, "test")), null, ct);
        Assert.Equal(1, (await f.Service.GetSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ReviewedRows);
        var pendingExport = f.Service.ExportAsync(f.Profile.Id, f.Job.Id, new HashSet<string> { "INTRODUCED" }, ct,
            f.Profile.Inn, CrptIntegrationMode.Mock);
        f.Profile.Inn = "7800000000";
        await f.Profiles.UpsertAsync(f.Profile, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in pendingExport) Assert.Fail("Stale export must not expose another scope.");
        });
        Assert.Equal(0, (await f.Service.GetSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ReviewedRows);
        var empty = await f.Service.PreviewPageAsync(f.Profile.Id, f.Job.Id, new HashSet<string> { "INTRODUCED" }, null, ct);
        Assert.Empty(empty.Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.GetSummaryAsync(Guid.NewGuid(), f.Job.Id, ct));
    }

    private sealed class EmptyClient : ITrueApiClient
    {
        private int _calls;
        public int Calls => _calls;
        public Uri BaseAddress => new("https://true-api.mock.invalid/api/v3/true-api");
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new IntegrationExchange(404, request.Method, request.Url, request.Headers, request.Body,
                new Dictionary<string, string> { ["X-CRPT-Workbench-Mock"] = "true" }, "{}", TimeSpan.Zero));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"shipment-files-{Guid.NewGuid():N}");
        public TraceXAndFavoritesTests.TestDbFactory Factory { get; private set; } = null!;
        public BulkCodeFileStore Files { get; private set; } = null!;
        public BulkCodeJobRepository Jobs { get; private set; } = null!;
        public ProfileRepository Profiles { get; private set; } = null!;
        public BulkShipmentCheckRepository Repository { get; private set; } = null!;
        public CrptProfile Profile { get; } = new() { Name = "Shipments", Inn = "7700000000" };
        public BulkCodeJob Job { get; private set; } = null!;
        public BulkShipmentFileService Service { get; private set; } = null!;
        public BulkShipmentFileService NewService() => new(Jobs, Files, Profiles, new BulkShipmentCheckRepository(Factory), new());
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.DirectoryPath);
            f.Factory = new(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={Path.Combine(f.DirectoryPath, "test.db")};Pooling=False").Options);
            await using (var db = f.Factory.CreateDbContext()) await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            f.Files = new(f.DirectoryPath);
            f.Jobs = new(f.Factory);
            f.Profiles = new(f.Factory);
            f.Repository = new(f.Factory);
            await f.Profiles.UpsertAsync(f.Profile, TestContext.Current.CancellationToken);
            f.Job = new() { ProfileId = f.Profile.Id, Mode = CrptIntegrationMode.Mock, State = "Running" };
            await f.Jobs.AddAsync(f.Job, TestContext.Current.CancellationToken);
            f.Service = f.NewService();
            return f;
        }
        public ValueTask DisposeAsync() { Directory.Delete(DirectoryPath, true); return ValueTask.CompletedTask; }
    }
}
