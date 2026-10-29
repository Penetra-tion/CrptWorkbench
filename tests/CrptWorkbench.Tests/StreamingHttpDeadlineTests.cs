using System.Net;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class StreamingHttpDeadlineTests
{
    [Theory]
    [InlineData("auth-key", false)]
    [InlineData("auth-token", false)]
    [InlineData("mercury", false)]
    [InlineData("sgr", false)]
    [InlineData("eaeu", false)]
    [InlineData("auth-key", true)]
    [InlineData("auth-token", true)]
    [InlineData("mercury", true)]
    [InlineData("sgr", true)]
    [InlineData("eaeu", true)]
    public async Task StalledBodyHonorsDeadlineAndCallerCancellation(string operation, bool callerCancellation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stalled = new StalledStream();
        var handler = new Handler(request => operation == "auth-token" && request.RequestUri!.AbsolutePath.EndsWith("/key", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new StringContent("{\"uuid\":\"challenge\",\"data\":\"challenge-data\"}") }
            : new(HttpStatusCode.OK) { Content = new StreamContent(stalled) });
        var factory = new Factory(handler, callerCancellation ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(100));
        var execution = ExecuteAsync(operation, factory, cancellation.Token);
        try
        {
            await stalled.Reading.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            if (callerCancellation) await cancellation.CancelAsync();
            if (callerCancellation || operation.StartsWith("auth-", StringComparison.Ordinal))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            else
                Assert.Equal(503, (await execution.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken))!.StatusCode);
            Assert.True(stalled.Disposed);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await execution; } catch (OperationCanceledException) { }
        }
    }

    private static async Task<IntegrationExchange?> ExecuteAsync(string operation, IHttpClientFactory factory, CancellationToken ct)
    {
        var authentication = new IntegrationAuthenticationContext(Guid.NewGuid(), "7700000000", new string('A', 40));
        if (operation.StartsWith("auth-", StringComparison.Ordinal))
        {
            var provider = new TrueApiTokenProvider(factory, new Signer(), Options.Create(new CrptIntegrationOptions
            { Mode = CrptIntegrationMode.Production }), TimeProvider.System);
            await provider.GetTokenAsync(authentication, "deadline", ct);
            return null;
        }
        if (operation == "mercury")
            return await new RealMercuryClient(factory).ExecuteAsync(new("mercury.vsd-info", "GET",
                "https://mercury.vetrf.ru/pub/operatorui?_action=findVetDocumentFormByUuid&uuid=0000-0000-0000-0000-0000-0000-0000-0001",
                new Dictionary<string, string>(), null, "deadline"), ct);
        var number = operation == "sgr" ? "RU.01.ЯЯ.00.001.Е.000001.01.99" : "ЕАЭС N RU Д-DEMO.АА00.В.00001/25";
        return await new RealPermitDocumentClient(factory).ExecuteAsync(new("permit-document.lookup", "GET", "",
            new Dictionary<string, string> { ["X-Permit-Number"] = number }, null, "deadline"), ct);
    }

    private sealed class StalledStream : MemoryStream
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            Reading.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
    private sealed class Factory(HttpMessageHandler handler, TimeSpan timeout) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false)
        { BaseAddress = new Uri(CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/"), Timeout = timeout };
    }
    private sealed class Signer : ICertificateSignatureService
    {
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken ct = default) => Task.FromResult("signature");
    }
}
