using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using System.Net;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class ApiConcurrencyTests
{
    [Fact(Timeout = 10000)]
    public async Task MockStreamingAndBufferedQueriesShareSlots()
    {
        using var output = new BlockingOutput();
        var mock = new MockTrueApiClient();
        var tree = new IntegrationRequest("true-api.aggregation-tree", "POST", "https://production.trueapi.mock.invalid/cises/info",
            new Dictionary<string, string>(), "[\"010460000000000021DEMO000000001\"]", "stream-limit");
        var streaming = mock.ExecuteToStreamAsync(tree, output, TestContext.Current.CancellationToken);
        var slots = new List<IDisposable>();
        try
        {
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            for (var i = 0; i < 3; i++) slots.Add(await ApiRequestLimiter.Shared.AcquireAsync(ApiConcurrencyService.TrueApi, TestContext.Current.CancellationToken));
            var dispatched = false;
            var buffered = mock.ExecuteAsync(tree with { OperationId = "true-api.code-info-bulk", BeforeDispatchAsync = _ => { dispatched = true; return Task.CompletedTask; } }, TestContext.Current.CancellationToken);
            Assert.False(dispatched);
            Assert.False(buffered.IsCompleted);
            output.Release.TrySetResult();
            await streaming.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await buffered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(dispatched);
        }
        finally
        {
            output.Release.TrySetResult();
            foreach (var slot in slots) slot.Dispose();
        }
    }

    [Fact(Timeout = 10000)]
    public async Task AuthorizationAndBusinessClientsShareTheSameServiceLimit()
    {
        var limiter = new ApiRequestLimiter();
        using var business = new HttpClient(new ApiConcurrencyHandler(limiter, ApiConcurrencyService.TrueApi) { InnerHandler = new Replies() });
        using var authentication = new HttpClient(new ApiConcurrencyHandler(limiter, ApiConcurrencyService.TrueApi) { InnerHandler = new Replies() });
        var responses = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < 4; i++) responses.Add(await business.GetAsync("https://test.invalid/cises/info", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken));
            var waiting = authentication.GetAsync("https://test.invalid/auth/key", TestContext.Current.CancellationToken);
            Assert.False(waiting.IsCompleted);
            responses[0].Dispose();
            using var response = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact(Timeout = 10000)]
    public async Task SigningSameCertificateIsSerializedAndOtherCertificatesRemainIndependent()
    {
        var key = Guid.NewGuid().ToString("N");
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CertificateSigningCoordinator.ExecuteAsync(key, () => release.Task, TestContext.Current.CancellationToken);
        var secondEntered = false;
        var second = CertificateSigningCoordinator.ExecuteAsync(key.ToUpperInvariant(), () => { secondEntered = true; return Task.FromResult("second"); }, TestContext.Current.CancellationToken);
        Assert.False(secondEntered);
        Assert.Equal("other", await CertificateSigningCoordinator.ExecuteAsync(key + "other", () => Task.FromResult("other"), TestContext.Current.CancellationToken));
        release.SetResult("first");
        Assert.Equal("first", await first);
        Assert.Equal("second", await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CertificateSigningCoordinator.ExecuteAsync<string>(key, () => throw new InvalidOperationException("signing failed"), TestContext.Current.CancellationToken));
        Assert.Equal("recovered", await CertificateSigningCoordinator.ExecuteAsync(key, () => Task.FromResult("recovered"), TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 10000)]
    public async Task LimitIsSharedAcrossCallersButIndependentBetweenServices()
    {
        var limiter = new ApiRequestLimiter();
        var slots = new List<IDisposable>();
        try
        {
            for (var i = 0; i < 4; i++) slots.Add(await limiter.AcquireAsync(ApiConcurrencyService.TrueApi, TestContext.Current.CancellationToken));
            var waiting = limiter.AcquireAsync(ApiConcurrencyService.TrueApi, TestContext.Current.CancellationToken).AsTask();
            Assert.False(waiting.IsCompleted);
            using var other = await limiter.AcquireAsync(ApiConcurrencyService.Suz, TestContext.Current.CancellationToken);
            slots[0].Dispose();
            using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var cancelled = limiter.AcquireAsync(ApiConcurrencyService.TrueApi, cancellation.Token).AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        finally { foreach (var slot in slots) slot.Dispose(); }
        for (var i = 0; i < 4; i++) using (await limiter.AcquireAsync(ApiConcurrencyService.TrueApi, TestContext.Current.CancellationToken)) { }
    }

    [Fact(Timeout = 10000)]
    public async Task StreamingResponsesHoldSlotsUntilDisposedAndDispatchWaitsForSlot()
    {
        var limiter = new ApiRequestLimiter();
        using var client = new HttpClient(new ApiConcurrencyHandler(limiter, ApiConcurrencyService.TrueApi) { InnerHandler = new Replies() });
        var responses = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < 4; i++) responses.Add(await client.GetAsync("https://test.invalid", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken));
            var dispatched = false;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.invalid");
            request.Options.Set(ApiConcurrencyHandler.BeforeDispatch, _ => { dispatched = true; return Task.CompletedTask; });
            var waiting = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
            Assert.False(waiting.IsCompleted);
            Assert.False(dispatched);
            responses[0].Dispose();
            using var response = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(dispatched);
            Assert.Equal("result", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact(Timeout = 10000)]
    public async Task CancellationWaitingForTransportPreservesNotDispatchedMarker()
    {
        var limiter = new ApiRequestLimiter();
        var slots = new List<IDisposable>();
        try
        {
            for (var i = 0; i < 4; i++) slots.Add(await limiter.AcquireAsync(ApiConcurrencyService.TrueApi, TestContext.Current.CancellationToken));
            using var client = new HttpClient(new ApiConcurrencyHandler(limiter, ApiConcurrencyService.TrueApi) { InnerHandler = new Replies() });
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var request = client.GetAsync("https://test.invalid", cancellation.Token);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Exception? current = error;
            while (current is not null && current is not ApiDispatchNotStartedException) current = current.InnerException;
            Assert.IsType<ApiDispatchNotStartedException>(current);
        }
        finally { foreach (var slot in slots) slot.Dispose(); }
    }

    [Fact(Timeout = 10000)]
    public async Task TransportFailuresDoNotLeakSlots()
    {
        var limiter = new ApiRequestLimiter();
        using var client = new HttpClient(new ApiConcurrencyHandler(limiter, ApiConcurrencyService.TrueApi) { InnerHandler = new Fails() });
        for (var i = 0; i < 8; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://test.invalid", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MockStreamingChecksDispatchBoundaryAfterWaiting()
    {
        var slots = new List<IDisposable>();
        var ct = TestContext.Current.CancellationToken;
        var mock = new MockTrueApiClient();
        using var destination = new MemoryStream();
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new IntegrationRequest("true-api.aggregation-tree", "POST", "https://production.trueapi.mock.invalid/cises/info",
            new Dictionary<string, string>(), "[\"010460000000000021DEMO000000001\"]", "paused-stream")
        {
            BeforeDispatchAsync = _ => { rejected.TrySetResult(); throw new OperationCanceledException(); }
        };
        Task<IntegrationExchange>? streaming = null;
        try
        {
            for (var i = 0; i < 4; i++) slots.Add(await ApiRequestLimiter.Shared.AcquireAsync(ApiConcurrencyService.TrueApi, ct));
            streaming = mock.ExecuteToStreamAsync(request, destination, ct);
            Assert.False(streaming.IsCompleted);
            foreach (var slot in slots) slot.Dispose();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => streaming);
            Assert.True(ApiDispatchNotStartedException.IsPresent(exception));
            Assert.True(rejected.Task.IsCompleted);
            Assert.Equal(0, destination.Length);
        }
        finally { foreach (var slot in slots) slot.Dispose(); }
    }

    private sealed class Replies : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("result") });
    }
    private sealed class BlockingOutput : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
    private sealed class Fails : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("test");
    }
}
