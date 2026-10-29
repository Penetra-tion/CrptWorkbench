using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class AggregationTreeTests
{
    private static readonly ApiOperationContext Context = new(new(), "tree-correlation");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static string Code(int number) => $"{number:D18}";
    internal static Dictionary<string, string> Input(string code, bool siblings = false) => new()
        { ["code"] = code, ["includeSiblings"] = siblings.ToString().ToLowerInvariant() };
    private static CodeInfoItem Item(string code, string? parent = null, string[]? children = null, string type = "UNIT") =>
        new(new CisInfo { RequestedCis = code, Cis = code, Parent = parent, Child = children ?? [], GeneralPackageType = type, Status = "INTRODUCED" });
    internal static IntegrationExchange Response(IntegrationRequest request, IEnumerable<CodeInfoItem> items) =>
        new(200, request.Method, request.Url, request.Headers, request.Body,
            new Dictionary<string, string> { ["X-Correlation-Id"] = request.CorrelationId },
            JsonSerializer.Serialize(items, JsonOptions), TimeSpan.FromMilliseconds(1));
    private static string[] Codes(IntegrationRequest request) => JsonSerializer.Deserialize<string[]>(request.Body!)!;
    private static async Task<AggregationTreeResult> Run(ITrueApiClient client, string code, bool siblings = false) =>
        AggregationTreeResult.Parse((await new TrueApiAggregationTreeHandler(client)
            .ExecuteAsync(Input(code, siblings), Context, TestContext.Current.CancellationToken)).ResponseBody)!;
    private static FakeClient Graph(params CodeInfoItem[] items)
    {
        var map = items.ToDictionary(i => i.CisInfo!.RequestedCis!);
        return new((r, _) => Task.FromResult(Response(r, Codes(r).Where(map.ContainsKey).Select(c => map[c]))));
    }

    [Theory]
    [InlineData("UNIT")]
    [InlineData("GROUP")]
    [InlineData("SET")]
    [InlineData("BOX")]
    [InlineData("ATK")]
    [InlineData("LEVEL1")]
    [InlineData("BUNDLE")]
    public async Task SupportedTypesAndRootWithoutParent(string type)
    {
        var code = Code(1);
        var tree = await Run(Graph(Item(code, type: type)), code);
        Assert.True(tree.Complete);
        Assert.Equal(code, tree.RootCode);
        Assert.Equal(type, Assert.Single(tree.Nodes).PackageType);
        Assert.Equal(new[] { code }, Assert.Single(tree.ExportPaths()));
    }

    [Fact]
    public void ParserNormalizesGsAndDefaultsCheckboxOff()
    {
        var parsed = TrueApiAggregationTreeHandler.Parse(new Dictionary<string, string> { ["code"] = Code(1) + "\u001D91SECRET" });
        Assert.Equal(Code(1), parsed.Code);
        Assert.False(parsed.IncludeSiblings);
        Assert.Throws<ArgumentException>(() => TrueApiAggregationTreeHandler.Parse(Input("bad")));
        Assert.Throws<ArgumentException>(() => TrueApiAggregationTreeHandler.Parse(Input(Code(1) + "\n" + Code(2))));
        Assert.Throws<ArgumentException>(() => TrueApiAggregationTreeHandler.Parse(Input("(01)04600000000000(21)SERIAL")));
        var badFlag = Input(Code(1));
        badFlag["includeSiblings"] = "unexpected";
        Assert.Throws<ArgumentException>(() => TrueApiAggregationTreeHandler.Parse(badFlag));
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 6)]
    public async Task ParentChainSubtreeAndSiblingModeCacheEveryCodeOnce(bool siblings, int expected)
    {
        var client = Graph(Item(Code(1), children: [Code(2), Code(5)], type: "BOX"),
            Item(Code(2), Code(1), [Code(3)], "GROUP"), Item(Code(3), Code(2), [Code(4)], "SET"),
            Item(Code(4), Code(3)), Item(Code(5), Code(1), [Code(6)], "BOX"), Item(Code(6), Code(5)));
        var tree = await Run(client, Code(3), siblings);
        Assert.True(tree.Complete);
        Assert.Equal(Code(1), tree.RootCode);
        Assert.Equal(expected, tree.Nodes.Count);
        Assert.Equal(expected, client.Requests.SelectMany(Codes).Count());
        Assert.Equal(expected, client.Requests.SelectMany(Codes).Distinct().Count());
        Assert.All(client.Requests, r =>
        {
            Assert.Equal(TrueApiAggregationTreeHandler.OperationId, r.OperationId);
            Assert.Equal(Context.CorrelationId, r.CorrelationId);
            Assert.Equal(Context.CorrelationId, r.Headers["X-Correlation-Id"]);
            Assert.Equal("POST", r.Method);
            Assert.EndsWith("/cises/info", r.Url);
            Assert.DoesNotContain("?", r.Url);
        });
        Assert.Equal(siblings ? 0 : 1, tree.Nodes.Single(n => n.Code == Code(1)).OmittedChildren);
        Assert.Equal(siblings ? 2 : 1, tree.ExportPaths().Count());
    }

    [Theory]
    [InlineData("010460000000000021TREE000000001")]
    [InlineData("046000000000000201")]
    [InlineData("000000000000000201")]
    public async Task CoherentMockTreeSupportsLeafGroupAndBox(string code)
    {
        var tree = await Run(new MockTrueApiClient(), code, true);
        Assert.True(tree.Complete);
        Assert.Equal("000000000000000200", tree.RootCode);
        Assert.Contains(tree.Nodes, n => n.PackageType == "SET");
        Assert.True(tree.CanExport);
    }

    [Fact]
    public async Task RepeatedChildReferencesDoNotDuplicateFetchOrExport()
    {
        var client = Graph(Item(Code(1), children: [Code(2), Code(2)], type: "BOX"), Item(Code(2), Code(1)));
        var tree = await Run(client, Code(1));
        Assert.True(tree.Complete);
        Assert.Equal(2, client.Requests.SelectMany(Codes).Count());
        Assert.Single(tree.Links);
        Assert.Single(tree.ExportPaths());
    }

    [Theory]
    [InlineData(100000, true)]
    [InlineData(100001, true)]
    public async Task LargeTreeHasNoApplicationCapAndBoundsBatchesAndConcurrency(int total, bool complete)
    {
        var active = 0;
        var peak = 0;
        var children = Enumerable.Range(2, total - 1).Select(Code).ToArray();
        var client = new FakeClient(async (r, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref peak); } while (observed < current && Interlocked.CompareExchange(ref peak, current, observed) != observed);
            try
            {
                await Task.Delay(1, ct);
                return Response(r, Codes(r).Select(c => c == Code(1)
                    ? Item(c, children: children, type: "BOX") : Item(c, Code(1))));
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var tree = await Run(client, Code(1));
        Assert.Equal(total, tree.Nodes.Count);
        Assert.Equal(total, client.Requests.SelectMany(Codes).Distinct().Count());
        Assert.All(client.Requests, r => Assert.InRange(Codes(r).Length, 1, 1000));
        Assert.InRange(peak, 1, 4);
        Assert.Equal(complete, tree.Complete);
        Assert.Equal(complete ? 0 : 1, tree.Nodes.Single(n => n.Code == Code(1)).OmittedChildren);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unexpected")]
    [InlineData("identifier-mismatch")]
    public async Task MalformedMissingAmbiguousAndUnexpectedRepliesCannotExport(string kind)
    {
        var client = new FakeClient((r, _) =>
        {
            var items = kind switch
            {
                "duplicate" => new[] { Item(Code(1)), Item(Code(1)) },
                "unexpected" => new[] { Item(Code(1)), Item(Code(2)) },
                "identifier-mismatch" => new[] { new CodeInfoItem(Item(Code(1)).CisInfo! with { Cis = Code(2) }) },
                _ => new[] { Item(Code(2)) }
            };
            var response = Response(r, items);
            if (kind == "invalid") response = response with { ResponseBody = "{broken-json" };
            if (kind == "missing") response = response with { ResponseBody = "[]" };
            return Task.FromResult(response);
        });
        var tree = await Run(client, Code(1));
        Assert.False(tree.Complete);
        Assert.False(tree.CanExport);
        Assert.NotNull(Assert.Single(tree.Nodes).Error);
    }

    [Theory]
    [InlineData("BOX")]
    [InlineData("GROUP")]
    [InlineData("SET")]
    [InlineData("ATK")]
    [InlineData("LEVEL1")]
    [InlineData("BUNDLE")]
    public async Task MissingPackageCompositionIsNotAnEmptyLeaf(string type)
    {
        var item = new CodeInfoItem(Item(Code(1), type: type).CisInfo! with { Child = null });
        var tree = await Run(Graph(item), Code(1));
        Assert.False(tree.Complete);
        Assert.False(tree.CanExport);
    }

    [Fact]
    public async Task InvalidRelatedCodeIsDiagnosedWithoutFetch()
    {
        var client = Graph(Item(Code(1), children: ["bad"], type: "BOX"));
        var tree = await Run(client, Code(1));
        Assert.False(tree.Complete);
        Assert.False(tree.CanExport);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task CycleCannotExportAndTraversalTerminates()
    {
        var client = Graph(Item(Code(1), Code(2), [Code(2)], "BOX"), Item(Code(2), Code(1), [Code(1)], "BOX"));
        var tree = await Run(client, Code(1), true);
        Assert.False(tree.Complete);
        Assert.False(tree.CanExport);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(tree.Nodes, n => n.Error!.Contains("Цикл"));
        Assert.Equal(2, tree.VisibleRows(new HashSet<string> { Code(1), Code(2) }).Count);
    }

    [Fact]
    public async Task ContradictoryOrMultipleParentsExcludeOnlyDisputedPaths()
    {
        var client = Graph(Item(Code(1), children: [Code(2), Code(3), Code(5)], type: "BOX"),
            Item(Code(2), Code(1), [Code(4)], "GROUP"), Item(Code(3), Code(1), [Code(4)], "GROUP"),
            Item(Code(4), Code(2)), Item(Code(5), Code(1)));
        var tree = await Run(client, Code(1));
        Assert.False(tree.Complete);
        Assert.NotNull(tree.Nodes.Single(n => n.Code == Code(4)).Error);
        Assert.All(tree.Links.Where(l => l.Child == Code(4)), l => Assert.False(l.Confirmed));
        Assert.Equal(new[] { Code(5), Code(1) }, Assert.Single(tree.ExportPaths()));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AccessFailureStopsSchedulingAndKeepsKnownParent(int status)
    {
        var client = new FakeClient((r, _) => Task.FromResult(Codes(r)[0] == Code(1)
            ? Response(r, [Item(Code(1), children: [Code(2)], type: "BOX")])
            : Response(r, []) with { StatusCode = status }));
        var tree = await Run(client, Code(1));
        Assert.False(tree.Complete);
        Assert.True(tree.Nodes.Single(n => n.Code == Code(1)).Loaded);
        Assert.NotNull(tree.Nodes.Single(n => n.Code == Code(2)).Error);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(tree.Diagnostics, d => d.Contains("401/403"));
    }

    [Fact]
    public async Task ConcurrentAccessFailureStopsAnAlreadyWaitingThrottleRetry()
    {
        var concurrent = Environment.ProcessorCount > 1;
        var throttled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var denied = 0;
        var callsAfterDenial = 0;
        var children = Enumerable.Range(2, 1001).Select(Code).ToArray();
        var client = new FakeClient(async (request, ct) =>
        {
            if (Volatile.Read(ref denied) != 0) Interlocked.Increment(ref callsAfterDenial);
            var codes = Codes(request);
            if (codes.Length == 1 && codes[0] == Code(1))
                return Response(request, [Item(Code(1), children: children, type: "BOX")]);
            if (concurrent && codes.Length == 1000)
            {
                throttled.TrySetResult();
                return Response(request, []) with { StatusCode = 429 };
            }
            if (concurrent) await throttled.Task.WaitAsync(ct);
            Volatile.Write(ref denied, 1);
            return Response(request, []) with { StatusCode = 403 };
        });
        var tree = await Run(client, Code(1));
        Assert.False(tree.Complete);
        Assert.Equal(1, Volatile.Read(ref denied));
        Assert.Equal(0, Volatile.Read(ref callsAfterDenial));
        Assert.Equal(concurrent ? 3 : 2, client.Requests.Count);
        Assert.Contains(tree.Diagnostics, d => d.Contains("401/403"));
    }

    [Fact]
    public async Task ProgressReportsStageAndProcessedCountWithoutRefetch()
    {
        var progress = new ProgressRecorder();
        var client = Graph(Item(Code(1), children: [Code(2)], type: "BOX"), Item(Code(2), Code(1)));
        var context = Context with { Progress = progress };
        await new TrueApiAggregationTreeHandler(client).ExecuteAsync(Input(Code(2), true), context, TestContext.Current.CancellationToken);
        Assert.Equal(2, progress.Items.Count);
        Assert.Equal(new[] { 1, 2 }, progress.Items.Select(p => p.ProcessedCodes));
        Assert.All(progress.Items, p => Assert.False(string.IsNullOrWhiteSpace(p.Stage)));
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task TransportExceptionAndHttpFailureKeepSafePartialResultWithoutRetry()
    {
        var client = new FakeClient((r, _) => throw new HttpRequestException("transport-private-secret"));
        var tree = await Run(client, Code(1));
        Assert.False(tree.Complete);
        Assert.Single(client.Requests);
        Assert.DoesNotContain("transport-private-secret", JsonSerializer.Serialize(tree));
        var unavailable = new FakeClient((r, _) => Task.FromResult(Response(r, []) with { StatusCode = 503 }));
        var failedTree = await Run(unavailable, Code(1));
        Assert.False(failedTree.Complete);
        Assert.Single(unavailable.Requests);
        Assert.Contains("503", Assert.Single(failedTree.Nodes).Error!);
    }

    [Fact]
    public async Task ParentCompositionDisagreesWithChildParentAndInvalidatesPath()
    {
        var client = Graph(Item(Code(1), children: [], type: "BOX"), Item(Code(2), Code(1)));
        var tree = await Run(client, Code(2));
        Assert.False(tree.Complete);
        Assert.NotNull(tree.Nodes.Single(n => n.Code == Code(2)).Error);
        Assert.False(Assert.Single(tree.Links).Confirmed);
        Assert.False(tree.CanExport);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrottlingRetriesExactlyTwiceAndOtherFailuresRemainPartial(bool exhausted)
    {
        var calls = 0;
        var client = new FakeClient((r, _) => Task.FromResult(Response(r, [Item(Code(1))]) with
            { StatusCode = Interlocked.Increment(ref calls) < 3 || exhausted ? 429 : 200 }));
        var tree = await Run(client, Code(1));
        Assert.Equal(3, calls);
        Assert.Equal(!exhausted, tree.Complete);
    }

    [Fact]
    public async Task CancellationDuringTransportOrThrottlePropagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new FakeClient((r, _) => { cts.Cancel(); return Task.FromResult(Response(r, []) with { StatusCode = 429 }); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TrueApiAggregationTreeHandler(client)
            .ExecuteAsync(Input(Code(1)), Context, cts.Token));
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task TxtUsesConfirmedVariableDepthPathsCrLfAndNoBom()
    {
        var tree = await Run(Graph(Item(Code(1), children: [Code(2), Code(4), Code(5)], type: "BOX"),
            Item(Code(2), Code(1), [Code(3)], "GROUP"), Item(Code(3), Code(2)), Item(Code(4), Code(1)),
            new CodeInfoItem(new CisInfo { RequestedCis = Code(5) }, "404", "not found")), Code(1));
        Assert.False(tree.Complete);
        using var stream = new MemoryStream();
        await tree.WriteTxtAsync(stream, TestContext.Current.CancellationToken);
        var expected = $"{Code(4)}\t{Code(1)}\r\n{Code(3)}\t{Code(2)}\t{Code(1)}\r\n";
        Assert.Equal(new UTF8Encoding(false).GetBytes(expected), stream.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(65536)]
    public async Task StreamedTxtMatchesExactBytesForSmallAndLargeReadChunks(int chunkSize)
    {
        var tree = await Run(Graph(Item(Code(1), children: [Code(2), Code(3)], type: "BOX"),
            Item(Code(2), Code(1)), Item(Code(3), Code(1))), Code(1));
        using var expected = new MemoryStream();
        await tree.WriteTxtAsync(expected, TestContext.Current.CancellationToken);
        using var source = tree.OpenTxtStream(TestContext.Current.CancellationToken);
        Assert.True(source.CanRead);
        Assert.False(source.CanSeek);
        Assert.False(source.CanWrite);
        Assert.Throws<NotSupportedException>(() => source.Seek(0, SeekOrigin.Begin));
        Assert.Equal(0, await source.ReadAsync(Memory<byte>.Empty, TestContext.Current.CancellationToken));
        using var received = new MemoryStream();
        var buffer = new byte[chunkSize];
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken)) > 0)
            received.Write(buffer, 0, count);
        Assert.Equal(expected.ToArray(), received.ToArray());
        Assert.Equal(0, source.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public async Task StreamedTxtCancellationHonorsSourceAndReadTokens()
    {
        var tree = await Run(Graph(Item(Code(1))), Code(1));
        using var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = tree.OpenTxtStream(sourceCancellation.Token);
        var buffer = new byte[1];
        Assert.Equal(1, source.Read(buffer, 0, 1));
        sourceCancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => source.Read(buffer, 0, 1));
        using var other = tree.OpenTxtStream(TestContext.Current.CancellationToken);
        using var readCancellation = new CancellationTokenSource();
        readCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            Assert.Equal(1, await other.ReadAsync(buffer.AsMemory(), readCancellation.Token)));
    }

    [Fact]
    public async Task SingleCodeTxtHasOneColumnAndUnavailableRootHasNoRows()
    {
        var tree = await Run(Graph(Item(Code(1))), Code(1));
        using var source = tree.OpenTxtStream(TestContext.Current.CancellationToken);
        using var target = new MemoryStream();
        await source.CopyToAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal(Encoding.UTF8.GetBytes(Code(1) + "\r\n"), target.ToArray());
        var missing = tree with { Nodes = [new(Code(1), null, null, false, "missing", 0, 0)], Complete = false };
        using var emptySource = missing.OpenTxtStream(TestContext.Current.CancellationToken);
        Assert.Equal(0, emptySource.Read(new byte[1], 0, 1));
        Assert.False(missing.CanExport);
    }

    [Fact]
    public void DeepVisibleRowsUseIterativeTraversalAndCollapsedBranchesStaySmall()
    {
        const int count = 20000;
        var nodes = Enumerable.Range(1, count).Select(i => new AggregationTreeNode(Code(i), "BOX", "INTRODUCED", true, null,
            i == count ? 0 : 1, 0)).ToArray();
        var links = Enumerable.Range(1, count - 1).Select(i => new AggregationTreeLink(Code(i), Code(i + 1), true)).ToArray();
        var tree = new AggregationTreeResult(Code(1), true, Code(1), nodes, links, true, []);
        Assert.Single(tree.VisibleRows(new HashSet<string>()));
        var expanded = nodes.Select(n => n.Code).ToHashSet();
        var rows = tree.VisibleRows(expanded);
        Assert.Equal(count, rows.Count);
        Assert.Equal(count - 1, rows[^1].Depth);
        Assert.Equal(count, Assert.Single(tree.ExportPaths()).Count);
    }

    internal sealed class FakeClient(Func<IntegrationRequest, CancellationToken, Task<IntegrationExchange>> execute) : ITrueApiClient
    {
        public Uri BaseAddress { get; } = new("https://trueapi.mock.invalid/api/v3/true-api");
        public ConcurrentBag<IntegrationRequest> Requests { get; } = [];
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return execute(request, cancellationToken); }
    }

    private sealed class ProgressRecorder : IProgress<ApiOperationProgress>
    {
        public ConcurrentQueue<ApiOperationProgress> Items { get; } = [];
        public void Report(ApiOperationProgress value) => Items.Enqueue(value);
    }
}
