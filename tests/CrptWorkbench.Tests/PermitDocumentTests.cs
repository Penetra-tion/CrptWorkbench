using System.Net;
using System.Text;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class PermitDocumentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("RU С-CN.РА08.В.93658/26", PermitNumberKind.Certificate, "RU")]
    [InlineData("ЕАЭС N RU Д-CN.РА08.В.93658/26", PermitNumberKind.Declaration, "RU")]
    [InlineData("ТС № KG417/028.Д.0000000", PermitNumberKind.Ambiguous, "KG")]
    [InlineData("BY.50.51.01.008.E.000911.10.25", PermitNumberKind.StateRegistration, "BY")]
    public void ClassifiesDocumentNumbers(string input, PermitNumberKind kind, string country)
    {
        var parsed = PermitDocumentNumbers.Parse("\u00a0" + input + "\u00a0");
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(country, parsed.Country);
        Assert.Equal(input, parsed.Value);
    }

    [Theory]
    [InlineData("RU")]
    [InlineData("BY")]
    [InlineData("KZ")]
    [InlineData("AM")]
    [InlineData("KG")]
    public void AcceptsSgrNumbersForEveryEaeuCountry(string country)
    {
        var parsed = PermitDocumentNumbers.Parse($"{country}.50.51.01.008.E.000911.10.25");
        Assert.Equal(PermitNumberKind.StateRegistration, parsed.Kind);
        Assert.Equal(country, parsed.Country);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RU С-CN.РА08.В.93658/26&x=1")]
    [InlineData("BY.50.51.01.008.E.000911.13.25")]
    [InlineData("https://evil.example/")]
    public void RejectsInvalidNumbers(string value) => Assert.Throws<ArgumentException>(() => PermitDocumentNumbers.Parse(value));

    [Fact]
    public void GuardAllowsOnlySgrRead()
    {
        var number = "BY.50.51.01.008.E.000911.10.25";
        using var allowed = new HttpRequestMessage(HttpMethod.Get, PermitDocumentTransport.SgrUrl(number));
        Assert.True(PermitDocumentTransport.IsAllowed(allowed));
        foreach (var url in new[] { PermitDocumentTransport.SgrUrl(number).Replace("https:", "http:"),
            PermitDocumentTransport.SgrUrl(number).Replace("nsi.eaeunion.org", "evil.example"),
            PermitDocumentTransport.SgrUrl(number) + "&extra=1",
            "https://nsi.eaeunion.org/api/v1/dictionaries/1995/update" })
        {
            using var blocked = new HttpRequestMessage(HttpMethod.Get, url);
            Assert.False(PermitDocumentTransport.IsAllowed(blocked));
        }
        using var post = new HttpRequestMessage(HttpMethod.Post, allowed.RequestUri);
        Assert.False(PermitDocumentTransport.IsAllowed(post));
        using var eaeu = new HttpRequestMessage(HttpMethod.Post, PermitDocumentTransport.EaeuUrl);
        Assert.True(PermitDocumentTransport.IsAllowed(eaeu));
        using var eaeuOther = new HttpRequestMessage(HttpMethod.Post, PermitDocumentTransport.EaeuUrl + "&extra=1");
        Assert.False(PermitDocumentTransport.IsAllowed(eaeuOther));
    }

    [Fact]
    public async Task EaeuGuardRejectsUnexpectedSearchBodyBeforeNetwork()
    {
        var called = false;
        using var client = new HttpClient(new PermitDocumentNetworkGuardHandler
        {
            InnerHandler = new FakeHandler(_ =>
            {
                called = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            })
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, PermitDocumentTransport.EaeuUrl)
        {
            Content = new StringContent("{\"$where\":\"anything\"}", Encoding.UTF8, "text/plain")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, Ct));
        Assert.False(called);
    }

    [Fact]
    public async Task MockAndHistoryShowStructuredCard()
    {
        var response = await new PermitDocumentLookupHandler(new MockPermitDocumentClient()).ExecuteAsync(
            new Dictionary<string, string> { ["number"] = PermitDocumentLookupHandler.ExampleNumber },
            new(new CrptProfile(), "correlation"), Ct);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("true", response.ResponseHeaders["X-CRPT-Workbench-Mock"]);
        var result = Assert.IsType<PermitDocumentResult>(PermitDocumentResult.Parse(response.ResponseBody));
        Assert.Equal("found", result.State);
        Assert.Single(result.Items);
        var saved = SavedResultView.From(new ApiRequestLog { OperationId = PermitDocumentLookupHandler.OperationId,
            ResponseBody = response.ResponseBody, Success = true });
        Assert.Equal(result.Number, saved.PermitDocument?.Number);
    }

    [Fact]
    public async Task LookupHistoryIsProfileScopedAndMasksUnexpectedSecrets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crpt-permit-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var factory = new TestDbFactory(options);
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync(Ct);
            var profiles = new ProfileRepository(factory);
            var active = new CrptProfile { Name = "Permit active" };
            var other = new CrptProfile { Name = "Permit other" };
            await profiles.UpsertAsync(active, Ct);
            await profiles.UpsertAsync(other, Ct);
            var history = new ApiRequestLogRepository(factory);
            var handler = new PermitDocumentLookupHandler(new SecretBearingClient());
            var executor = new ExecuteApiOperationService(new ApiOperationRegistry([handler]), profiles, history,
                new SensitiveDataMasker());
            var result = await executor.ExecuteAsync(PermitDocumentLookupHandler.OperationId,
                new Dictionary<string, string> { ["number"] = PermitDocumentLookupHandler.ExampleNumber }, active.Id, Ct);
            Assert.True(result.Success);
            var row = Assert.Single(await history.ListAsync(active.Id, 10, Ct));
            Assert.Empty(await history.ListAsync(other.Id, 10, Ct));
            Assert.Null(await history.GetAsync(other.Id, row.Id, Ct));
            Assert.Equal(result.CorrelationId, row.CorrelationId);
            Assert.Equal("found", SavedResultView.From(row).PermitDocument?.State);
            var stored = string.Join("\n", row.RequestHeaders, row.ResponseHeaders, row.ResponseBody, row.ExchangesJson);
            Assert.Contains("Bearer ********", stored);
            Assert.DoesNotContain("original-secret", stored);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task SgrResultRequiresExactNumberAndKeepsOnlyRequestedFields()
    {
        const string number = "BY.50.51.01.008.E.000911.10.25";
        var body = """
            {"elements":[{"id":"b19cce42-d3fd-4efb-968d-8ac3a0bcba86","data":{"NUMB_DOC":"BY.50.51.01.008.Е.000911.10.25","STATUS":{"name":"подписан и действует"},"DATE_DOC":"2025-10-15","NAME_PROD":"Отвердитель","FIRMMADE_NAME":"Изготовитель","FIRMGET_NAME":"Получатель","DOC_NORM":"ТР ТС","PRIVATE":"do not save"}},{"id":"ca3222e9-0f96-4d18-a66f-86d990ce5369","data":{"NUMB_DOC":"BY.50.51.01.008.E.000912.10.25","STATUS":{"name":"аннулирован"}}}]}
            """;
        var factory = new FakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var response = await new RealPermitDocumentClient(factory).ExecuteAsync(Request(number), Ct);
        var result = Assert.IsType<PermitDocumentResult>(PermitDocumentResult.Parse(response.ResponseBody));
        Assert.Equal("found", result.State);
        Assert.Single(result.Items);
        Assert.Equal("подписан и действует", result.Items[0].Status);
        Assert.DoesNotContain("do not save", response.ResponseBody);
        Assert.All(factory.Urls, url => Assert.StartsWith(PermitDocumentTransport.SgrBase, url));
    }

    [Fact]
    public async Task ChangedResponseAndUnconfirmedRoutesNeverClaimAbsence()
    {
        var factory = new FakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var real = new RealPermitDocumentClient(factory);
        var sgr = await real.ExecuteAsync(Request("BY.50.51.01.008.E.000911.10.25"), Ct);
        Assert.Equal(503, sgr.StatusCode);
        Assert.Equal("unavailable", PermitDocumentResult.Parse(sgr.ResponseBody)?.State);
        var certificate = await real.ExecuteAsync(Request("RU С-CN.РА08.В.93658/26"), Ct);
        Assert.Equal(503, certificate.StatusCode);
        Assert.DoesNotContain(factory.Urls, x => x.Contains("fsa.gov.ru", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RussianSearchUsesOnlyTheApprovedEaeuRegistry()
    {
        var factory = new FakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"result\":[],\"matchedDocuments\":0,\"responseSize\":0}")
        });
        foreach (var number in new[] { "RU С-CN.РА08.В.93658/26", "RU Д-CN.РА08.В.93658/26" })
        {
            var response = await new RealPermitDocumentClient(factory).ExecuteAsync(Request(number), Ct);
            Assert.Equal(404, response.StatusCode);
            var result = Assert.IsType<PermitDocumentResult>(PermitDocumentResult.Parse(response.ResponseBody));
            Assert.Equal("not_found", result.State);
            Assert.StartsWith("https://tech.eaeunion.org/", result.ManualUrl);
        }
        Assert.Equal(2, factory.Urls.Count);
        Assert.All(factory.Urls, url => Assert.Equal(PermitDocumentTransport.EaeuUrl, url));
    }

    [Fact]
    public async Task EaeuOpenApiResolvesKindFromMatchedRecord()
    {
        const string number = "ТС № KG417/028.Д.0000000";
        var body = """
            {"result":[{"docId":"ТС № KG417/028.Д.0000000","unifiedCountryCode":{"value":"KG"},"conformityDocKindName":"декларация о соответствии ТР ЕАЭС","docStartDate":{"$date":"2025-01-01T00:00:00Z"},"docValidityDate":{"$date":"2028-01-01T00:00:00Z"},"docStatusDetails":{"docStatusCode":"01"},"applicantDetails":{"businessEntityName":"Заявитель"},"technicalRegulationObjectDetails":{"productDetails":[{"productName":"Товар"}],"manufacturerDetails":[{"businessEntityName":"Завод"}]},"technicalRegulationId":["ТР ТС 001/2011"],"privateData":"discard"},{"docId":"ТС № KG417/028.Д.0000001","conformityDocKindName":"сертификат соответствия"}],"matchedDocuments":2,"responseSize":2}
            """;
        var factory = new FakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var response = await new RealPermitDocumentClient(factory).ExecuteAsync(Request(number), Ct);
        Assert.Equal(200, response.StatusCode);
        var result = Assert.IsType<PermitDocumentResult>(PermitDocumentResult.Parse(response.ResponseBody));
        Assert.Equal("found", result.State);
        var item = Assert.Single(result.Items);
        Assert.Equal("Декларация о соответствии", item.Type);
        Assert.Equal("KG", item.Country);
        Assert.Equal("Товар", item.Product);
        Assert.DoesNotContain("discard", response.ResponseBody);
        Assert.Equal(PermitDocumentTransport.EaeuUrl, Assert.Single(factory.Urls));
    }

    [Fact]
    public async Task EaeuIncompletePageDoesNotClaimNotFound()
    {
        var body = """{"result":[],"matchedDocuments":1,"responseSize":0}""";
        var factory = new FakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        var response = await new RealPermitDocumentClient(factory).ExecuteAsync(Request("ТС № KG417/028.Д.0000000"), Ct);
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("unavailable", PermitDocumentResult.Parse(response.ResponseBody)?.State);
    }

    private static IntegrationRequest Request(string number) => new(PermitDocumentLookupHandler.OperationId, "GET",
        "https://nsi.eaeunion.org/portal/1995/card/", new Dictionary<string, string>
        {
            ["X-Permit-Number"] = number, ["X-Correlation-Id"] = "c"
        }, null, "c");

    private sealed class FakeFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public List<string> Urls { get; } = [];
        public HttpClient CreateClient(string name) => new(new FakeHandler(request =>
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return response(request);
        }));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class SecretBearingClient : IPermitDocumentClient
    {
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            var headers = new Dictionary<string, string>(request.Headers) { ["Authorization"] = "Bearer original-secret" };
            var result = new PermitDocumentResult(PermitDocumentLookupHandler.ExampleNumber, "found", DateTimeOffset.UtcNow,
                [new PermitDocumentItem("СГР", PermitDocumentLookupHandler.ExampleNumber, "RU", "Действует", null,
                    null, null, null, null, null, "Локальный пример", "https://nsi.eaeunion.org/portal/1995/card/")],
                null, "https://nsi.eaeunion.org/portal/1995/card/");
            var body = System.Text.Json.JsonSerializer.Serialize(result);
            body = body[..^1] + ",\"token\":\"original-secret\"}";
            return Task.FromResult(new IntegrationExchange(200, "GET", request.Url, headers, null,
                new Dictionary<string, string> { ["X-CRPT-Workbench-Mock"] = "true" }, body, TimeSpan.Zero));
        }
    }

    private sealed class TestDbFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    {
        public WorkbenchDbContext CreateDbContext() => new(options);
    }
}
