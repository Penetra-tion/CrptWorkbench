using System.Net;
using System.Text;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class AuthenticationDispatchWaitTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PauseCancelsPendingAuthenticationBeforeBusinessDispatch(bool streaming, bool refreshing)
    {
        var ct = TestContext.Current.CancellationToken;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var eligible = 1;
        var businessCalls = 0;
        var dispatchClaims = 0;
        var tokens = new BlockingTokenProvider(refreshing);
        using var handler = new DelegateHandler(() =>
        {
            Interlocked.Increment(ref businessCalls);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("{\"error\":\"expired\"}", Encoding.UTF8, "application/json") };
        });
        var options = Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production });
        var client = new RealTrueApiClient(new StaticHttpClientFactory(handler), tokens, options);
        var request = new IntegrationRequest(streaming ? "true-api.aggregation-tree" : "true-api.code-info", "POST",
            CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/cises/info", new Dictionary<string, string>(),
            "[\"010460000000000021DEMO000000001\"]", "pause-auth",
            new(Guid.NewGuid(), "7700000000", "0123456789ABCDEF0123456789ABCDEF01234567"))
        {
            CanDispatchAsync = _ => Task.FromResult(Volatile.Read(ref eligible) == 1),
            BeforeDispatchAsync = _ => { Interlocked.Increment(ref dispatchClaims); return Task.CompletedTask; }
        };
        await using var destination = new MemoryStream();
        var execution = streaming
            ? client.ExecuteToStreamAsync(request, destination, lifetime.Token)
            : client.ExecuteAsync(request, lifetime.Token);
        try
        {
            await tokens.Entered.Task.WaitAsync(ct);
            Volatile.Write(ref eligible, 0);
            var completed = await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(2), ct));
            Assert.Same(execution, completed);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await execution);
            Assert.True(ApiDispatchNotStartedException.IsPresent(error));
            Assert.True(tokens.Finished.Task.IsCompleted);
            Assert.Equal(refreshing ? 1 : 0, businessCalls);
            Assert.Equal(0, dispatchClaims);
            Assert.Equal(0, destination.Length);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await execution; } catch (OperationCanceledException) { }
        }
    }

    private sealed class BlockingTokenProvider(bool refreshing) : ITrueApiTokenProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public async Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1 && refreshing) return "old-token";
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); return "unreachable-token"; }
            finally { Finished.TrySetResult(); }
        }
        public void Invalidate(IntegrationAuthenticationContext authentication) { }
    }

    private sealed class DelegateHandler(Func<HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback());
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false)
        { BaseAddress = new Uri(CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/") };
    }
}
