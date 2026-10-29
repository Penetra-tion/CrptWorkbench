using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class TokenInvalidationTests
{
    private static readonly IntegrationAuthenticationContext Authentication = new(
        Guid.Parse("d83e1594-5531-4644-a4af-3eb08c86b620"), "7700000000",
        "0123456789ABCDEF0123456789ABCDEF01234567");
    private const string Code = "010460000000000021DEMO000000001";

    [Theory]
    [InlineData("buffered")]
    [InlineData("streaming")]
    [InlineData("catalog")]
    public async Task LateUnauthorizedResponseReusesReplacementToken(string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var slowDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signIns = 0;
        var businessTokens = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
        using var handler = new DelegateHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/key", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"uuid\":\"challenge\",\"data\":\"challenge-data\"}");
            if (path.EndsWith("/auth/simpleSignIn", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { token = $"token-{Interlocked.Increment(ref signIns)}" }));

            var correlation = request.Headers.GetValues("X-Correlation-Id").Single();
            var usedToken = request.Headers.Authorization!.Parameter!;
            businessTokens.GetOrAdd(correlation, _ => new()).Enqueue(usedToken);
            if (usedToken == "token-1")
            {
                if (correlation == "slow")
                {
                    slowDispatched.TrySetResult();
                    await releaseSlow.Task.WaitAsync(token);
                }
                return Json(HttpStatusCode.Unauthorized, "{\"error_message\":\"expired\"}");
            }
            return Json(HttpStatusCode.OK, "[]");
        });
        var options = Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production });
        var factory = new StaticHttpClientFactory(handler);
        var provider = new TrueApiTokenProvider(factory, new SignatureService(), options, TimeProvider.System);
        var trueApi = new RealTrueApiClient(factory, provider, options);
        var catalog = new RealNationalCatalogClient(factory, provider, options);

        async Task<IntegrationExchange> Execute(string correlation)
        {
            var request = new IntegrationRequest(mode == "catalog" ? "national-catalog.product" :
                    mode == "streaming" ? "true-api.aggregation-tree" : "true-api.code-info", "POST",
                mode == "catalog" ? CrptIntegrationOptions.ProductionNationalCatalogBaseUrl + "/v4/rd-info-by-gtin" :
                    CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/cises/info",
                new Dictionary<string, string> { ["X-Correlation-Id"] = correlation },
                JsonSerializer.Serialize(new[] { Code }), correlation, Authentication);
            if (mode == "catalog") return await catalog.ExecuteAsync(request, ct);
            if (mode != "streaming") return await trueApi.ExecuteAsync(request, ct);
            await using var destination = new MemoryStream();
            var exchange = await trueApi.ExecuteToStreamAsync(request, destination, ct);
            Assert.Equal("[]", Encoding.UTF8.GetString(destination.ToArray()));
            return exchange;
        }

        var slow = Execute("slow");
        await slowDispatched.Task.WaitAsync(ct);
        try
        {
            Assert.Equal(200, (await Execute("fast")).StatusCode);
        }
        finally { releaseSlow.TrySetResult(); }
        Assert.Equal(200, (await slow).StatusCode);
        Assert.Equal(2, signIns);
        Assert.Equal(new[] { "token-1", "token-2" }, businessTokens["fast"].ToArray());
        Assert.Equal(new[] { "token-1", "token-2" }, businessTokens["slow"].ToArray());
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("inn")]
    [InlineData("certificate")]
    public async Task RejectedTokenInvalidationKeepsOtherAuthenticationContexts(string changedPart)
    {
        var ct = TestContext.Current.CancellationToken;
        var signIns = 0;
        using var handler = new DelegateHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/auth/key", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"uuid\":\"challenge\",\"data\":\"challenge-data\"}")
                : Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { token = $"token-{Interlocked.Increment(ref signIns)}" }))));
        ITrueApiTokenProvider provider = new TrueApiTokenProvider(new StaticHttpClientFactory(handler), new SignatureService(),
            Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production }), TimeProvider.System);
        var other = changedPart switch
        {
            "profile" => Authentication with { ProfileId = Guid.Parse("ff76985a-6598-4bca-b9ca-a182c9bb923f") },
            "inn" => Authentication with { Inn = "7800000000" },
            _ => Authentication with { CertificateThumbprint = "1123456789ABCDEF0123456789ABCDEF01234567" }
        };
        Assert.Equal("token-1", await provider.GetTokenAsync(Authentication, "first", ct));
        Assert.Equal("token-2", await provider.GetTokenAsync(other, "second", ct));
        provider.Invalidate(Authentication, "token-2");
        Assert.Equal("token-1", await provider.GetTokenAsync(Authentication, "still-first", ct));
        provider.Invalidate(Authentication, "token-1");
        Assert.Equal("token-3", await provider.GetTokenAsync(Authentication, "renew-first", ct));
        Assert.Equal("token-2", await provider.GetTokenAsync(other, "still-second", ct));
        Assert.Equal(3, signIns);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false)
        { BaseAddress = new Uri(CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/") };
    }

    private sealed class SignatureService : ICertificateSignatureService
    {
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default) =>
            Task.FromResult("test-signature");
    }
}
