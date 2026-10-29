using System.Data.Common;
using System.Reflection;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class HistorySummaryTests
{
    [Fact]
    public async Task SummaryPagingOmitsPayloadColumnsAndSelectedDetailsRemainProfileScoped()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"crpt-history-summary-{Guid.NewGuid():N}.db");
        var reads = new ReadCommands();
        var factory = new TraceXAndFavoritesTests.TestDbFactory(new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(reads).Options);
        var profile = new CrptProfile { Name = "Summary" };
        var other = new CrptProfile { Name = "Other" };
        var now = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        var selected = new ApiRequestLog { ProfileId = profile.Id, CreatedAt = now, CreatedAtUnixMs = 100,
            OperationId = "true-api.code-info", System = ApiSystem.TrueApi, StatusCode = 200, DurationMs = 42,
            Success = true, RequestHeaders = "request-headers", RequestBody = "request-body",
            ResponseHeaders = "response-headers", ResponseBody = "response-body", ExchangesJson = "exchanges" };
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync(ct);
                db.Profiles.AddRange(profile, other);
                db.ApiRequestLogs.Add(selected);
                for (var i = 0; i < 100; i++) db.ApiRequestLogs.Add(new() { ProfileId = profile.Id, CreatedAtUnixMs = 100 });
                db.ApiRequestLogs.Add(new() { ProfileId = other.Id, CreatedAtUnixMs = 200 });
                await db.SaveChangesAsync(ct);
            }
            reads.Commands.Clear();
            IApiRequestLogRepository repository = new ApiRequestLogRepository(factory);
            var first = await repository.ListSummaryPageAsync(profile.Id, cancellationToken: ct);
            Assert.Equal(101, first.TotalCount);
            Assert.Equal(100, first.Items.Count);
            var second = await repository.ListSummaryPageAsync(profile.Id, first.NextCursor, ct);
            Assert.Null(second.NextCursor);
            Assert.Single(second.Items);
            var rows = first.Items.Concat(second.Items).ToArray();
            Assert.Equal(101, rows.Select(x => x.Id).Distinct().Count());
            var row = Assert.Single(rows, x => x.Id == selected.Id);
            Assert.Equal(now, row.CreatedAt);
            Assert.Equal("true-api.code-info", row.OperationId);
            Assert.Equal(200, row.StatusCode);
            Assert.Equal(42, row.DurationMs);
            Assert.True(row.Success);
            Assert.All(reads.Commands, sql =>
            {
                Assert.DoesNotContain("RequestBody", sql);
                Assert.DoesNotContain("ResponseBody", sql);
                Assert.DoesNotContain("ExchangesJson", sql);
            });
            var detail = await repository.GetAsync(profile.Id, selected.Id, ct);
            Assert.Equal("request-body", detail!.RequestBody);
            Assert.Equal("response-body", detail.ResponseBody);
            Assert.Equal("exchanges", detail.ExchangesJson);
            Assert.Null(await repository.GetAsync(other.Id, selected.Id, ct));
            foreach (var suffix in new[] { "", $"?id={selected.Id}" })
            {
                var component = new CrptWorkbench.Web.Components.Pages.History();
                var type = component.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                type.GetProperty("HistoryService", flags)!.SetValue(component, new HistoryService(repository));
                type.GetProperty("Workbench", flags)!.SetValue(component, new WorkbenchContext { ProfileId = profile.Id });
                type.GetProperty("Navigation", flags)!.SetValue(component, new TestNavigation("http://localhost/history" + suffix));
                type.GetField("_items", flags)!.SetValue(component, new ApiRequestLogSummary[] { row });
                await (Task)type.GetMethod("SelectFromUrlAsync", flags)!.Invoke(component, null)!;
                var opened = (ApiRequestLog)type.GetField("_selected", flags)!.GetValue(component)!;
                Assert.Equal("response-body", opened.ResponseBody);
                Assert.Equal("exchanges", opened.ExchangesJson);
                component.Dispose();
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class ReadCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string url) => Initialize("http://localhost/", url);
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}
