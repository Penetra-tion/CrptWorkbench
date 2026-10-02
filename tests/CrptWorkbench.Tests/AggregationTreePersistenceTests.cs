using System.Net;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class AggregationTreePersistenceTests
{
    [Fact]
    public async Task TreeReopensFromSingleMaskedProfileScopedHistoryWithCorrelation()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"crpt-tree-{Guid.NewGuid():N}.db");
        var factory = new DbFactory(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync(ct);
            var profiles = new ProfileRepository(factory);
            var profile = new CrptProfile { Name = "Tree profile" };
            var other = new CrptProfile { Name = "Other profile" };
            await profiles.UpsertAsync(profile, ct);
            await profiles.UpsertAsync(other, ct);
            var handler = new TrueApiAggregationTreeHandler(new MockTrueApiClient());
            var repository = new ApiRequestLogRepository(factory);
            var executor = new ExecuteApiOperationService(new ApiOperationRegistry([handler]), profiles, repository, new SensitiveDataMasker());
            var result = await executor.ExecuteAsync(handler.Descriptor.Id,
                AggregationTreeTests.Input("010460000000000021TREE000000001", true), profile.Id, ct);
            Assert.True(result.Success);
            var tree = AggregationTreeResult.Parse(result.ResponseBody)!;
            Assert.True(tree.Complete);
            Assert.True(tree.CanExport);
            var reopened = new HistoryService(new ApiRequestLogRepository(factory));
            var row = Assert.Single(await reopened.ListAsync(profile.Id, 100, ct));
            Assert.Empty(await reopened.ListAsync(other.Id, 100, ct));
            Assert.Null(await reopened.GetAsync(other.Id, row.Id, ct));
            var saved = (await reopened.GetAsync(profile.Id, row.Id, ct))!;
            Assert.Equal(result.CorrelationId, saved.CorrelationId);
            Assert.Equal(TrueApiAggregationTreeHandler.OperationId, saved.OperationId);
            Assert.Equal(result.ResponseBody, saved.ResponseBody);
            Assert.Equal(tree.Nodes.Count, AggregationTreeResult.Parse(saved.ResponseBody)!.Nodes.Count);
            Assert.Contains("Bearer ********", saved.RequestHeaders!);
            Assert.Contains("X-CRPT-Workbench-Mock", saved.ResponseHeaders!);
            Assert.Contains(result.CorrelationId, saved.RequestHeaders!);
            Assert.Contains(result.CorrelationId, saved.ResponseHeaders!);
            Assert.Contains(result.CorrelationId, saved.ExchangesJson!);
            Assert.DoesNotContain("mock-token-not-a-real-secret", string.Join("\n", saved.RequestHeaders, saved.ResponseHeaders, saved.ExchangesJson));
            using var before = new MemoryStream();
            using var after = new MemoryStream();
            await tree.WriteTxtAsync(before, ct);
            await AggregationTreeResult.Parse(saved.ResponseBody)!.WriteTxtAsync(after, ct);
            Assert.Equal(before.ToArray(), after.ToArray());

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var cancelClient = new AggregationTreeTests.FakeClient((r, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(AggregationTreeTests.Response(r, []));
            });
            var cancelHandler = new TrueApiAggregationTreeHandler(cancelClient);
            var cancelExecutor = new ExecuteApiOperationService(new ApiOperationRegistry([cancelHandler]), profiles, repository, new SensitiveDataMasker());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelExecutor.ExecuteAsync(cancelHandler.Descriptor.Id,
                AggregationTreeTests.Input(AggregationTreeTests.Code(1)), profile.Id, cancellation.Token));
            Assert.Single(await reopened.ListAsync(profile.Id, 100, ct));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("POST", "", "valid", true)]
    [InlineData("GET", "", "valid", false)]
    [InlineData("POST", "?pg=milk", "valid", false)]
    [InlineData("POST", "/extra", "valid", false)]
    [InlineData("POST", "", "empty", false)]
    [InlineData("POST", "", "object", false)]
    [InlineData("POST", "", "short", false)]
    [InlineData("POST", "", "gs", false)]
    [InlineData("POST", "", "over1000", false)]
    [InlineData("POST", "", "null", false)]
    [InlineData("POST", "", "other-host", false)]
    public async Task ProductionTreeGuardChecksExactMethodUrlAndBodyBeforeAuthentication(string method, string suffix, string kind, bool allowed)
    {
        var tokens = new CountingTokens();
        using var transport = new LocalTransport();
        var client = new RealTrueApiClient(new LocalFactory(transport), tokens, Options.Create(new CrptIntegrationOptions
            { Mode = CrptIntegrationMode.Production, TrueApiBaseUrl = CrptIntegrationOptions.ProductionTrueApiBaseUrl }));
        var code = AggregationTreeTests.Code(1);
        var body = kind switch
        {
            "empty" => "[]", "object" => "{}", "short" => "[\"bad\"]", "null" => "[null]",
            "gs" => JsonSerializer.Serialize(new[] { code + "\u001D91SECRET" }),
            "over1000" => JsonSerializer.Serialize(Enumerable.Range(1, 1001).Select(AggregationTreeTests.Code)),
            _ => JsonSerializer.Serialize(new[] { code })
        };
        var url = kind == "other-host" ? "https://unapproved.example/api/v3/true-api/cises/info"
            : CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/cises/info" + suffix;
        var request = new IntegrationRequest(TrueApiAggregationTreeHandler.OperationId, method, url,
            new Dictionary<string, string> { ["X-Correlation-Id"] = "guard-tree-correlation" }, body,
            "guard-tree-correlation", new(Guid.NewGuid(), "7700000000", "0123456789ABCDEF0123456789ABCDEF01234567"));
        if (allowed)
        {
            var response = await client.ExecuteAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal(1, tokens.Requests);
            Assert.Equal(1, transport.Calls);
            Assert.Equal(body, transport.Body);
            Assert.Equal("Bearer local-test-token", transport.Authorization);
            Assert.Equal(request.CorrelationId, transport.Correlation);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(request, TestContext.Current.CancellationToken));
            Assert.Equal(0, tokens.Requests);
            Assert.Equal(0, transport.Calls);
        }
    }

    private sealed class DbFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    { public WorkbenchDbContext CreateDbContext() => new(options); }

    private sealed class CountingTokens : ITrueApiTokenProvider
    {
        public int Requests { get; private set; }
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId, CancellationToken cancellationToken = default)
        { Requests++; return Task.FromResult("local-test-token"); }
        public void Invalidate(IntegrationAuthenticationContext authentication) { }
    }

    private sealed class LocalFactory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }

    private sealed class LocalTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public string? Correlation { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            Correlation = request.Headers.GetValues("X-Correlation-Id").Single();
            return new(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        }
    }
}
