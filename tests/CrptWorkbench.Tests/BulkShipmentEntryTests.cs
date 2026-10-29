using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class BulkShipmentEntryTests
{
    [Fact]
    public async Task EntriesRetainStatusesDuplicatesAndExactContinuation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"shipment-entries-{Guid.NewGuid():N}");
        var store = new BulkCodeFileStore(directory);
        var id = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var input = Enumerable.Range(0, 101).Select(i => (i % 2 == 0 ? "INTRODUCED" : "APPLIED", $"Код-{i / 2}")).ToArray();
            await store.WriteFragmentAsync(id, 0, input, 101, 0, ct);
            var statuses = new HashSet<string> { "INTRODUCED", "APPLIED" };
            var first = await store.ReadEntriesPageAsync(id, 1, statuses, null, ct);
            Assert.Equal(100, first.Items.Count);
            Assert.NotNull(first.NextCursor);
            var second = await store.ReadEntriesPageAsync(id, 1, statuses, first.NextCursor, ct);
            Assert.Single(second.Items);
            Assert.Null(second.NextCursor);
            Assert.Equal(input.Select(x => new BulkCodeEntry(x.Item1, x.Item2)), first.Items.Concat(second.Items));
        }
        finally { store.Delete(id); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MissingFragmentBlocksCheckpointUntilItArrives()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"shipment-gap-{Guid.NewGuid():N}");
        var store = new BulkCodeFileStore(directory);
        var id = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await store.WriteFragmentAsync(id, 1, [("INTRODUCED", "late")], 1, 0, ct);
            var statuses = new HashSet<string> { "INTRODUCED" };
            var waiting = await store.ReadEntriesPageAsync(id, 2, statuses, null, ct);
            Assert.Empty(waiting.Items);
            Assert.Equal(new FilePageCursor(0, 0), waiting.NextCursor);
            await store.WriteFragmentAsync(id, 0, [("INTRODUCED", "first")], 1, 0, ct);
            var resumed = await store.ReadEntriesPageAsync(id, 2, statuses, waiting.NextCursor, ct);
            Assert.Equal(new[] { "first", "late" }, resumed.Items.Select(x => x.Code));
            Assert.Null(resumed.NextCursor);
        }
        finally { store.Delete(id); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task EmptyStatusSelectionReadsEveryShipmentEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"shipment-all-statuses-{Guid.NewGuid():N}");
        var store = new BulkCodeFileStore(directory);
        var id = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await store.WriteFragmentAsync(id, 0,
                [("INTRODUCED", "first"), ("APPLIED_NOT_PAID", "second"), ("FUTURE_STATUS", "third")], 3, 0, ct);

            var page = await store.ReadEntriesPageAsync(id, 1, new HashSet<string>(), null, ct);

            Assert.Equal(new[] { "first", "second", "third" }, page.Items.Select(x => x.Code));
        }
        finally { store.Delete(id); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
