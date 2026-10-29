using System.Collections.Concurrent;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class CodeDocumentConcurrencyTests
{
    private const string First = "010460000000000021DEMO000000001";
    private const string Second = "010460000000000021DEMO000000002";
    private static ApiOperationContext Context => new(new(), "document-concurrency");

    [Fact]
    public async Task SingleHistoryUsesFourDocumentSlotsAndPreservesDuplicateEventOrder()
    {
        var client = new GatedClient();
        var operation = new TrueApiCodeInfoHandler(client).ExecuteAsync(Input(First), Context, TestContext.Current.CancellationToken);
        try
        {
            await client.FourDocumentsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(4, client.Active);
        }
        finally { client.Release.TrySetResult(); }

        var exchange = await operation;
        var history = Assert.Single(CodeInfoResults.Parse(exchange.ResponseBody)!.Items).DocumentHistory!;
        Assert.Equal(new[] { "doc-3", "doc-1", "doc-2", "doc-4", "doc-5", "doc-3" }, history.Documents.Select(x => x.DocumentId));
        Assert.True(history.Paid);
        Assert.Null(history.Warning);
        Assert.Equal(4, client.Maximum);
        Assert.Equal(5, client.Documents.Count);
        Assert.All(client.Documents.GroupBy(x => x), group => Assert.Single(group));
        Assert.Equal(7, exchange.Exchanges!.Count);
    }

    [Fact]
    public async Task MultipleHistoriesShareDocumentsAndKeepTheSameGlobalFourSlotBound()
    {
        var client = new GatedClient();
        var operation = new TrueApiCodeInfoHandler(client).ExecuteAsync(Input(First + "\n" + Second), Context, TestContext.Current.CancellationToken);
        try
        {
            await client.FourDocumentsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(4, client.Active);
        }
        finally { client.Release.TrySetResult(); }

        var exchange = await operation;
        var items = CodeInfoResults.Parse(exchange.ResponseBody)!.Items;
        Assert.Equal(new[] { First, Second }, items.Select(x => x.CisInfo!.RequestedCis));
        Assert.All(items, item => Assert.Equal(6, item.DocumentHistory!.Documents.Count));
        Assert.Equal(5, client.Documents.Count);
        Assert.Equal(4, client.Maximum);
        Assert.Equal(8, exchange.Exchanges!.Count);
    }

    [Fact]
    public async Task CancellationStopsQueuedDocumentRequests()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new GatedClient();
        var operation = new TrueApiCodeInfoHandler(client).ExecuteAsync(Input(First), Context, cancellation.Token);
        try
        {
            await client.FourDocumentsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(4, client.Documents.Count);
            Assert.Equal(0, client.Active);
        }
        finally { client.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("doc-3", true)]
    [InlineData("doc-5", null)]
    public async Task MissingDetailKeepsKnownPaymentOrUnknownInsteadOfFalse(string missingDocument, bool? expectedPaid)
    {
        var client = new GatedClient { MissingDocument = missingDocument };
        client.Release.TrySetResult();
        var exchange = await new TrueApiCodeInfoHandler(client).ExecuteAsync(Input(First), Context, TestContext.Current.CancellationToken);
        var history = Assert.Single(CodeInfoResults.Parse(exchange.ResponseBody)!.Items).DocumentHistory!;
        Assert.Equal(expectedPaid, history.Paid);
        Assert.NotNull(history.Warning);
        Assert.Equal(6, history.Documents.Count);
        Assert.Null(history.Documents.First(x => x.DocumentId == missingDocument).Type);
        Assert.All(client.Documents.GroupBy(x => x), group => Assert.Single(group));
    }

    private static Dictionary<string, string> Input(string codes) => new() { ["codes"] = codes, ["loadDocuments"] = "true" };

    private sealed class GatedClient : ITrueApiClient
    {
        public Uri BaseAddress { get; } = new("https://documents.mock.invalid/");
        public TaskCompletionSource FourDocumentsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Documents { get; } = new();
        public string? MissingDocument { get; init; }
        private int active;
        private int maximum;
        public int Active => Volatile.Read(ref active);
        public int Maximum => Volatile.Read(ref maximum);

        public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken)
        {
            string body;
            var status = 200;
            if (request.Url.Contains("/cises/info", StringComparison.Ordinal))
            {
                var codes = JsonSerializer.Deserialize<string[]>(request.Body!)!;
                body = JsonSerializer.Serialize(codes.Select(code => new CodeInfoItem(new CisInfo { RequestedCis = code, Cis = code, ProductGroup = "milk" })));
            }
            else if (request.Url.Contains("/cises/history?", StringComparison.Ordinal))
                body = """[{"docId":"doc-3"},{"docId":"doc-1"},{"docId":"doc-2"},{"docId":"doc-4"},{"docId":"doc-5"},{"docId":"doc-3"}]""";
            else
            {
                Assert.Equal(TrueApiDocumentStatusHandler.OperationId, request.OperationId);
                Assert.Equal("GET", request.Method);
                Assert.EndsWith("/info?body=false&content=false", request.Url);
                Documents.Enqueue(request.Url);
                var count = Interlocked.Increment(ref active);
                int old;
                do { old = Volatile.Read(ref maximum); }
                while (count > old && Interlocked.CompareExchange(ref maximum, count, old) != old);
                if (Documents.Count == 4) FourDocumentsEntered.TrySetResult();
                try { await Release.Task.WaitAsync(cancellationToken); }
                finally { Interlocked.Decrement(ref active); }
                body = request.Url.Contains("doc-5/", StringComparison.Ordinal)
                    ? """[{"type":"PAYMENT","status":"CHECKED_OK"}]"""
                    : """[{"type":"AGGREGATION_DOCUMENT","status":"CHECKED_OK"}]""";
                if (MissingDocument is not null && request.Url.Contains(MissingDocument + "/", StringComparison.Ordinal))
                { status = 503; body = "{}"; }
            }
            return new(status, request.Method, request.Url, request.Headers, request.Body,
                new Dictionary<string, string> { ["X-Correlation-Id"] = request.CorrelationId, ["X-CRPT-Workbench-Mock"] = "true" },
                body, TimeSpan.FromMilliseconds(1));
        }
    }
}
