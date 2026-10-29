using System.Collections.Concurrent;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class ShipmentCheckServiceTests
{
    private const string Inn = "7700000000";
    private const string Code = "010460000000000021test";
    private static ApiOperationContext Context() => new(new CrptProfile { Inn = Inn }, "shipment-correlation");
    private static string History(params string[] ids) => JsonSerializer.Serialize(ids.Select(id => new { docId = id, timestamp = "2026-10-01T00:00:00Z" }));
    private static string Document(string type = "UNIVERSAL_TRANSFER_DOCUMENT", string status = "CHECKED_OK", object? body = null) =>
        JsonSerializer.Serialize(new[] { new { type, status, senderInn = Inn, receiverInn = "7800000000", receivedAt = "2026-10-01T00:00:00Z", body } });
    private static object Body(string code = Code) => new { sellerInfo = new { inn = Inn }, buyerInfo = new { inn = "7800000000" },
        upd = "1", updDate = "2026-10-01T00:00:00Z", products = new[] { new { code, codeType = "EXEMPLARY" } }, productsTotal = 1 };

    [Fact]
    public async Task History404MeansNotShipped()
    {
        var client = new FakeClient((_, _) => Task.FromResult(Reply(404, "secret server message")));
        var result = await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken);
        Assert.Equal(ShipmentCheckState.NotShipped, result.State);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task MetadataPrecedesBodyAndNoRawBodyLeavesService()
    {
        var client = new FakeClient((r, _) => Task.FromResult(Reply(200, r.Method == "POST" ? History("a") :
            r.Url.Contains("body=true") ? Document(body: Body()) : Document())));
        var result = await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, "furs", TestContext.Current.CancellationToken);
        Assert.Equal(ShipmentCheckState.Shipped, result.State);
        Assert.Null(result.Reason);
        var requests = client.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("doc/a/info?body=false&content=false&pg=furs", requests[1].Url);
        Assert.Equal("doc/a/info?body=true&content=false&pg=furs&limit=36000&pageNumber=1", requests[2].Url);
        Assert.All(requests, r => { Assert.Equal(ShipmentCheckService.OperationId, r.OperationId); Assert.Equal("shipment-correlation", r.CorrelationId); Assert.Null(r.Body); Assert.Equal(Inn, r.Authentication!.Inn); });
    }

    [Fact]
    public async Task UnsupportedDocumentNeverLoadsBody()
    {
        var client = new FakeClient((r, _) => Task.FromResult(Reply(200, r.Method == "POST" ? History("a") : Document("AGGREGATION_DOCUMENT"))));
        Assert.Equal(ShipmentCheckState.NotShipped, (await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task PendingDocumentGivesUnknownWithoutBody()
    {
        var client = new FakeClient((r, _) => Task.FromResult(Reply(200, r.Method == "POST" ? History("a") : Document(status: "IN_PROGRESS"))));
        Assert.Equal(ShipmentCheckState.Unknown, (await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, client.Requests.Count);
    }

    [Theory] [InlineData(401)] [InlineData(403)]
    public async Task AuthorizationFailureStopsLaterCodes(int status)
    {
        var client = new FakeClient((_, _) => Task.FromResult(Reply(status, "sensitive")));
        var session = new ShipmentCheckService(client).CreateSession(Context());
        Assert.Equal(ShipmentCheckState.Unknown, (await session.CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
        Assert.True(session.AuthorizationFailed);
        Assert.Equal(ShipmentCheckState.Unknown, (await session.CheckAsync("other", null, TestContext.Current.CancellationToken)).State);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task RateLimitHasExactlyTwoRetries()
    {
        var client = new FakeClient((_, _) => Task.FromResult(Reply(429, "sensitive", new Dictionary<string, string> { ["Retry-After"] = "0" })));
        Assert.Equal(ShipmentCheckState.Unknown, (await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public async Task ParallelRequestsNeverExceedFour()
    {
        var active = 0; var maximum = 0;
        var client = new FakeClient(async (_, token) =>
        {
            var count = Interlocked.Increment(ref active);
            int old;
            do { old = Volatile.Read(ref maximum); } while (old < count && Interlocked.CompareExchange(ref maximum, count, old) != old);
            try { await Task.Delay(20, token); return Reply(404, ""); }
            finally { Interlocked.Decrement(ref active); }
        });
        var session = new ShipmentCheckService(client).CreateSession(Context());
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => session.CheckAsync("code" + i, null, TestContext.Current.CancellationToken)));
        Assert.InRange(maximum, 1, 4);
    }

    [Fact]
    public async Task SharedDocumentIsReadOncePerSession()
    {
        var client = new FakeClient(async (r, token) => { await Task.Delay(10, token); return Reply(200, r.Method == "POST" ? History("a") :
            r.Url.Contains("body=true") ? Document(body: Body()) : Document()); });
        var session = new ShipmentCheckService(client).CreateSession(Context());
        await Task.WhenAll(session.CheckAsync(Code, null, TestContext.Current.CancellationToken), session.CheckAsync("another-code", null, TestContext.Current.CancellationToken));
        Assert.Equal(2, client.Requests.Count(r => r.Method == "GET"));
    }

    [Fact]
    public async Task CancellationDoesNotBecomeUnknownAndCanResume()
    {
        var client = new FakeClient(async (r, token) => { await Task.Delay(25, token); return Reply(404, ""); });
        var session = new ShipmentCheckService(client).CreateSession(Context());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CheckAsync(Code, null, cancellation.Token));
        Assert.Equal(ShipmentCheckState.NotShipped, (await session.CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task TransportExceptionIsSafe()
    {
        var client = new FakeClient((_, _) => throw new InvalidOperationException("Bearer private body"));
        var result = await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken);
        Assert.Equal(ShipmentCheckState.Unknown, result.State);
        Assert.DoesNotContain("private", result.Reason);
        Assert.DoesNotContain("Bearer", result.Reason);
    }

    [Fact]
    public async Task MissingBodyCannotBecomeNotShipped()
    {
        var client = new FakeClient((r, _) => Task.FromResult(Reply(200, r.Method == "POST" ? History("a") : Document())));
        Assert.Equal(ShipmentCheckState.Unknown, (await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task CancelledDocumentNeedsNoBody()
    {
        var client = new FakeClient((r, _) => Task.FromResult(Reply(200, r.Method == "POST" ? History("a") : Document(status: "CANCELLED"))));
        Assert.Equal(ShipmentCheckState.NotShipped, (await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task CancellingInflightCheckAllowsSameCodeToResume()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient(async (_, token) => { reached.TrySetResult(); await Task.Delay(50, token); return Reply(404, ""); });
        var session = new ShipmentCheckService(client).CreateSession(Context());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = session.CheckAsync(Code, null, cancellation.Token);
        await reached.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(ShipmentCheckState.NotShipped, (await session.CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task CancelledDocumentReadDoesNotPoisonResumeCache()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient(async (request, token) =>
        {
            if (request.Method == "POST") return Reply(200, History("a"));
            reached.TrySetResult();
            await Task.Delay(40, token);
            return Reply(200, request.Url.Contains("body=true") ? Document(body: Body()) : Document());
        });
        var session = new ShipmentCheckService(client).CreateSession(Context());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = session.CheckAsync(Code, null, cancellation.Token);
        await reached.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(ShipmentCheckState.Shipped, (await session.CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task MissingCodesPageCannotPretendToBeComplete()
    {
        var client = new FakeClient((r, _) => Task.FromResult(Reply(200, r.Method == "POST" ? History("a") :
            r.Url.Contains("body=true") ? Document(body: new { products = new[] { new { code = Code, codeType = "EXEMPLARY" } }, productsTotal = 36001 }) : Document())));
        Assert.Equal(ShipmentCheckState.Unknown, (await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task ChangingDocumentTypeBetweenMetadataAndBodyIsUnknown()
    {
        var client = new FakeClient((r, _) => Task.FromResult(Reply(200, r.Method == "POST" ? History("a") :
            r.Url.Contains("body=true") ? Document(type: "AGGREGATION_DOCUMENT", body: Body()) : Document())));
        Assert.Equal(ShipmentCheckState.Unknown, (await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task FursGroupIsRecoveredFromHistoryForFileCodes()
    {
        var history = JsonSerializer.Serialize(new[] { new { docId = "a", timestamp = "2026-10-01T00:00:00Z", productGroup = "furs" } });
        var client = new FakeClient((r, _) => Task.FromResult(Reply(200, r.Method == "POST" ? history :
            r.Url.Contains("body=true") ? Document(body: Body()) : Document())));
        var result = await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken);
        Assert.Equal(ShipmentCheckState.Shipped, result.State);
        Assert.All(client.Requests.Where(r => r.Method == "GET"), r => Assert.Contains("&pg=furs", r.Url));
    }

    [Fact]
    public async Task HistoryGroupConflictingWithCodeLookupIsUnknown()
    {
        var history = JsonSerializer.Serialize(new[] { new { docId = "a", timestamp = "2026-10-01T00:00:00Z", productGroup = "furs" } });
        var client = new FakeClient((_, _) => Task.FromResult(Reply(200, history)));
        var result = await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, "shoes", TestContext.Current.CancellationToken);
        Assert.Equal(ShipmentCheckState.Unknown, result.State);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task DocumentBeyond36000ReadsAllPages()
    {
        var pages = new ConcurrentQueue<int>();
        var client = new FakeClient((r, _) =>
        {
            if (r.Method == "POST") return Task.FromResult(Reply(200, History("a")));
            if (!r.Url.Contains("body=true")) return Task.FromResult(Reply(200, Document()));
            var page = r.Url.EndsWith("pageNumber=1") ? 1 : 2; pages.Enqueue(page);
            var products = page == 1 ? Enumerable.Range(0, 36000).Select(i => new { code = "other" + i, codeType = "EXEMPLARY" }).ToArray() :
                new[] { new { code = Code, codeType = "EXEMPLARY" } };
            return Task.FromResult(Reply(200, Document(body: new { sellerInfo = new { inn = Inn }, buyerInfo = new { inn = "7800000000" },
                upd = "1", updDate = "2026-10-01T00:00:00Z", products, productsTotal = 36001 })));
        });
        Assert.Equal(ShipmentCheckState.Shipped, (await new ShipmentCheckService(client).CreateSession(Context()).CheckAsync(Code, null, TestContext.Current.CancellationToken)).State);
        Assert.Equal(new[] { 1, 2 }, pages.ToArray());
    }

    private static IntegrationExchange Reply(int status, string body, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, "GET", "mock", new Dictionary<string, string>(), null, headers ?? new Dictionary<string, string>(), body, TimeSpan.Zero);
    private sealed class FakeClient(Func<IntegrationRequest, CancellationToken, Task<IntegrationExchange>> execute) : ITrueApiClient
    {
        public Uri BaseAddress => new("https://true-api.mock.invalid/api/v3/true-api/");
        public ConcurrentQueue<IntegrationRequest> Requests { get; } = new();
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        { Requests.Enqueue(request); return execute(request, cancellationToken); }
    }
}
