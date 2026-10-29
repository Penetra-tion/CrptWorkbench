using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Web;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class BulkShipmentSelectionTests
{
    [Fact]
    public void ShipmentSelectionIntersectsStatusesAndExcludesUnknownAndUnchecked()
    {
        var results = CodeInfoResults.Parse("[{\"cisInfo\":{\"requestedCis\":\"A\",\"status\":\"INTRODUCED\"}},{\"cisInfo\":{\"requestedCis\":\"B\",\"status\":\"INTRODUCED\"}},{\"cisInfo\":{\"requestedCis\":\"C\",\"status\":\"APPLIED\"}},{\"cisInfo\":{\"requestedCis\":\"D\",\"status\":\"INTRODUCED\"}}]")!;
        var checks = new Dictionary<string, ShipmentCheckResult>
        {
            ["A"] = new("A", ShipmentCheckState.Shipped),
            ["B"] = new("B", ShipmentCheckState.Unknown),
            ["C"] = new("C", ShipmentCheckState.Shipped)
        };
        var statuses = new HashSet<string> { "INTRODUCED" };
        Assert.Equal("A", BulkCodeInfoSelection.CopyCodes(BulkCodeInfoSelection.Filter(results, statuses, true, checks)));
        Assert.Equal("A\nB\nD", BulkCodeInfoSelection.CopyCodes(BulkCodeInfoSelection.Filter(results, statuses, false, checks)));
        Assert.Equal("A\nC", BulkCodeInfoSelection.CopyCodes(BulkCodeInfoSelection.Filter(results, new HashSet<string>(), true, checks)));
    }

    [Fact]
    public void ShipmentDownloadTicketRetainsFilterAndScopeAndRejectsTampering()
    {
        var tickets = new BulkCodeDownloadTickets(new EphemeralDataProtectionProvider());
        var profile = Guid.NewGuid();
        var job = Guid.NewGuid();
        var token = tickets.Issue(profile, job, new[] { "INTRODUCED" }, true, "7700000000", CrptIntegrationMode.Mock);
        Assert.True(tickets.TryRead(token, out var readProfile, out var readJob, out var statuses, out var shipped, out var inn, out var mode));
        Assert.Equal(profile, readProfile);
        Assert.Equal(job, readJob);
        Assert.Equal(new[] { "INTRODUCED" }, statuses);
        Assert.True(shipped);
        Assert.Equal("7700000000", inn);
        Assert.Equal(CrptIntegrationMode.Mock, mode);
        Assert.False(tickets.TryRead(token[..^8] + "xxxxxxxx", out _, out _, out _, out _, out _, out _));
        var old = tickets.Issue(profile, job, new[] { "INTRODUCED" });
        Assert.True(tickets.TryRead(old, out _, out _, out _, out var oldShipped, out _, out _));
        Assert.False(oldShipped);

        var all = tickets.Issue(profile, job, []);
        Assert.True(tickets.TryRead(all, out _, out _, out var allStatuses));
        Assert.Empty(allStatuses);
    }
}
