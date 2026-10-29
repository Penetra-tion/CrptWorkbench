using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Web;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class AggregationTreePresentationTests
{
    [Fact]
    public void LegacyTypeProjectionKeepsChildToRootOrderAndDeduplicatesProjectedRows()
    {
        var tree = new AggregationTreeResult("box", false, "box",
            [new("box", "BOX", null, true, null, 2, 0), new("one", "UNIT", null, true, null, 0, 0), new("two", "UNIT", null, true, null, 0, 0)],
            [new("box", "one", true), new("box", "two", true)], true, []);
        var options = new AggregationExportOptions(false, ["BOX"]);
        Assert.Equal(new[] { "box" }, Assert.Single(tree.ExportPaths(options)));
        Assert.Equal(2, tree.ExportPaths(new()).Count());
        Assert.Empty(tree.ExportPaths(new(false, ["SET"])));
        Assert.Throws<ArgumentException>(() => tree.ExportPaths(new(false, [])).ToArray());
    }

    [Fact]
    public void DurableHistoryReferenceOpensJobWithoutDeserializingAnEntireGraph()
    {
        var id = Guid.NewGuid();
        var log = new ApiRequestLog { OperationId = TrueApiAggregationTreeHandler.OperationId,
            ResponseBody = JsonSerializer.Serialize(new { kind = "aggregation-tree-job", jobId = id, state = "Paused" }) };
        var view = SavedResultView.From(log);
        Assert.Equal(id, view.AggregationJobId);
        Assert.Null(view.AggregationTree);
    }

    [Fact]
    public void TicketsBindProfileJobModeExportAndRejectWrongPurposeOrTampering()
    {
        var tickets = new AggregationTreeDownloadTickets(new EphemeralDataProtectionProvider());
        var profile = Guid.NewGuid(); var job = Guid.NewGuid(); var export = Guid.NewGuid();
        var token = tickets.Issue(profile, job, CrptIntegrationMode.Production, export);
        Assert.True(tickets.TryRead(token, false, out var result));
        Assert.Equal(profile, result!.ProfileId); Assert.Equal(job, result.JobId);
        Assert.Equal(export, result.ExportId); Assert.Equal(CrptIntegrationMode.Production, result.Mode);
        Assert.False(tickets.TryRead(token, true, out _));
        Assert.False(tickets.TryRead(token + "tampered", false, out _));
        Assert.True(tickets.TryRead(tickets.IssueRejected(profile, job, CrptIntegrationMode.Mock), true, out _));
    }
}
