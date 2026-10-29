using System.Diagnostics;
using System.Net;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class ApiRateLimitTests
{
    [Theory]
    [InlineData("60")]
    [InlineData("date")]
    [InlineData("invalid")]
    public async Task RateLimitedResponseDefersOtherCallersAndWaitingCanBeCancelled(string header)
    {
        var limiter = new ApiRequestLimiter();
        var transport = new RateLimitedReplies(header);
        using var client = new HttpClient(new ApiConcurrencyHandler(limiter, ApiConcurrencyService.NationalCatalog) { InnerHandler = transport });
        using var first = await client.GetAsync("https://test.invalid", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = client.GetAsync("https://test.invalid", cancellation.Token);
        try
        {
            Assert.False(waiting.IsCompleted);
            using var otherService = await limiter.AcquireAsync(ApiConcurrencyService.Suz, TestContext.Current.CancellationToken);
            Assert.Equal(1, transport.Requests);
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task TrueApiStartsArePacedAcrossCallers()
    {
        var limiter = new ApiRequestLimiter();
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 51; i++)
            using (await limiter.AcquireAsync(ApiConcurrencyService.TrueApi, TestContext.Current.CancellationToken)) { }
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(980), "51 requests must not start in less than one second.");
    }

    [Theory]
    [InlineData("12", 0, 12000, 12250)]
    [InlineData("Thu, 29 Oct 2026 12:00:05 GMT", 0, 5000, 5250)]
    [InlineData("Thu, 29 Oct 2026 11:59:00 GMT", 0, 0, 250)]
    [InlineData("invalid", 0, 1000, 1250)]
    [InlineData("-1", 1, 2000, 2250)]
    public void RetryAfterAcceptsSecondsAndDatesAndFallsBackForInvalidValues(string header, int attempt, int minimumMs, int maximumMs)
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var delay = ApiRateLimitDelay.ForRetry(new Dictionary<string, string> { ["retry-after"] = header }, attempt, now);
        Assert.InRange(delay.TotalMilliseconds, minimumMs, maximumMs);
    }

    [Fact]
    public async Task HugeRetryAfterDoesNotOverflowAndRemainsCancellable()
    {
        var delay = ApiRateLimitDelay.ForRetry(new Dictionary<string, string> { ["Retry-After"] = "9223372036854775807" }, 0);
        Assert.True(delay > TimeSpan.FromDays(365));
        var limiter = new ApiRequestLimiter();
        limiter.Defer(ApiConcurrencyService.NationalCatalog, delay);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = limiter.AcquireAsync(ApiConcurrencyService.NationalCatalog, cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    private sealed class RateLimitedReplies(string header) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(Interlocked.Increment(ref Requests) == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            { Content = new StringContent("{}") };
            response.Headers.TryAddWithoutValidation("Retry-After", header == "date" ? DateTimeOffset.UtcNow.AddMinutes(1).ToString("R") : header);
            return Task.FromResult(response);
        }
    }
}
