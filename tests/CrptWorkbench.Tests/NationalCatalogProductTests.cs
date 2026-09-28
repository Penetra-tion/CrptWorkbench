using System.Net;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class NationalCatalogProductTests
{
    [Fact]
    public async Task ProductionLookupUsesSharedTrueApiTokenAndRetriesOnceAfterUnauthorized()
    {
        var issuedTokens = new List<string>();
        var provider = new RotatingTokenProvider();
        using var transport = new CallbackHandler(request =>
        {
            issuedTokens.Add(request.Headers.Authorization?.Parameter ?? string.Empty);
            return new HttpResponseMessage(issuedTokens.Count == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        });
        var client = new RealNationalCatalogClient(new NationalCatalogHttpClientFactory(transport), provider,
            Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production }));
        var authentication = new IntegrationAuthenticationContext(Guid.NewGuid(), "7700000000", new string('A', 40));
        var request = new IntegrationRequest(NationalCatalogProductHandler.OperationId, "GET",
            $"{client.BaseAddress}v3/feed-product?gtin=12345678&format=json",
            new Dictionary<string, string> { ["X-Correlation-Id"] = "catalog-refresh" }, null, "catalog-refresh", authentication);

        var exchange = await client.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal(["token-1", "token-2"], issuedTokens);
        Assert.Equal(1, provider.Invalidations);
        Assert.Equal("Bearer token-2", exchange.RequestHeaders["Authorization"]);
        Assert.Equal("catalog-refresh", exchange.RequestHeaders["X-Correlation-Id"]);
    }

    [Fact]
    public async Task ProductionLookupStopsAfterSecondUnauthorized()
    {
        var provider = new RotatingTokenProvider();
        var calls = 0;
        using var transport = new CallbackHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
        });
        var client = new RealNationalCatalogClient(new NationalCatalogHttpClientFactory(transport), provider,
            Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production }));
        var request = new IntegrationRequest(NationalCatalogProductHandler.OperationId, "GET",
            $"{client.BaseAddress}v3/feed-product?gtin=12345678&format=json",
            new Dictionary<string, string>(), null, "catalog-refresh",
            new IntegrationAuthenticationContext(Guid.NewGuid(), "7700000000", new string('A', 40)));

        var exchange = await client.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(401, exchange.StatusCode);
        Assert.Equal(2, calls);
        Assert.Equal(1, provider.Invalidations);
    }

    [Fact]
    public async Task MockPublishedCardReturnsFriendlyFieldsPackagesDocumentsAndTwoExchanges()
    {
        var handler = new NationalCatalogProductHandler(new MockNationalCatalogClient());
        var exchange = await handler.ExecuteAsync(
            new Dictionary<string, string> { ["gtin"] = "04600000000000" },
            new(new CrptProfile(), "national-catalog-mock"),
            TestContext.Current.CancellationToken);

        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal(2, exchange.Exchanges?.Count);
        var result = JsonSerializer.Deserialize<NationalCatalogProductResult>(exchange.ResponseBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result);
        Assert.Equal("04600000000000", result.Gtin);
        Assert.Equal("1905319900", result.TnVed);
        Assert.Equal("Сладости и кондитерские изделия", result.ProductGroup);
        Assert.Equal("Опубликована", result.CardStatus);
        Assert.Equal(3, result.Packages.Count);
        Assert.Equal(2, result.Documents.Count);
        Assert.All(exchange.Exchanges!, item => Assert.Equal("true", item.ResponseHeaders["X-CRPT-Workbench-Mock"]));
    }

    [Theory]
    [InlineData("1234567")]
    [InlineData("12345678901A")]
    [InlineData("123456789012345")]
    public async Task InvalidGtinIsRejectedBeforeTransport(string gtin)
    {
        var client = new QueueNationalCatalogClient([]);
        var handler = new NationalCatalogProductHandler(client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(
            new Dictionary<string, string> { ["gtin"] = gtin }, new(new(), "invalid"), TestContext.Current.CancellationToken));
        Assert.Contains("8, 12, 13 или 14", error.Message);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task DraftCardUsesAttributeDocumentsAndKeepsUnknownStatusVisible()
    {
        var card = Exchange("GET", "/v3/feed-product?gtin=12345678&format=json", """
            {"result":[{"good_name":"Черновик","good_status":"custom-review","identified_by":[{"value":"12345678","type":"gtin","multiplier":1,"level":"trade-unit"}],"good_attrs":[{"attr_id":3959,"attr_value":"3303"},{"attr_id":23768,"attr_value":"4"},{"attr_id":23557,"attr_value":"ЕАЭС N RU Д-TEST"}]}]}
            """);
        var handler = new NationalCatalogProductHandler(new QueueNationalCatalogClient([card]));
        var exchange = await handler.ExecuteAsync(new Dictionary<string, string> { ["gtin"] = "12345678" }, new(new(), "draft"), TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<NationalCatalogProductResult>(exchange.ResponseBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("custom-review", result.CardStatus);
        Assert.Equal("3303", result.TnVed);
        Assert.Equal("Духи и туалетная вода", result.ProductGroup);
        Assert.Single(result.Documents);
        Assert.Single(exchange.Exchanges!);
    }

    [Fact]
    public async Task CardCategoryIsShownWhenProductGroupAttributeIsMissing()
    {
        var card = Exchange("GET", "/v3/feed-product?gtin=4607167628274&format=json", """
            {"result":[{"good_name":"Вода питьевая","good_status":"draft","categories":[{"cat_id":123,"cat_name":"Воды питьевые и минеральные"}],"good_attrs":[]}]}
            """);
        var handler = new NationalCatalogProductHandler(new QueueNationalCatalogClient([card]));

        var exchange = await handler.ExecuteAsync(new Dictionary<string, string> { ["gtin"] = "4607167628274" },
            new(new(), "category"), TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<NationalCatalogProductResult>(exchange.ResponseBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("Воды питьевые и минеральные", result.ProductGroup);
    }

    [Fact]
    public async Task DocumentFailureKeepsCardAndAddsWarning()
    {
        var card = Exchange("GET", "/v3/feed-product?gtin=123456789012&format=json", """
            {"result":[{"good_name":"Товар","good_status":"published","producer_inn":"7700000000","identified_by":[],"good_attrs":[]}]}
            """);
        var failedDocuments = Exchange("POST", "/v4/rd-info-by-gtin", "{}", 503);
        var handler = new NationalCatalogProductHandler(new QueueNationalCatalogClient([card, failedDocuments]));
        var exchange = await handler.ExecuteAsync(new Dictionary<string, string> { ["gtin"] = "123456789012" }, new(new(), "partial"), TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<NationalCatalogProductResult>(exchange.ResponseBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(200, exchange.StatusCode);
        Assert.Contains("HTTP 503", result.Warning);
        Assert.Equal(2, exchange.Exchanges!.Count);
    }

    [Fact]
    public async Task ExecutorPersistsOneMaskedHistoryRowForCompoundLookup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"crpt-national-catalog-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var factory = new TestDbFactory(options);
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync(cancellationToken);
            var profiles = new ProfileRepository(factory);
            var profile = new CrptProfile { Name = "National Catalog test" };
            await profiles.UpsertAsync(profile, cancellationToken);
            var history = new ApiRequestLogRepository(factory);
            var handler = new NationalCatalogProductHandler(new MockNationalCatalogClient());
            var executor = new ExecuteApiOperationService(new ApiOperationRegistry([handler]), profiles, history, new SensitiveDataMasker());

            var result = await executor.ExecuteAsync(handler.Descriptor.Id, new Dictionary<string, string> { ["gtin"] = "04600000000000" }, profile.Id, cancellationToken);
            var row = Assert.Single(await history.ListAsync(profile.Id, 10, cancellationToken));
            Assert.True(result.Success);
            Assert.Equal(result.CorrelationId, row.CorrelationId);
            Assert.Contains("X-CRPT-Workbench-Mock", row.ExchangesJson!);
            Assert.Contains("Bearer ********", row.ExchangesJson!);
            Assert.DoesNotContain("mock-token-not-a-real-secret", row.ExchangesJson!);
            Assert.Equal(2, JsonSerializer.Deserialize<ApiHttpExchange[]>(row.ExchangesJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("GET", "https://апи.национальный-каталог.рф/v3/feed-product?gtin=12345678&format=json", true)]
    [InlineData("POST", "https://апи.национальный-каталог.рф/v4/rd-info-by-gtin", true)]
    [InlineData("GET", "https://апи.национальный-каталог.рф/v4/rd-info-by-gtin", false)]
    [InlineData("GET", "http://апи.национальный-каталог.рф/v3/feed-product?gtin=12345678&format=json", false)]
    [InlineData("GET", "https://апи.национальный-каталог.рф:444/v3/feed-product?gtin=12345678&format=json", false)]
    [InlineData("GET", "https://example.org/v3/feed-product?gtin=12345678&format=json", false)]
    [InlineData("GET", "https://апи.национальный-каталог.рф/v3/feed-product?gtin=12345678&format=json&extra=1", false)]
    public async Task ProductionGuardAllowsOnlyApprovedRequests(string method, string url, bool allowed)
    {
        using var guard = new ProductionNationalCatalogNetworkGuardHandler { InnerHandler = new ConstantHandler(HttpStatusCode.OK, "{}") };
        using var invoker = new HttpMessageInvoker(guard);
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (allowed)
        {
            using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
        }
    }

    private static IntegrationExchange Exchange(string method, string path, string body, int status = 200) => new(
        status, method, "https://production.nationalcatalog.mock.invalid" + path,
        new Dictionary<string, string>(), null,
        new Dictionary<string, string> { ["Content-Type"] = "application/json" }, body, TimeSpan.FromMilliseconds(1));

    private sealed class QueueNationalCatalogClient(IEnumerable<IntegrationExchange> exchanges) : INationalCatalogClient
    {
        private readonly Queue<IntegrationExchange> _exchanges = new(exchanges);
        public Uri BaseAddress { get; } = new("https://production.nationalcatalog.mock.invalid");
        public int CallCount { get; private set; }
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_exchanges.Dequeue());
        }
    }

    private sealed class RotatingTokenProvider : ITrueApiTokenProvider
    {
        private int _issued;
        public int Invalidations { get; private set; }
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult($"token-{Interlocked.Increment(ref _issued)}");
        public void Invalidate(IntegrationAuthenticationContext authentication) => Invalidations++;
    }

    private sealed class NationalCatalogHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(CrptIntegrationOptions.ProductionNationalCatalogBaseUrl.TrimEnd('/') + "/")
        };
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }

    private sealed class ConstantHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
    }

    private sealed class TestDbFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    {
        public WorkbenchDbContext CreateDbContext() => new(options);
    }
}

internal sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }
}
