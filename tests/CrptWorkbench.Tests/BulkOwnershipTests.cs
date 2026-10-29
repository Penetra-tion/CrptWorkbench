using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class BulkOwnershipTests
{
    [Fact]
    public void PastedSelectionUsesEachRowsOwnerAndExcludesErrors()
    {
        var results = CodeInfoResults.Parse("""
            [{"cisInfo":{"requestedCis":"same","status":"INTRODUCED","ownerInn":"7700000000"}},
             {"cisInfo":{"requestedCis":"same","status":"INTRODUCED","ownerInn":"7800000000"}},
             {"cisInfo":{"requestedCis":"missing","status":"INTRODUCED"}},
             {"cisInfo":{"requestedCis":"empty","status":"APPLIED","ownerInn":""}},
             {"cisInfo":{"requestedCis":"error","status":"INTRODUCED"},"errorCode":"404","errorMessage":"missing"}]
            """)!;
        Assert.Equal("same\nmissing\nempty", BulkCodeInfoSelection.CopyCodes(
            BulkCodeInfoSelection.FilterByOwner(results, new HashSet<string>(), "7700000000")));
        Assert.Equal("same\nmissing", BulkCodeInfoSelection.CopyCodes(
            BulkCodeInfoSelection.FilterByOwner(results, new HashSet<string> { "INTRODUCED" }, "7700000000")));
        Assert.Throws<InvalidOperationException>(() => BulkCodeInfoSelection.FilterByOwner(results, new HashSet<string>(), " "));
    }

    [Fact]
    public async Task OwnedFilesPreserveDuplicatesLegacyRowsPagingAndScopeAfterReopening()
    {
        await using var f = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var rows = Enumerable.Range(0, 125).Select(i => new BulkCodeEntry("INTRODUCED", $"code{i}", null, true))
            .Concat(new[] { new BulkCodeEntry("INTRODUCED", "code0", "7800000000", true),
                new BulkCodeEntry("INTRODUCED", "own", "7700000000", true),
                new BulkCodeEntry("APPLIED", "empty", "", true) }).ToArray();
        await f.SaveAsync(0, rows);
        await f.Files.WriteFragmentAsync(f.Job.Id, 1, [("INTRODUCED", "legacy")], 1, 0, ct);
        await f.CompleteAsync(1, 1);
        await f.Jobs.SetJobStateAsync(f.Profile.Id, f.Job.Id, "Paused", null, ct);
        var service = f.NewService();
        var summary = await service.GetOwnershipSummaryAsync(f.Profile.Id, f.Job.Id, ct);
        Assert.Equal(129, summary.ReviewedRows);
        Assert.Equal(1, summary.UnknownRows);
        Assert.Equal(126, summary.ShippedCounts["INTRODUCED"]);
        Assert.Equal(1, summary.ShippedCounts["APPLIED"]);
        Assert.Equal(summary.ShippedCounts, (await service.GetOwnershipSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ShippedCounts);
        var statuses = new HashSet<string>();
        var first = await service.OwnershipPreviewPageAsync(f.Profile.Id, f.Job.Id, statuses, null, ct);
        var second = await service.OwnershipPreviewPageAsync(f.Profile.Id, f.Job.Id, statuses, first.NextCursor, ct);
        Assert.Equal(100, first.Items.Count);
        Assert.Equal(27, second.Items.Count);
        var exported = await ExportAsync(service, f.Profile.Id, f.Job.Id, statuses, ct);
        Assert.Equal(first.Items.Concat(second.Items), exported);
        Assert.Equal(129, (await f.Files.CountCodesByStatusAsync(f.Job.Id, 2, ct)).Values.Sum());
        var unfiltered = new List<string>();
        await foreach (var code in f.Files.ReadCodesAsync(f.Job.Id, 2, statuses, ct)) unfiltered.Add(code);
        Assert.Equal(129, unfiltered.Count);
        f.Profile.Inn = "7800000000";
        await f.Profiles.UpsertAsync(f.Profile, ct);
        Assert.Equal(126, (await service.GetOwnershipSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ShippedCounts["INTRODUCED"]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.OwnershipExportAsync(f.Profile.Id, f.Job.Id, statuses, ct, "7700000000", CrptIntegrationMode.Mock)) { }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetOwnershipSummaryAsync(Guid.NewGuid(), f.Job.Id, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BulkShipmentFileService(f.Jobs, f.Files, f.Profiles,
            new BulkShipmentCheckRepository(f.Factory), new() { Mode = CrptIntegrationMode.Production }).GetOwnershipSummaryAsync(f.Profile.Id, f.Job.Id, ct));
        f.Profile.Inn = null;
        await f.Profiles.UpsertAsync(f.Profile, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetOwnershipSummaryAsync(f.Profile.Id, f.Job.Id, ct));
    }

    [Fact]
    public async Task PartialResultsIncludeLaterFragmentsAndRevisitMissingBatchWithoutDoubleCounting()
    {
        await using var f = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        await f.Jobs.AddBatchAsync(new() { JobId = f.Job.Id, Number = 0, CodeCount = 1 }, 1, ct);
        await f.Jobs.FailBatchAsync(f.Job.Id, 0, "failed", ct);
        await f.SaveAsync(1, [new("INTRODUCED", "later", null, true)]);
        await f.Jobs.SetJobStateAsync(f.Profile.Id, f.Job.Id, "Partial", null, ct);
        var service = f.NewService();
        Assert.Equal(1, (await service.GetOwnershipSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ReviewedRows);
        Assert.Equal(new[] { "later" }, await ExportAsync(service, f.Profile.Id, f.Job.Id, new HashSet<string>(), ct));
        await f.Files.WriteOwnedFragmentAsync(f.Job.Id, 0, [new("INTRODUCED", "retried", null, true)], 1, 0, ct);
        Assert.Equal(2, (await service.GetOwnershipSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ReviewedRows);
        Assert.Equal(2, (await service.GetOwnershipSummaryAsync(f.Profile.Id, f.Job.Id, ct)).ReviewedRows);
        Assert.Equal(new[] { "retried", "later" }, await ExportAsync(service, f.Profile.Id, f.Job.Id, new HashSet<string>(), ct));
    }

    private static async Task<List<string>> ExportAsync(BulkShipmentFileService service, Guid profile, Guid job,
        IReadOnlySet<string> statuses, CancellationToken ct)
    {
        var result = new List<string>();
        await foreach (var code in service.OwnershipExportAsync(profile, job, statuses, ct)) result.Add(code);
        return result;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ownership-{Guid.NewGuid():N}");
        public TraceXAndFavoritesTests.TestDbFactory Factory { get; private set; } = null!;
        public BulkCodeFileStore Files { get; private set; } = null!;
        public BulkCodeJobRepository Jobs { get; private set; } = null!;
        public ProfileRepository Profiles { get; private set; } = null!;
        public CrptProfile Profile { get; } = new() { Name = "Ownership", Inn = "7700000000" };
        public BulkCodeJob Job { get; private set; } = null!;
        public BulkShipmentFileService NewService() => new(Jobs, new BulkCodeFileStore(_directory), Profiles, new BulkShipmentCheckRepository(Factory), new());
        public async Task CompleteAsync(long number, int count)
        {
            var ct = TestContext.Current.CancellationToken;
            await Jobs.AddBatchAsync(new() { JobId = Job.Id, Number = number, CodeCount = count }, count, ct);
            await Jobs.CompleteBatchAsync(Job.Id, number, count, 0, ct);
        }
        public async Task SaveAsync(long number, IReadOnlyList<BulkCodeEntry> rows)
        {
            await Files.WriteOwnedFragmentAsync(Job.Id, number, rows, rows.Count, 0, TestContext.Current.CancellationToken);
            await CompleteAsync(number, rows.Count);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f._directory);
            f.Factory = new(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={Path.Combine(f._directory, "test.db")};Pooling=False").Options);
            await using (var db = f.Factory.CreateDbContext()) await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            f.Files = new(f._directory); f.Jobs = new(f.Factory); f.Profiles = new(f.Factory);
            await f.Profiles.UpsertAsync(f.Profile, TestContext.Current.CancellationToken);
            f.Job = new() { ProfileId = f.Profile.Id, Mode = CrptIntegrationMode.Mock, State = "Running" };
            await f.Jobs.AddAsync(f.Job, TestContext.Current.CancellationToken);
            return f;
        }
        public ValueTask DisposeAsync() { Directory.Delete(_directory, true); return ValueTask.CompletedTask; }
    }
}
