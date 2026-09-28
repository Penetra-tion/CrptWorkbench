using System.Net;
using System.Text;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SuzTransportTests
{
    private const string Oms = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Doc = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Path = "api/v3/receipts/receipt?omsId=" + Oms + "&resultDocId=" + Doc;
    internal static readonly IntegrationAuthenticationContext Auth = new(Guid.NewGuid(), "7700000000", new string('A', 40));
    internal static readonly SuzConnectionContext Connection = new(Oms, "separate-connection_1", null);
    internal static IOptions<CrptIntegrationOptions> OptionsFor(CrptIntegrationMode mode = CrptIntegrationMode.Production) => Options.Create(new CrptIntegrationOptions { Mode = mode });
    private static IntegrationRequest Request() => new("suz.document-status", "GET", Path, new Dictionary<string, string>(), null, "suz-correlation", Auth, Connection);

    [Theory]
    [InlineData("GET", "https://suzgrid.crpt.ru/" + Path, true)]
    [InlineData("POST", "https://suzgrid.crpt.ru/" + Path, false)]
    [InlineData("GET", "http://suzgrid.crpt.ru/" + Path, false)]
    [InlineData("GET", "https://suzgrid.crpt.ru:444/" + Path, false)]
    [InlineData("GET", "https://suzgrid.crpt.ru.evil.test/" + Path, false)]
    [InlineData("GET", "https://user@suzgrid.crpt.ru/" + Path, false)]
    [InlineData("GET", "https://suzgrid.crpt.ru/" + Path + "&extra=1", false)]
    [InlineData("GET", "https://suzgrid.crpt.ru/" + Path + "#fragment", false)]
    [InlineData("GET", "https://suzgrid.crpt.ru/api/v3/receipts/receipt?omsId=x&resultDocId=y", false)]
    public async Task ReceiptGuardIsExact(string method, string url, bool allowed)
    {
        using var client = new HttpClient(new ProductionSuzNetworkGuardHandler { InnerHandler = new Handler(_ => Reply(200, "{}")) });
        if (allowed) Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(new(new HttpMethod(method), url), TestContext.Current.CancellationToken)).StatusCode);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new(new HttpMethod(method), url), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("GET", "auth/key", true)]
    [InlineData("POST", "auth/simpleSignIn/separate-connection_1", true)]
    [InlineData("POST", "auth/simpleSignIn", false)]
    [InlineData("POST", "auth/simpleSignIn/a/b", false)]
    [InlineData("POST", "auth/simpleSignIn/a%2Fb", false)]
    [InlineData("POST", "auth/simpleSignIn/a?x=1", false)]
    [InlineData("GET", "cises/info", false)]
    public async Task AuthGuardOnlyAllowsSeparateConnectionFlow(string method, string path, bool allowed)
    {
        using var client = new HttpClient(new SuzAuthenticationNetworkGuardHandler { InnerHandler = new Handler(_ => Reply(200, "{}")) });
        var request = new HttpRequestMessage(new(method), CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/" + path);
        if (allowed) Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(401, 2, 1)]
    [InlineData(500, 1, 0)]
    [InlineData(403, 1, 0)]
    [InlineData(200, 1, 0)]
    public async Task Only401RetriesOnceWithFreshToken(int status, int expectedCalls, int invalidations)
    {
        var calls = 0;
        var tokens = new Tokens();
        var factory = new Factory(new Handler(request => {
            calls++;
            Assert.Equal("suz-correlation", request.Headers.GetValues("X-Correlation-Id").Single());
            Assert.Equal("token-" + calls, request.Headers.GetValues("clientToken").Single());
            Assert.Null(request.Content);
            return Reply(status, "{}");
        }));
        var exchange = await new RealSuzClient(factory, tokens, OptionsFor()).ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(invalidations, tokens.Invalidations);
        Assert.Equal("********", exchange.RequestHeaders["clientToken"]);
        Assert.True(exchange.Duration >= TimeSpan.Zero);
    }

    [Theory]
    [InlineData("https://evil.test/", "GET", null)]
    [InlineData(Path, "POST", null)]
    [InlineData(Path, "GET", "{}")]
    [InlineData("api/v3/receipts/receipt?omsId=cccccccc-cccc-cccc-cccc-cccccccccccc&resultDocId=" + Doc, "GET", null)]
    public async Task InvalidRequestsNeverAuthenticate(string path, string method, string? body)
    {
        var tokens = new Tokens();
        var client = new RealSuzClient(new Factory(new Handler(_ => throw new Exception("network"))), tokens, OptionsFor());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Request() with { Url = path, Method = method, Body = body }, TestContext.Current.CancellationToken));
        Assert.Equal(0, tokens.Calls);
    }

    [Fact]
    public async Task MockAndInvalidConnectionBlockBeforeAuthAndDemoRemainsLocal()
    {
        var tokens = new Tokens();
        var factory = new Factory(new Handler(_ => throw new Exception("network")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RealSuzClient(factory, tokens, OptionsFor(CrptIntegrationMode.Mock)).ExecuteAsync(Request(), TestContext.Current.CancellationToken));
        var real = new RealSuzClient(factory, tokens, OptionsFor());
        await Assert.ThrowsAsync<InvalidOperationException>(() => real.ExecuteAsync(Request() with { SuzConnection = Connection with { BaseUrl = "https://evil.test" } }, TestContext.Current.CancellationToken));
        var demo = await new SuzDemoHandler(real).ExecuteAsync(new Dictionary<string, string> { ["value"] = "demo-order-001" },
            new(new CrptProfile(), "demo-correlation"), TestContext.Current.CancellationToken);
        Assert.Equal("true", demo.ResponseHeaders["X-CRPT-Workbench-Mock"]);
        Assert.Equal(0, tokens.Calls);
    }

    [Fact]
    public async Task SafeLoggerNeverIncludesSecretsOrRemoteError()
    {
        var logger = new Capture();
        using var client = new HttpClient(new SuzSafeLoggingHandler(logger) { InnerHandler = new Handler(_ => Reply(200, "secret-response-token")) });
        using var message = new HttpRequestMessage(HttpMethod.Post, CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/auth/simpleSignIn/private-connection") { Content = new StringContent("secret-signature") };
        message.Headers.Add("clientToken", "secret-header-token");
        await client.SendAsync(message, TestContext.Current.CancellationToken);
        var text = string.Join(" ", logger.Messages);
        Assert.Contains("SUZ authentication", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("private-connection", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFailuresAreSanitizedAndTimeoutIsDistinct(bool timeout)
    {
        var factory = new Factory(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingContent(timeout) })));
        var client = new RealSuzClient(factory, new Tokens(), OptionsFor());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(timeout ? "Превышено время ожидания ответа СУЗ." : "Не удалось получить ответ СУЗ. Проверьте подключение и повторите запрос.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET-READ-SENTINEL", error.ToString());
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new Factory(new Handler(_ => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }));
        var client = new RealSuzClient(factory, new Tokens(), OptionsFor());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync(Request(), cancellation.Token));
    }

    private sealed class FailingContent(bool timeout) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw (timeout ? new OperationCanceledException("SECRET-READ-SENTINEL") : new IOException("SECRET-READ-SENTINEL"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return action(request); } }
    internal sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    internal static Task<HttpResponseMessage> Reply(int status, string body) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    private sealed class Tokens : ISuzTokenProvider
    {
        public int Calls; public int Invalidations;
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext a, SuzConnectionContext c, string id, CancellationToken ct = default) => Task.FromResult("token-" + ++Calls);
        public void Invalidate(IntegrationAuthenticationContext a, SuzConnectionContext c, string token) { Assert.Equal("token-1", token); Invalidations++; }
    }
    private sealed class Capture : ILogger<SuzSafeLoggingHandler>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
