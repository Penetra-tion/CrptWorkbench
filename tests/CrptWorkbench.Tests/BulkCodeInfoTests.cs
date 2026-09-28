using System.Collections.Concurrent;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class BulkCodeInfoTests
{
    private static readonly ApiOperationContext Context = new(new(), "bulk-test-correlation");
    private static Dictionary<string, string> Input(IEnumerable<string> codes) => new() { ["codes"] = string.Join('\n', codes) };
    private static string Code(int number) => $"010460000000000021{number:D12}";

    [Fact]
    public async Task BatchesAtOneThousandAndPreservesOrderAndCorrelation()
    {
        var client = new FakeClient(async (request, token) =>
        {
            await Task.Delay(4 - JsonSerializer.Deserialize<string[]>(request.Body!)!.Length / 500, token);
            return Success(request);
        });
        var codes = Enumerable.Range(1, 1001).Select(Code).ToArray();
        var result = await new TrueApiBulkCodeInfoHandler(client).ExecuteAsync(Input(codes), Context, TestContext.Current.CancellationToken);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(new[] { 1000, 1 }, client.Requests.Select(x => JsonSerializer.Deserialize<string[]>(x.Body!)!.Length).OrderByDescending(x => x));
        Assert.All(client.Requests, x => Assert.Equal(Context.CorrelationId, x.CorrelationId));
        Assert.Equal(codes, CodeInfoResults.Parse(result.ResponseBody)!.Items.Select(x => x.CisInfo!.RequestedCis));
        Assert.Equal(2, result.Exchanges!.Count);
    }

    [Fact]
    public void InvalidCodesDoNotReachTransport()
    {
        var input = Input([Code(1), "bad"]);
        Assert.Contains("Строка 2", Assert.Throws<ArgumentException>(() => TrueApiBulkCodeInfoHandler.Parse(input)).Message);
    }

    [Fact]
    public async Task IncompleteBatchMakesWholeOperationFail()
    {
        var client = new FakeClient((request, _) => Task.FromResult(JsonSerializer.Deserialize<string[]>(request.Body!)!.Length == 1
            ? Success(request) with { ResponseBody = "[]" }
            : Success(request)));
        var result = await new TrueApiBulkCodeInfoHandler(client).ExecuteAsync(Input(Enumerable.Range(1, 1001).Select(Code)), Context, TestContext.Current.CancellationToken);
        Assert.Equal(502, result.StatusCode);
        Assert.Null(CodeInfoResults.Parse(result.ResponseBody));
        Assert.Equal(2, result.Exchanges!.Count);
    }

    [Fact]
    public async Task FailedBatchMakesWholeOperationFail()
    {
        var client = new FakeClient((request, _) => Task.FromResult(JsonSerializer.Deserialize<string[]>(request.Body!)!.Length == 1
            ? Success(request) with { StatusCode = 503, ResponseBody = "{}" }
            : Success(request)));
        var result = await new TrueApiBulkCodeInfoHandler(client).ExecuteAsync(Input(Enumerable.Range(1, 1001).Select(Code)), Context, TestContext.Current.CancellationToken);
        Assert.Equal(503, result.StatusCode);
        Assert.Null(CodeInfoResults.Parse(result.ResponseBody));
        Assert.Equal(2, result.Exchanges!.Count);
    }

    [Fact]
    public void StatusFilterCopiesEveryMatchingCodeBeyondTheFirstFifty()
    {
        var results = new CodeInfoResults(Enumerable.Range(1, 120)
            .Select(index => new CodeInfoItem(new CisInfo { RequestedCis = Code(index), Status = index == 60 ? "RETIRED" : "INTRODUCED" }))
            .Append(new CodeInfoItem(new CisInfo { RequestedCis = Code(121), Status = "INTRODUCED" }, "404", "Не найден"))
            .ToArray());
        var shown = BulkCodeInfoSelection.Filter(results, new HashSet<string> { "INTRODUCED" });
        Assert.Equal(119, shown.Count);
        Assert.Equal(string.Join('\n', Enumerable.Range(1, 120).Where(index => index != 60).Select(Code)),
            BulkCodeInfoSelection.CopyCodes(shown));
    }

    [Fact]
    public async Task ThrottleIsRetriedAndCancellationStopsWork()
    {
        var calls = 0;
        var client = new FakeClient((request, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? Success(request) with { StatusCode = 429, ResponseBody = "{}" }
            : Success(request)));
        var result = await new TrueApiBulkCodeInfoHandler(client).ExecuteAsync(Input([Code(1)]), Context, TestContext.Current.CancellationToken);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, calls);
        Assert.Equal(2, result.Exchanges!.Count);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TrueApiBulkCodeInfoHandler(client)
            .ExecuteAsync(Input([Code(1)]), Context, cancellation.Token));
    }

    [Fact]
    public async Task ConcurrencyIsBoundedByProcessorCountAndFour()
    {
        var active = 0;
        var peak = 0;
        var client = new FakeClient(async (request, token) =>
        {
            var current = Interlocked.Increment(ref active);
            peak = Math.Max(peak, current);
            try { await Task.Delay(20, token); return Success(request); }
            finally { Interlocked.Decrement(ref active); }
        });
        var result = await new TrueApiBulkCodeInfoHandler(client).ExecuteAsync(Input(Enumerable.Range(1, 5000).Select(Code)), Context, TestContext.Current.CancellationToken);
        Assert.Equal(200, result.StatusCode);
        Assert.InRange(peak, 1, Math.Clamp(Environment.ProcessorCount, 1, 4));
    }

    private static IntegrationExchange Success(IntegrationRequest request)
    {
        var codes = JsonSerializer.Deserialize<string[]>(request.Body!)!;
        var body = JsonSerializer.Serialize(codes.Select(code => new CodeInfoItem(new CisInfo
        {
            RequestedCis = code, Cis = code, Status = "INTRODUCED"
        })));
        return new(200, request.Method, request.Url, request.Headers, request.Body, new Dictionary<string, string>(), body, TimeSpan.FromMilliseconds(1));
    }

    private sealed class FakeClient(Func<IntegrationRequest, CancellationToken, Task<IntegrationExchange>> execute) : ITrueApiClient
    {
        public Uri BaseAddress { get; } = new("https://production.trueapi.mock.invalid");
        public ConcurrentBag<IntegrationRequest> Requests { get; } = [];
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return execute(request, cancellationToken);
        }
    }
}
