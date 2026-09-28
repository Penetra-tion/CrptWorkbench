using System.Net;
using System.Text;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class DocumentStatusTests
{
    private static readonly ApiOperationContext Context = new(new CrptProfile(), "document-correlation");

    [Fact]
    public void DescriptorUsesDocumentIdText()
    {
        var field = Assert.Single(new TrueApiDocumentStatusHandler(new MockTrueApiClient()).Descriptor.Fields, x => x.Name == "documentId");

        Assert.Equal("GUID документа", field.Label);
        Assert.Equal("00000000-0000-0000-0000-000000000001", field.Placeholder);
    }

    [Theory]
    [InlineData("demo-success", "", "doc/demo-success/info?body=false&content=false")]
    [InlineData("Файл 123", "furs", "doc/%D0%A4%D0%B0%D0%B9%D0%BB%20123/info?body=false&content=false&pg=furs")]
    public async Task HandlerBuildsOneReadRequest(string id, string group, string expected)
    {
        var client = new CapturingClient();
        using var cancellation = new CancellationTokenSource();
        await new TrueApiDocumentStatusHandler(client).ExecuteAsync(new Dictionary<string, string> { ["documentId"] = id, ["productGroup"] = group }, Context, cancellation.Token);
        Assert.Equal(expected, client.Request!.Url);
        Assert.Equal("GET", client.Request.Method);
        Assert.Null(client.Request.Body);
        Assert.Equal(Context.CorrelationId, client.Request.CorrelationId);
        Assert.Equal(cancellation.Token, client.Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../other")]
    [InlineData("x/y")]
    [InlineData("x%2Fy")]
    [InlineData("x?body=true")]
    [InlineData("x\n")]
    public void UnsafeIdsAreRejected(string id) =>
        Assert.Throws<ArgumentException>(() => TrueApiDocumentStatusHandler.Parse(new Dictionary<string, string> { ["documentId"] = id }));

    [Fact]
    public async Task CancelledRequestDoesNotReachClient()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new CapturingClient();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TrueApiDocumentStatusHandler(client)
            .ExecuteAsync(new Dictionary<string, string> { ["documentId"] = "demo-success" }, Context, cancellation.Token));
        Assert.Null(client.Request);
    }

    [Theory]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v4/true-api/doc/demo-success/info?body=false&content=false", true)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v4/true-api/doc/demo-success/info?body=false&content=false&pg=furs", true)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v4/true-api/doc/demo-success/info?body=false&content=false&pg=furs&pg=lp", false)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v4/true-api/doc/demo-success/info?body=true&content=false", false)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v4/true-api/doc/demo-success/info?body=false&content=false&evil=1", false)]
    [InlineData("POST", "https://markirovka.crpt.ru/api/v4/true-api/doc/demo-success/info?body=false&content=false", false)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v4/true-api/doc/demo-success/content?body=false&content=false", false)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v4/true-api/doc/x%00/info?body=false&content=false", false)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v4/true-api/doc/x%2Fy/info?body=false&content=false", false)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v4/true-api/doc/x%25y/info?body=false&content=false", false)]
    [InlineData("GET", "https://markirovka.crpt.ru.evil.invalid/api/v4/true-api/doc/demo-success/info?body=false&content=false", false)]
    public async Task ProductionGuardAllowsOnlyDocumentInfo(string method, string url, bool allowed)
    {
        using var guard = new ProductionTrueApiNetworkGuardHandler { InnerHandler = new ConstantHandler(HttpStatusCode.OK) };
        using var invoker = new HttpMessageInvoker(guard);
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (allowed) Assert.Equal(HttpStatusCode.OK, (await invoker.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MockCasesAndParserCoverErrorsAndEmptyResult()
    {
        var client = new MockTrueApiClient();
        foreach (var (id, status, count) in new[] { ("demo-success", "ACCEPTED", 1), ("demo-pending", "IN_PROGRESS", 1), ("demo-error", "PARSE_ERROR", 1), ("missing", "", 0) })
        {
            var response = await new TrueApiDocumentStatusHandler(client).ExecuteAsync(new Dictionary<string, string> { ["documentId"] = id }, Context, TestContext.Current.CancellationToken);
            Assert.Equal("true", response.ResponseHeaders["X-CRPT-Workbench-Mock"]);
            Assert.Equal(Context.CorrelationId, response.ResponseHeaders["X-Correlation-Id"]);
            Assert.True(response.Duration > TimeSpan.Zero);
            var parsed = Assert.IsType<DocumentStatusResults>(DocumentStatusResults.Parse(response.ResponseBody));
            Assert.Equal(count, parsed.Items.Count);
            if (count > 0) Assert.Equal(status, parsed.Items[0].Status);
            if (id == "demo-error") Assert.Equal(2, DocumentStatusResults.ErrorTexts(parsed.Items[0]).Count);
        }
        Assert.Null(DocumentStatusResults.Parse("not json"));
        Assert.Equal("Не указано", DocumentStatusResults.Value(null));
        Assert.Equal("Не указано", DocumentStatusResults.ReceivedAt(null));
        Assert.Equal("unknown", DocumentStatusResults.Status("unknown"));
    }

    [Fact]
    public void ParserKeepsMultipleRecordsAndMissingFields()
    {
        const string json = """
            [{"receivedAt":"2026-09-23T09:15:00Z","type":"LP_INTRODUCE_GOODS","productGroup":["lp","shoes"],"status":"CHECKED_OK"},
             {"receivedAt":"bad-date","type":"OTHER_TYPE","productGroup":null,"status":null,"errors":["Ошибка","Ошибка"],"commonErrors":[{"errorMessage":"Другая ошибка"}]}]
            """;
        var items = Assert.IsType<DocumentStatusResults>(DocumentStatusResults.Parse(json)).Items;
        Assert.Equal(2, items.Count);
        Assert.Equal("23.09.2026 09:15:00 UTC", DocumentStatusResults.ReceivedAt(items[0].ReceivedAt));
        Assert.Equal(2, items[0].ProductGroup!.Count);
        Assert.Equal("Ввод в оборот. Производство РФ", DocumentStatusResults.Type(items[0].Type));
        Assert.Equal("Неизвестный тип документа", DocumentStatusResults.Type(items[1].Type));
        Assert.Equal("Не указано", DocumentStatusResults.Status(items[1].Status));
        Assert.Equal("Не указано", DocumentStatusResults.ReceivedAt(items[1].ReceivedAt));
        Assert.Equal(2, DocumentStatusResults.ErrorTexts(items[1]).Count);
    }

    [Fact]
    public async Task ExecutorStoresMaskedDocumentExchangeInHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crpt-document-status-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var factory = new TestDbFactory(options);
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var profiles = new ProfileRepository(factory);
            var profile = new CrptProfile { Name = "Document status test" };
            await profiles.UpsertAsync(profile, TestContext.Current.CancellationToken);
            var history = new ApiRequestLogRepository(factory);
            var handler = new TrueApiDocumentStatusHandler(new MockTrueApiClient());
            var executor = new ExecuteApiOperationService(new ApiOperationRegistry([handler]), profiles, history, new SensitiveDataMasker());
            var result = await executor.ExecuteAsync(handler.Descriptor.Id, new Dictionary<string, string> { ["documentId"] = "demo-error" }, profile.Id, TestContext.Current.CancellationToken);
            Assert.True(result.Success);
            Assert.Equal("PARSE_ERROR", Assert.Single(DocumentStatusResults.Parse(result.ResponseBody)!.Items).Status);
            var row = Assert.Single(await history.ListAsync(profile.Id, 10, TestContext.Current.CancellationToken));
            Assert.Equal("GET", row.HttpMethod);
            Assert.Equal(result.CorrelationId, row.CorrelationId);
            Assert.Equal(result.RequestUrl, row.Url);
            Assert.True(row.DurationMs > 0);
            Assert.Contains("Bearer ********", row.RequestHeaders!);
            Assert.Contains("X-CRPT-Workbench-Mock", row.ResponseHeaders!);
            Assert.DoesNotContain("mock-token-not-a-real-secret", string.Join("\n", row.RequestHeaders, row.ResponseHeaders, row.ResponseBody, row.ExchangesJson));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 1)]
    [InlineData(HttpStatusCode.Unauthorized, 2)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData(HttpStatusCode.NotFound, 1)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    public async Task RealClientRetriesOnlyUnauthorized(HttpStatusCode status, int calls)
    {
        var handler = new CountingHandler(status);
        var tokens = new FakeTokenProvider();
        var client = new RealTrueApiClient(new StaticFactory(handler), tokens,
            Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production }));
        var request = new IntegrationRequest(TrueApiDocumentStatusHandler.OperationId, "GET", "doc/demo-success/info?body=false&content=false",
            new Dictionary<string, string> { ["X-Correlation-Id"] = "document-correlation" }, null, "document-correlation",
            new IntegrationAuthenticationContext(Guid.NewGuid(), "7700000000", "thumbprint"));
        var result = await client.ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(calls, handler.Calls);
        Assert.Equal(status, (HttpStatusCode)result.StatusCode);
        Assert.Equal(calls - 1, tokens.Invalidations);
        Assert.Contains("/api/v4/true-api/doc/demo-success/info?body=false&content=false", result.RequestUrl);
    }

    private sealed class CapturingClient : ITrueApiClient
    {
        public Uri BaseAddress => new("https://mock.invalid/");
        public IntegrationRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request; Token = cancellationToken;
            return Task.FromResult(new IntegrationExchange(200, request.Method, request.Url, request.Headers, null, new Dictionary<string, string>(), "[]", TimeSpan.Zero));
        }
    }
    private sealed class ConstantHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
    }
    private sealed class CountingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        }
    }
    private sealed class StaticFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri(CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/") };
    }
    private sealed class FakeTokenProvider : ITrueApiTokenProvider
    {
        public int Invalidations { get; private set; }
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId, CancellationToken cancellationToken = default) => Task.FromResult("secret-test-token");
        public void Invalidate(IntegrationAuthenticationContext authentication) => Invalidations++;
    }
    private sealed class TestDbFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    {
        public WorkbenchDbContext CreateDbContext() => new(options);
    }
}
