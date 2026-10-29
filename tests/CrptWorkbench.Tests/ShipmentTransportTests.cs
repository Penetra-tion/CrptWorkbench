using System.Net;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class ShipmentTransportTests
{
    private const string Metadata = "doc/test-document/info?body=false&content=false";
    private const string Body = "doc/test-document/info?body=true&content=false&limit=36000&pageNumber=1";
    private static IntegrationRequest Request(string url, string method = "GET", string? body = null) =>
        new("true-api.shipment-check", method, url, new Dictionary<string, string> { ["X-Correlation-Id"] = "shipment", ["Vendor-Secret"] = "hidden-secret" }, body, "shipment",
            new IntegrationAuthenticationContext(Guid.Parse("11111111-1111-1111-1111-111111111111"), "7700000000", "thumbprint"));

    [Theory]
    [InlineData("UNIVERSAL_TRANSFER_DOCUMENT")]
    [InlineData("UNIVERSAL_TRANSFER_DOCUMENT_FIX")]
    [InlineData("UNIVERSAL_CORRECTION_DOCUMENT")]
    [InlineData("UNIVERSAL_CORRECTION_DOCUMENT_FIX")]
    [InlineData("FIXATION")]
    [InlineData("FIXATION_CANCEL")]
    [InlineData("UNIVERSAL_CANCEL_DOCUMENT")]
    public async Task BodyRequiresSuccessfulTypeCheckAndKeepsSecretsOutOfExchangeHeaders(string type)
    {
        var handler = new Handler(type); var tokens = new Tokens(); var client = Client(handler, tokens);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Request(Body), TestContext.Current.CancellationToken));
        Assert.Equal(0, tokens.Calls);
        await client.ExecuteAsync(Request(Metadata), TestContext.Current.CancellationToken);
        var result = await client.ExecuteAsync(Request(Body), TestContext.Current.CancellationToken);
        Assert.Contains("private-document-body", result.ResponseBody);
        Assert.DoesNotContain("secret", string.Join(";", result.RequestHeaders.Values.Concat(result.ResponseHeaders.Values)));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("EMISSION")]
    [InlineData("")]
    public async Task UnapprovedTypeNeverAuthorizesBody(string type)
    {
        var handler = new Handler(type); var client = Client(handler, new Tokens());
        await client.ExecuteAsync(Request(Metadata), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Request(Body), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("doc/x%2Fy/info?body=false&content=false", "GET", null)]
    [InlineData("doc/test-document/info?body=true&content=true&limit=36000&pageNumber=1", "GET", null)]
    [InlineData("doc/test-document/info?body=false&content=false", "POST", null)]
    [InlineData("doc/test-document/info?body=false&content=false", "GET", "{}")]
    [InlineData("doc/test-document/info?body=true&content=false&limit=36001&pageNumber=1", "GET", null)]
    [InlineData("doc/test-document/info?body=true&content=false&limit=36000&pageNumber=0", "GET", null)]
    [InlineData("https://evil.invalid/doc/test-document/info?body=false&content=false", "GET", null)]
    public async Task InvalidRequestRejectedBeforeAuthentication(string url, string method, string? body)
    {
        var tokens = new Tokens(); var client = Client(new Handler("FIXATION"), tokens);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Request(url, method, body), TestContext.Current.CancellationToken));
        Assert.Equal(0, tokens.Calls);
    }

    [Fact]
    public async Task TypeProofCannotCrossProfilesOrDocumentIds()
    {
        var handler = new Handler("FIXATION"); var client = Client(handler, new Tokens());
        await client.ExecuteAsync(Request(Metadata), TestContext.Current.CancellationToken);
        var request = Request(Body);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(request with { Authentication = request.Authentication! with { ProfileId = Guid.NewGuid() } }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Request(Body.Replace("test-document", "other")), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ShipmentHistoryUsesExistingExactReadContract()
    {
        var handler = new Handler("FIXATION"); var client = Client(handler, new Tokens());
        var url = CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/cises/history?cis=010460000000000021DEMO000000001";
        var result = await client.ExecuteAsync(Request(url, "POST"), TestContext.Current.CancellationToken);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NetworkGuardDoesNotPermitUnprovenBodyReads()
    {
        using var handler = new ProductionTrueApiNetworkGuardHandler { InnerHandler = new Handler("FIXATION") };
        using var invoker = new HttpMessageInvoker(handler);
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://markirovka.crpt.ru/api/v4/true-api/" + Body);
        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(message, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedFreshMetadataRevokesPreviousProof()
    {
        var handler = new Handler("FIXATION"); var client = Client(handler, new Tokens());
        await client.ExecuteAsync(Request(Metadata), TestContext.Current.CancellationToken);
        handler.Status = HttpStatusCode.NotFound;
        await client.ExecuteAsync(Request(Metadata), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Request(Body), TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task FullLoggingNeverIncludesShipmentBodyCodeOrSecretHeaders()
    {
        var logger = new Logger();
        using var config = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WorkbenchLogging:HttpDetailsEnabled"] = "true" }).Build();
        using var logging = new LoggingHandler(logger, config) { InnerHandler = new Handler("FIXATION") };
        using var invoker = new HttpMessageInvoker(logging);
        using var message = new HttpRequestMessage(HttpMethod.Post, CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/cises/history?cis=never-log-this-code");
        message.Headers.Add("Vendor-Secret", "never-log-this-secret");
        using var response = await invoker.SendAsync(message, TestContext.Current.CancellationToken);
        var text = string.Join("\n", logger.Messages);
        Assert.DoesNotContain("never-log-this", text);
        Assert.DoesNotContain("response-secret", text);
        Assert.Contains("<body omitted>", text);
    }

    private sealed class Logger : ILogger<LoggingHandler>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static RealTrueApiClient Client(Handler handler, Tokens tokens) => new(new Factory(handler), tokens,
        Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production }));
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ProductionTrueApiNetworkGuardHandler { InnerHandler = handler }, false);
    }
    private sealed class Handler(string type) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(Status) { Content = new StringContent(request.RequestUri!.Query.Contains("body=true")
                ? "[{\"body\":{\"private\":\"private-document-body\"}}]" : "[{\"type\":\"" + type + "\"}]") };
            response.Headers.Add("Vendor-Secret", "response-secret");
            return Task.FromResult(response);
        }
    }
    private sealed class Tokens : ITrueApiTokenProvider
    {
        public int Calls { get; private set; }
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult("token-secret"); }
        public void Invalidate(IntegrationAuthenticationContext authentication) { }
    }
}
