using System.Net;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class PackageCreationTransportTests
{
    private static IntegrationRequest Request(string operation = "true-api.package-aggregation", string group = "milk",
        string type = "AGGREGATION_DOCUMENT", string format = "MANUAL", string document = "e30=", string signature = "signature") =>
        new(operation, "POST", $"lk/documents/create?pg={group}", new Dictionary<string, string>(),
            JsonSerializer.Serialize(new { type, document_format = format, product_document = document, signature }),
            "package-correlation", new(Guid.NewGuid(), "7700000000", new string('A', 40)));

    [Theory]
    [InlineData("true-api.package-aggregation", "AGGREGATION_DOCUMENT")]
    [InlineData("true-api.package-aggregation", "SETS_AGGREGATION")]
    [InlineData("true-api.package-transformation", "REAGGREGATION_DOCUMENT")]
    public async Task ApprovedCreationPreservesSignedEnvelope(string operation, string type)
    {
        var request = Request(operation, type: type);
        using var factory = new Factory();
        var tokens = new Tokens();
        var result = await Client(factory, tokens).ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(201, result.StatusCode);
        Assert.Equal(request.Body, factory.Body);
        Assert.Equal($"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/lk/documents/create?pg=milk", factory.Url);
        Assert.Equal(1, tokens.Calls);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task InvalidCreationAndRawBypassFailBeforeAuthentication()
    {
        var valid = Request();
        IntegrationRequest[] invalid =
        [
            Request(type: "REAGGREGATION_DOCUMENT"),
            Request(type: "SETS_AGGREGATION", group: "shoes"),
            Request(type: "SETS_AGGREGATION_XML"),
            Request(operation: "true-api.package-transformation", type: "SETS_AGGREGATION"),
            Request(operation: "true-api.package-transformation"),
            Request(operation: "true-api.code-info"), Request(operation: "raw"),
            Request(group: "unknown"), Request(group: "milk&pg=shoes"), Request(group: ""),
            Request(format: "XML"), Request(document: "not-base64"), Request(document: ""),
            Request(document: Convert.ToBase64String(Encoding.UTF8.GetBytes("not-json"))),
            Request(signature: "  "), valid with { Method = "GET" }, valid with { Body = null },
            valid with { Url = $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/lk/documents/create?pg=milk" },
            valid with { Url = "lk/documents/create?pg=milk#fragment" },
            valid with { Body = "{}" }
        ];
        foreach (var request in invalid)
        {
            using var factory = new Factory();
            var tokens = new Tokens();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Client(factory, tokens).ExecuteAsync(request, TestContext.Current.CancellationToken));
            Assert.Equal(0, tokens.Calls);
            Assert.Equal(0, factory.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousCreationIsNotRetried(bool timeout)
    {
        using var factory = new Factory { Timeout = timeout, Status = HttpStatusCode.ServiceUnavailable };
        var tokens = new Tokens();
        var client = Client(factory, tokens);
        if (timeout) await Assert.ThrowsAsync<TaskCanceledException>(() => client.ExecuteAsync(Request(), TestContext.Current.CancellationToken));
        else Assert.Equal(503, (await client.ExecuteAsync(Request(), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, tokens.Calls);
    }

    [Theory]
    [InlineData("true-api.package-aggregation", "SETS_AGGREGATION", "lp", "000000000000000001", "CHECKED_OK")]
    [InlineData("true-api.package-aggregation", "SETS_AGGREGATION", "milk", "999999999999999999", "CHECKED_NOT_OK")]
    [InlineData("true-api.package-aggregation", "AGGREGATION_DOCUMENT", "shoes", "000000000000000001", "CHECKED_OK")]
    [InlineData("true-api.package-aggregation", "AGGREGATION_DOCUMENT", "water", "999999999999999999", "CHECKED_NOT_OK")]
    [InlineData("true-api.package-transformation", "REAGGREGATION_DOCUMENT", "lp", "000000000000000001", "CHECKED_OK")]
    public async Task MockMetadataAndErrorsSurviveClientRecreation(string operation, string type, string group, string package, string status)
    {
        var document = type is "AGGREGATION_DOCUMENT" or "SETS_AGGREGATION"
            ? JsonSerializer.Serialize(new { participantId = "7700000000", aggregationUnits = new[] { new { unitSerialNumber = package, aggregationType = "AGGREGATION", sntins = new[] { "010460000000000021DEMO000000001" } } } })
            : JsonSerializer.Serialize(new { uitu = package });
        var request = Request(operation, group, type, document: Convert.ToBase64String(Encoding.UTF8.GetBytes(document)));
        var exchange = await new MockTrueApiClient().ExecuteAsync(request, TestContext.Current.CancellationToken);
        var id = JsonSerializer.Deserialize<string>(exchange.ResponseBody!)!;
        Assert.True(Guid.TryParse(id, out _));
        var second = await new MockTrueApiClient().ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.NotEqual(exchange.ResponseBody, second.ResponseBody);
        var polled = await new MockTrueApiClient().ExecuteAsync(new("true-api.document-status", "GET",
            $"doc/{id}/info?body=false&content=false", new Dictionary<string, string>(), null, "status-correlation"), TestContext.Current.CancellationToken);
        using var response = JsonDocument.Parse(polled.ResponseBody!);
        var item = response.RootElement[0];
        Assert.Equal(type, item.GetProperty("type").GetString());
        Assert.Equal(group, item.GetProperty("productGroup")[0].GetString());
        Assert.Equal(status, item.GetProperty("status").GetString());
        Assert.Equal(status == "CHECKED_NOT_OK" ? 1 : 0, item.GetProperty("errors").GetArrayLength());
        Assert.Equal("true", polled.ResponseHeaders["X-CRPT-Workbench-Mock"]);
    }

    [Fact]
    public async Task ProductionGuardRejectsUnreviewedProductGroup()
    {
        using var guard = new ProductionTrueApiNetworkGuardHandler { InnerHandler = new Factory.Handler(new Factory()) };
        using var invoker = new HttpMessageInvoker(guard);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/lk/documents/create?pg=unknown");
        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(request, TestContext.Current.CancellationToken));
    }

    private static RealTrueApiClient Client(Factory factory, Tokens tokens) => new(factory, tokens,
        Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production, TrueApiBaseUrl = CrptIntegrationOptions.ProductionTrueApiBaseUrl }));

    [Fact]
    public async Task SignedCreationBodyIsExcludedEvenFromFullTransportLogs()
    {
        var logger = new CaptureLogger();
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Serilog:FullLoggingEnabled"] = "true" }).Build();
        using var factory = new Factory();
        using var handler = new LoggingHandler(logger, configuration) { InnerHandler = new Factory.Handler(factory) };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/lk/documents/create?pg=milk")
            { Content = new StringContent(Request(signature: "never-log-this-signature").Body!) };
        request.Headers.Authorization = new("Bearer", "never-log-this-bearer");
        request.Headers.TryAddWithoutValidation("Cookie", "session=never-log-this-cookie");
        request.Headers.TryAddWithoutValidation("X-Api-Token", "never-log-this-token");
        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
        var text = string.Join("\n", logger.Messages);
        Assert.DoesNotContain("never-log-this-signature", text);
        Assert.DoesNotContain("product_document", text);
        Assert.DoesNotContain("never-log-this-bearer", text);
        Assert.DoesNotContain("never-log-this-cookie", text);
        Assert.DoesNotContain("never-log-this-token", text);
        Assert.DoesNotContain("never-log-response-cookie", text);
        Assert.DoesNotContain("never-log-response-token", text);
        Assert.Contains("Authorization: Bearer ********", text);
        Assert.Contains("Cookie: ********", text);
        Assert.Contains("Set-Cookie: ********", text);
        Assert.Contains("<signed document omitted>", text);
    }

    private sealed class CaptureLogger : ILogger<LoggingHandler>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class Tokens : ITrueApiTokenProvider
    {
        public int Calls { get; private set; }
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult("fake-token"); }
        public void Invalidate(IntegrationAuthenticationContext authentication) { }
    }

    private sealed class Factory : IHttpClientFactory, IDisposable
    {
        private HttpClient? _client;
        public int Calls { get; set; }
        public string? Body { get; set; }
        public string? Url { get; set; }
        public bool Timeout { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.Created;
        public HttpClient CreateClient(string name) => _client ??= new HttpClient(new Handler(this)) { BaseAddress = new(CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/") };
        public void Dispose() => _client?.Dispose();
        public sealed class Handler(Factory factory) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                factory.Calls++;
                factory.Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                factory.Url = request.RequestUri!.AbsoluteUri;
                if (factory.Timeout) throw new TaskCanceledException("Simulated timeout");
                var response = new HttpResponseMessage(factory.Status) { Content = new StringContent(JsonSerializer.Serialize(Guid.NewGuid().ToString())) };
                response.Headers.TryAddWithoutValidation("Set-Cookie", "session=never-log-response-cookie");
                response.Headers.TryAddWithoutValidation("X-Api-Token", "never-log-response-token");
                return response;
            }
        }
    }
}
