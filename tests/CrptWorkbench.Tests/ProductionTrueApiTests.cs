using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class ProductionTrueApiTests
{
    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";
    private static readonly IntegrationAuthenticationContext Authentication = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "7700000000", Thumbprint);

    [Theory]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v3/true-api/auth/key", true)]
    [InlineData("POST", "https://markirovka.crpt.ru/api/v3/true-api/auth/simpleSignIn", true)]
    [InlineData("POST", "https://markirovka.crpt.ru/api/v3/true-api/cises/info", true)]
    [InlineData("POST", "https://markirovka.crpt.ru/api/v3/true-api/cises/history?cis=010460000000000021DEMO000000001", true)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v3/true-api/cises/history?cis=010460000000000021DEMO000000001", false)]
    [InlineData("POST", "https://markirovka.crpt.ru/api/v3/true-api/cises/history", false)]
    [InlineData("POST", "https://markirovka.crpt.ru/api/v3/true-api/cises/history?cis=010460000000000021DEMO000000001&pg=milk", false)]
    [InlineData("POST", "https://markirovka.crpt.ru:444/api/v3/true-api/cises/info", false)]
    [InlineData("POST", "http://markirovka.crpt.ru/api/v3/true-api/cises/info", false)]
    [InlineData("POST", "https://markirovka.crpt.ru.evil.example/api/v3/true-api/cises/info", false)]
    [InlineData("POST", "https://user@markirovka.crpt.ru/api/v3/true-api/cises/info", false)]
    [InlineData("POST", "https://markirovka.crpt.ru/api/v3/true-api/cises/info?pg=milk", false)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v3/true-api/cises/info", false)]
    [InlineData("POST", "https://markirovka.crpt.ru/api/v3/true-api/lk/documents/create", false)]
    public async Task ProductionGuardAllowsOnlyApprovedMethodAndEndpoint(string method, string url, bool allowed)
    {
        using var guard = new ProductionTrueApiNetworkGuardHandler { InnerHandler = new ConstantHandler(HttpStatusCode.OK, "{}") };
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

    [Fact]
    public async Task MockGuardStillRejectsProductionHost()
    {
        using var guard = new MockOnlyNetworkGuardHandler { InnerHandler = new ConstantHandler(HttpStatusCode.OK, "{}") };
        using var invoker = new HttpMessageInvoker(guard);
        using var production = new HttpRequestMessage(HttpMethod.Get, CrptIntegrationOptions.ProductionTrueApiBaseUrl);
        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(production, TestContext.Current.CancellationToken));
        using var mock = new HttpRequestMessage(HttpMethod.Get, "https://safe.mock.invalid/test");
        using var response = await invoker.SendAsync(mock, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var networkCalled = false;
        var handler = new DelegateHandler(request =>
        {
            networkCalled = true;
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        var mockOptions = ProductionOptions();
        mockOptions.Mode = CrptIntegrationMode.Mock;
        var tokenProvider = new TrueApiTokenProvider(new StaticHttpClientFactory(handler), new FakeSignatureService(), Options.Create(mockOptions), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokenProvider.GetTokenAsync(Authentication, "mock", TestContext.Current.CancellationToken));
        Assert.False(networkCalled);
    }

    [Fact]
    public async Task OutgoingLoggingNeverIncludesAuthorizationOrBody()
    {
        var logger = new CapturingLogger<LoggingHandler>();
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var logging = new LoggingHandler(logger, configuration) { InnerHandler = new ConstantHandler(HttpStatusCode.OK, "{}") };
        using var invoker = new HttpMessageInvoker(logging);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/cises/info")
        {
            Content = new StringContent("{\"signature\":\"base64-secret-signature\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new("Bearer", "production-secret-token");

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        var messages = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains("cises/info", messages);
        Assert.DoesNotContain("production-secret-token", messages);
        Assert.DoesNotContain("base64-secret-signature", messages);
        Assert.DoesNotContain("Authorization", messages);
    }

    [Fact]
    public async Task FullOutgoingLoggingIncludesExchangeAndPreservesResponseContent()
    {
        const string token = "full-log-production-token";
        const string requestSecret = "full-log-request-signature";
        const string responseSecret = "full-log-response-error";
        var logger = new CapturingLogger<LoggingHandler>();
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkbenchLogging:HttpDetailsEnabled"] = "true"
            })
            .Build();
        using var logging = new LoggingHandler(logger, configuration)
        {
            InnerHandler = new ConstantHandler(
                HttpStatusCode.Forbidden,
                $"{{\"error_message\":\"{responseSecret}\"}}")
        };
        using var invoker = new HttpMessageInvoker(logging);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/auth/simpleSignIn")
        {
            Content = new StringContent(
                $"{{\"data\":\"{requestSecret}\"}}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new("Bearer", token);

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var messages = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains("Authorization", messages);
        Assert.DoesNotContain(token, messages);
        Assert.DoesNotContain(requestSecret, messages);
        Assert.DoesNotContain(responseSecret, messages);
        Assert.Contains(responseSecret, responseBody);
    }

    [Fact]
    public async Task TokenProviderTreatsTokensAsOpaqueCachesAndCoalescesRefresh()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var signer = new FakeSignatureService();
        var signInCount = 0;
        var handler = new DelegateHandler(async request =>
        {
            Assert.Equal("corr-1", request.Headers.GetValues("X-Correlation-Id").Single());
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/key", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"uuid\":\"challenge-id\",\"data\":\"sign-exactly-this\"}");

            var body = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("challenge-id", json.RootElement.GetProperty("uuid").GetString());
            Assert.Equal(signer.Signature, json.RootElement.GetProperty("data").GetString());
            Assert.Equal("7700000000", json.RootElement.GetProperty("inn").GetString());
            var token = Interlocked.Increment(ref signInCount) == 1 ? "header.payload.signature" : "c7c99992-40f8-41e1-b8a5-130d78bc35b3";
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { token }));
        });
        var provider = CreateTokenProvider(handler, signer, time);

        var firstBatch = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => provider.GetTokenAsync(Authentication, "corr-1", TestContext.Current.CancellationToken)));
        Assert.All(firstBatch, token => Assert.Equal("header.payload.signature", token));
        Assert.Equal(1, signInCount);
        Assert.Equal("sign-exactly-this", signer.Data);
        Assert.Equal(Thumbprint, signer.Thumbprint);

        time.Advance(TimeSpan.FromHours(9) + TimeSpan.FromSeconds(1));
        var refreshed = await provider.GetTokenAsync(Authentication, "corr-1", TestContext.Current.CancellationToken);
        Assert.Equal("c7c99992-40f8-41e1-b8a5-130d78bc35b3", refreshed);
        Assert.Equal(2, signInCount);
    }

    [Fact]
    public async Task RealClientRefreshesOnceAfterUnauthorizedAndReturnsOnlyFinalExchange()
    {
        var call = 0;
        var authorizations = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            authorizations.Add(request.Headers.Authorization!.ToString());
            Assert.Equal("operation-correlation", request.Headers.GetValues("X-Correlation-Id").Single());
            return Task.FromResult(Interlocked.Increment(ref call) == 1
                ? Json(HttpStatusCode.Unauthorized, "{\"error_message\":\"expired\"}")
                : Json(HttpStatusCode.OK, "[]"));
        });
        var tokenProvider = new RotatingTokenProvider();
        var options = Options.Create(ProductionOptions());
        var client = new RealTrueApiClient(new StaticHttpClientFactory(handler), tokenProvider, options);
        var request = new IntegrationRequest("true-api.code-info", "POST",
            $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/cises/info",
            new Dictionary<string, string> { ["Accept"] = "application/json", ["X-Correlation-Id"] = "operation-correlation" },
            "[]", "operation-correlation", Authentication);

        var exchange = await client.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal(2, call);
        Assert.Equal(1, tokenProvider.Invalidations);
        Assert.Equal(new[] { "Bearer token-1", "Bearer token-2" }, authorizations);
        Assert.False(exchange.ResponseHeaders.ContainsKey("X-CRPT-Workbench-Mock"));
        Assert.True(exchange.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task RealClientDoesNotRetryOtherErrors()
    {
        var call = 0;
        var handler = new DelegateHandler(_ =>
        {
            Interlocked.Increment(ref call);
            return Task.FromResult(Json(HttpStatusCode.Forbidden, "{\"error_message\":\"forbidden\"}"));
        });
        var tokenProvider = new RotatingTokenProvider();
        var client = new RealTrueApiClient(new StaticHttpClientFactory(handler), tokenProvider, Options.Create(ProductionOptions()));
        var request = new IntegrationRequest("true-api.code-info", "POST",
            $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/cises/info",
            new Dictionary<string, string> { ["X-Correlation-Id"] = "operation-correlation" },
            "[]", "operation-correlation", Authentication);

        var exchange = await client.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(403, exchange.StatusCode);
        Assert.Equal(1, call);
        Assert.Equal(0, tokenProvider.Invalidations);
    }

    [Fact]
    public async Task BulkCodeInfoAllowsOnlyExistingPathAndAtMostOneThousandCodes()
    {
        var calls = 0;
        var handler = new DelegateHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Json(HttpStatusCode.OK, "[]"));
        });
        var client = new RealTrueApiClient(new StaticHttpClientFactory(handler), new RotatingTokenProvider(), Options.Create(ProductionOptions()));
        var url = $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/cises/info";
        IntegrationRequest Request(string target, string body) => new("true-api.code-info-bulk", "POST", target,
            new Dictionary<string, string> { ["X-Correlation-Id"] = "bulk-test" }, body, "bulk-test", Authentication);
        var code = "010460000000000021DEMO000000001";
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Request(url + "?pg=milk", $"[\"{code}\"]"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Request(url, JsonSerializer.Serialize(Enumerable.Repeat(code, 1001))), TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
        var exchange = await client.ExecuteAsync(Request(url, $"[\"{code}\"]"), TestContext.Current.CancellationToken);
        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("challenge")]
    [InlineData("  Проверка\r\nстрока\n尾  ")]
    [InlineData("\uFEFFchallenge-with-explicit-bom-character")]
    public void CryptoProSignerCreatesAttachedPkcs7SignatureWithExactContent(string challenge)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=CRPT Workbench Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var challengeBytes = new UTF8Encoding(false, true).GetBytes(challenge);

        var signature = CryptoProCertificateSignatureService.CreateAttachedPkcs7Base64(challenge, certificate);

        Assert.DoesNotContain("\r", signature);
        Assert.DoesNotContain("\n", signature);
        var signatureBytes = Convert.FromBase64String(signature);
        var signedCms = new SignedCms();
        signedCms.Decode(signatureBytes);
        Assert.Equal(challengeBytes, signedCms.ContentInfo.Content);
        signedCms.CheckSignature(new X509Certificate2Collection(certificate), verifySignatureOnly: true);
    }

    [Theory]
    [InlineData("1.2.643.2.2.19", "1.2.643.2.2.9", true)]
    [InlineData("1.2.643.7.1.1.1.1", "1.2.643.7.1.1.2.2", true)]
    [InlineData("1.2.643.7.1.1.1.2", "1.2.643.7.1.1.2.3", true)]
    [InlineData("1.2.840.113549.1.1.1", null, false)]
    public void CryptoProSignerMapsOnlySupportedGostAlgorithms(
        string publicKeyOid,
        string? expectedDigestOid,
        bool expectedGost)
    {
        Assert.Equal(expectedDigestOid, CryptoProCertificateSignatureService.GetDigestAlgorithmOid(publicKeyOid));
        Assert.Equal(expectedGost, CryptoProCertificateSignatureService.IsGostPublicKeyAlgorithm(publicKeyOid));
    }

    [Fact]
    public void WindowsCryptoApiCreatesVerifiableAttachedCmsOverExactBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=CRPT Workbench Native CMS Test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var exactBytes = new UTF8Encoding(false, true).GetBytes("  Проверка\r\n尾\uFEFF  ");

        var signature = WindowsCryptoApiCmsSigner.Sign(
            exactBytes,
            certificate,
            "2.16.840.1.101.3.4.2.1");

        try
        {
            var signedCms = new SignedCms();
            signedCms.Decode(signature);
            Assert.Equal(exactBytes, signedCms.ContentInfo.Content);
            signedCms.CheckSignature(
                new X509Certificate2Collection(certificate),
                verifySignatureOnly: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(exactBytes);
        }
    }

    private static TrueApiTokenProvider CreateTokenProvider(HttpMessageHandler handler, ICertificateSignatureService signer, TimeProvider time) =>
        new(new StaticHttpClientFactory(handler), signer, Options.Create(ProductionOptions()), time);

    private static CrptIntegrationOptions ProductionOptions() => new()
    {
        Mode = CrptIntegrationMode.Production,
        TrueApiBaseUrl = CrptIntegrationOptions.ProductionTrueApiBaseUrl,
        TokenCacheHours = 9
    };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(CrptIntegrationOptions.ProductionTrueApiBaseUrl.TrimEnd('/') + "/")
        };
    }

    private sealed class ConstantHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Json(statusCode, body));
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class FakeSignatureService : ICertificateSignatureService
    {
        public string Signature { get; } = Convert.ToBase64String(Encoding.UTF8.GetBytes("attached-signature"));
        public string? Data { get; private set; }
        public string? Thumbprint { get; private set; }
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default)
        {
            Data = data;
            Thumbprint = thumbprint;
            return Task.FromResult(Signature);
        }
    }

    private sealed class RotatingTokenProvider : ITrueApiTokenProvider
    {
        private int _requests;
        public int Invalidations { get; private set; }
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult($"token-{Interlocked.Increment(ref _requests)}");
        public void Invalidate(IntegrationAuthenticationContext authentication) => Invalidations++;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

}
