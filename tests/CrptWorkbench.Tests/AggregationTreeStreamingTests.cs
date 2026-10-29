using Xunit;
using System.Net;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Options;

namespace CrptWorkbench.Tests;

public sealed class AggregationTreeStreamingTests
{
    private const string Root = "000000000000000200";
    private const string Child = "010460000000000021TREE000000001";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChildArrayBeforeRequestedCodeStreamsInBoundedBatches()
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartArray(); writer.WriteStartObject(); writer.WriteStartObject("cisInfo");
            writer.WriteStartArray("child");
            for (var i = 0; i < 120_001; i++) writer.WriteStringValue(Child);
            writer.WriteEndArray(); writer.WriteString("packageType", "UNIT");
            writer.WriteString("requestedCis", Root); writer.WriteString("generalPackageType", "BOX");
            writer.WriteString("cis", Root); writer.WriteString("status", "INTRODUCED");
            writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray();
        }
        payload.Position = 0;
        using var chunked = new ChunkedStream(payload, 31);
        var sink = new Sink();
        await AggregationTreeResponseReader.ReadAsync(chunked, new HashSet<string> { Root }, sink, Ct);
        var node = Assert.Single(sink.Nodes);
        Assert.Equal("BOX", node.PackageType);
        Assert.True(node.ChildrenKnown);
        Assert.Equal(120_001, node.ChildCount);
        Assert.Equal(120_001, sink.Children);
        Assert.InRange(sink.MaxBatch, 1, 1000);
        Assert.Null(node.Error);
    }

    [Theory]
    [InlineData("[")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"cisInfo\":{\"child\":[]}}]")]
    [InlineData("[{\"cisInfo\":{\"requestedCis\":\"000000000000000201\"}}]")]
    [InlineData("[{\"cisInfo\":{\"requestedCis\":\"000000000000000200\"}},{\"cisInfo\":{\"requestedCis\":\"000000000000000200\"}}]")]
    [InlineData("[{\"cisInfo\":{\"requestedCis\":\"000000000000000200\",\"requestedCis\":\"000000000000000200\"}}]")]
    [InlineData("[] []")]
    public async Task InvalidOrAmbiguousResponseRejectsEntireBatch(string json)
    {
        using var stream = new ChunkedStream(new MemoryStream(Encoding.UTF8.GetBytes(json)), 3);
        await Assert.ThrowsAsync<InvalidDataException>(() => AggregationTreeResponseReader.ReadAsync(stream,
            new HashSet<string> { Root }, new Sink(), Ct));
    }

    [Theory]
    [InlineData("{\"requestedCis\":\"000000000000000200\",\"packageType\":\"BOX\"}")]
    [InlineData("{\"requestedCis\":\"000000000000000200\",\"packageType\":\"BOX\",\"child\":[\"bad\"]}")]
    [InlineData("{\"requestedCis\":\"000000000000000200\",\"packageType\":\"UNIT\",\"parent\":\"bad\"}")]
    [InlineData("{\"requestedCis\":\"000000000000000200\",\"packageType\":\"UNIT\",\"cis\":\"000000000000000201\"}")]
    public async Task InvalidRelationsAndMissingCompositionBecomeNodeErrors(string info)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[{\"cisInfo\":" + info + "}]"));
        var sink = new Sink();
        await AggregationTreeResponseReader.ReadAsync(stream, new HashSet<string> { Root }, sink, Ct);
        var node = Assert.Single(sink.Nodes);
        Assert.NotNull(node.Error);
        Assert.Null(node.Parent);
        Assert.Equal(0, sink.Children);
    }

    [Fact]
    public async Task StreamTransportRefreshesUnauthorizedWithoutBufferingOrRetainingBody()
    {
        var calls = 0;
        var tokens = new Tokens();
        var handler = new Handler(_ => new HttpResponseMessage(++calls == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
        { Content = new StreamingOnlyContent(calls == 1 ? "expired" : "[]") });
        var client = new RealTrueApiClient(new Factory(handler), tokens, Options.Create(new CrptIntegrationOptions
        { Mode = CrptIntegrationMode.Production, TrueApiBaseUrl = CrptIntegrationOptions.ProductionTrueApiBaseUrl }));
        using var output = new MemoryStream();
        var exchange = await ((ITrueApiClient)client).ExecuteToStreamAsync(Request(), output, Ct);
        Assert.Equal("[]", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Null(exchange.ResponseBody);
        Assert.Equal("Bearer ********", exchange.RequestHeaders["Authorization"]);
        Assert.Equal(2, calls);
        Assert.Equal(1, tokens.Invalidations);
    }

    [Fact]
    public async Task StreamTransportRejectsUnapprovedRequestBeforeAuthentication()
    {
        var tokens = new Tokens();
        var client = new RealTrueApiClient(new Factory(new Handler(_ => throw new Exception("HTTP must not run"))),
            tokens, Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production,
                TrueApiBaseUrl = CrptIntegrationOptions.ProductionTrueApiBaseUrl }));
        using var output = new MemoryStream();
        foreach (var request in new[] { Request() with { OperationId = "true-api.code-info" },
            Request() with { Url = Request().Url + "?pg=milk" }, Request() with { Body = "[]" } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => ((ITrueApiClient)client).ExecuteToStreamAsync(request, output, Ct));
        Assert.Equal(0, tokens.Calls);
    }

    [Fact]
    public async Task ErrorWrapperAndSerialBracketsRetainOrdinaryFoundSemantics()
    {
        const string serial = "010460000000000021A(01)B(21)C";
        var json = "[{\"errorCode\":\"404\",\"cisInfo\":{\"requestedCis\":\"000000000000000200\",\"packageType\":\"UNIT\"}}," +
            "{\"cisInfo\":{\"requestedCis\":\"" + serial + "\",\"packageType\":\"UNIT\",\"parent\":null,\"unknown\":{\"a\":[1,2,3]}}}]";
        using var stream = new ChunkedStream(new MemoryStream(Encoding.UTF8.GetBytes(json)), 7);
        var sink = new Sink();
        await AggregationTreeResponseReader.ReadAsync(stream, new HashSet<string> { Root, serial }, sink, Ct);
        Assert.NotNull(sink.Nodes[0].Error);
        Assert.Null(sink.Nodes[1].Error);
        Assert.Equal(serial, sink.Nodes[1].Code);
        Assert.False(sink.Nodes[1].ChildrenKnown);
    }

    [Fact]
    public async Task OversizedTokenIsRejectedWithoutGrowingTheInputBuffer()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[{\"ignored\":\"" + new string('x', 70_000) + "\"}]"));
        await Assert.ThrowsAsync<InvalidDataException>(() => AggregationTreeResponseReader.ReadAsync(stream,
            new HashSet<string> { Root }, new Sink(), Ct));
    }

    [Fact]
    public async Task ParserCancellationDoesNotPublishTheCurrentItem()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[{\"cisInfo\":{\"requestedCis\":\"000000000000000200\"}}]"));
        var sink = new Sink();
        await Assert.ThrowsAsync<OperationCanceledException>(() => AggregationTreeResponseReader.ReadAsync(stream,
            new HashSet<string> { Root }, sink, cancellation.Token));
        Assert.Empty(sink.Nodes);
        Assert.Equal(0, sink.Children);
    }
    [Theory]
    [InlineData(403, CrptAuthenticationMode.Auto, 1)]
    [InlineData(503, CrptAuthenticationMode.Auto, 1)]
    [InlineData(401, CrptAuthenticationMode.Auto, 2)]
    [InlineData(401, CrptAuthenticationMode.Manual, 1)]
    public async Task FinalHttpErrorsRemainStreamedAndAuthenticationRetriesStayBounded(int status,
        CrptAuthenticationMode mode, int expectedCalls)
    {
        var calls = 0;
        var tokens = new Tokens();
        var handler = new Handler(_ => { calls++; return new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StreamingOnlyContent("{\"error_message\":\"failure\"}") }; });
        var client = new RealTrueApiClient(new Factory(handler), tokens, Options.Create(new CrptIntegrationOptions
        { Mode = CrptIntegrationMode.Production, TrueApiBaseUrl = CrptIntegrationOptions.ProductionTrueApiBaseUrl,
            TrueApiAuthMode = mode }));
        using var output = new MemoryStream();
        var exchange = await client.ExecuteToStreamAsync(Request(), output, Ct);
        Assert.Equal(status, exchange.StatusCode);
        Assert.Equal("{\"error_message\":\"failure\"}", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Null(exchange.ResponseBody);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(expectedCalls - 1, tokens.Invalidations);
    }
    [Fact]
    public async Task InvalidUtf8ResponseIsRejectedAsInvalidData()
    {
        var prefix = Encoding.UTF8.GetBytes("[{\"cisInfo\":{\"requestedCis\":\"");
        var suffix = Encoding.UTF8.GetBytes("\",\"packageType\":\"UNIT\"}}]");
        using var stream = new MemoryStream(prefix.Concat(new byte[] { 0xff }).Concat(suffix).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => AggregationTreeResponseReader.ReadAsync(stream,
            new HashSet<string> { Root }, new Sink(), Ct));
    }
    [Fact]
    public async Task StalledBodyHonorsHttpTimeoutAndDisposesResponse()
    {
        var stream = new StalledStream();
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stream) });
        var client = new RealTrueApiClient(new Factory(handler, TimeSpan.FromMilliseconds(100)),
            new Tokens(), Options.Create(new CrptIntegrationOptions
            { Mode = CrptIntegrationMode.Production, TrueApiBaseUrl = CrptIntegrationOptions.ProductionTrueApiBaseUrl }));
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ExecuteToStreamAsync(Request(), output, Ct).WaitAsync(TimeSpan.FromSeconds(3), Ct));
        Assert.True(stream.Disposed);
    }

    private sealed class StalledStream : MemoryStream
    {
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static IntegrationRequest Request() => new("true-api.aggregation-tree", "POST",
        CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/cises/info", new Dictionary<string, string>
        { ["X-Correlation-Id"] = "stream-test" }, "[\"" + Root + "\"]", "stream-test", new(Guid.NewGuid(), "0000000000", "thumb"));

    private sealed class Sink : IAggregationTreeResponseSink
    {
        public List<AggregationResponseNode> Nodes { get; } = [];
        public long Children { get; private set; }
        public int MaxBatch { get; private set; }
        public ValueTask AddChildrenAsync(string code, IReadOnlyList<string> children, CancellationToken ct)
        { Assert.Equal(Root, code); Children += children.Count; MaxBatch = Math.Max(MaxBatch, children.Count); return ValueTask.CompletedTask; }
        public ValueTask AddNodeAsync(AggregationResponseNode node, CancellationToken ct)
        { Nodes.Add(node); return ValueTask.CompletedTask; }
    }

    private sealed class ChunkedStream(Stream inner, int chunk) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, chunk));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], ct);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class StreamingOnlyContent(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new Exception("Buffering is forbidden");
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ChunkedStream(new MemoryStream(Encoding.UTF8.GetBytes(body)), 2));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => CreateContentReadStreamAsync();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
    private sealed class Factory(HttpMessageHandler handler, TimeSpan? timeout = null) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false) { Timeout = timeout ?? TimeSpan.FromSeconds(100) }; }
    private sealed class Tokens : ITrueApiTokenProvider
    {
        public int Calls { get; private set; } public int Invalidations { get; private set; }
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext auth, string id, CancellationToken ct = default) => Task.FromResult("token-" + ++Calls);
        public void Invalidate(IntegrationAuthenticationContext auth) => Invalidations++;
    }
}






