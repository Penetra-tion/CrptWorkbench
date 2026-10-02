using System.Text;
using System.Text.Json;

namespace CrptWorkbench.Application;

public sealed record AggregationTreeRequest(string Code, bool IncludeSiblings);
public sealed record ApiOperationProgress(string Stage, int ProcessedCodes);
public sealed record AggregationTreeNode(string Code, string? PackageType, string? Status,
    bool Loaded, string? Error, int ChildCount, int OmittedChildren);
public sealed record AggregationTreeLink(string Parent, string Child, bool Confirmed, string? Error = null);
public sealed record AggregationTreeRow(AggregationTreeNode Node, int Depth, bool HasChildren);

public sealed record AggregationTreeResult(string RequestedCode, bool IncludeSiblings, string RootCode,
    IReadOnlyList<AggregationTreeNode> Nodes, IReadOnlyList<AggregationTreeLink> Links,
    bool Complete, IReadOnlyList<string> Diagnostics)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AggregationTreeResult? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<AggregationTreeResult>(body, JsonOptions);
            return result is not null && !string.IsNullOrWhiteSpace(result.RequestedCode) &&
                !string.IsNullOrWhiteSpace(result.RootCode) && result.Nodes is not null && result.Links is not null &&
                result.Diagnostics is not null && result.Nodes.Count <= TrueApiAggregationTreeHandler.MaxCodes &&
                result.Nodes.All(n => n is not null && !string.IsNullOrWhiteSpace(n.Code)) &&
                result.Nodes.Select(n => n.Code).Distinct(StringComparer.Ordinal).Count() == result.Nodes.Count &&
                result.Links.All(l => l is not null && !string.IsNullOrWhiteSpace(l.Parent) && !string.IsNullOrWhiteSpace(l.Child))
                ? result : null;
        }
        catch (JsonException) { return null; }
    }

    public IReadOnlyList<AggregationTreeRow> VisibleRows(IReadOnlySet<string> expanded)
    {
        var nodes = Nodes.ToDictionary(n => n.Code, StringComparer.Ordinal);
        var children = Links.GroupBy(l => l.Parent, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Child).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var rows = new List<AggregationTreeRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(string Code, int Depth)>();
        stack.Push((RootCode, 0));
        while (stack.TryPop(out var entry))
        {
            if (!seen.Add(entry.Code) || !nodes.TryGetValue(entry.Code, out var node)) continue;
            var childCodes = children.GetValueOrDefault(entry.Code) ?? [];
            rows.Add(new(node, entry.Depth, childCodes.Length > 0));
            if (!expanded.Contains(entry.Code)) continue;
            for (var i = childCodes.Length - 1; i >= 0; i--) stack.Push((childCodes[i], entry.Depth + 1));
        }
        return rows;
    }

    // A missing parent/child or a disputed edge invalidates the whole exported path.
    public IEnumerable<IReadOnlyList<string>> ExportPaths()
    {
        var nodes = Nodes.ToDictionary(n => n.Code, StringComparer.Ordinal);
        var parents = Links.GroupBy(l => l.Child, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var children = Links.GroupBy(l => l.Parent, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        if (!nodes.TryGetValue(RootCode, out var root) || !Valid(root)) yield break;
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(RootCode);
        while (queue.TryDequeue(out var code))
        {
            if (!reachable.Add(code)) continue;
            if (!children.TryGetValue(code, out var links)) continue;
            foreach (var link in links)
                if (link.Confirmed && parents[link.Child].Length == 1 &&
                    nodes.TryGetValue(link.Child, out var node) && Valid(node)) queue.Enqueue(link.Child);
        }
        foreach (var node in Nodes)
        {
            if (!reachable.Contains(node.Code) || node.ChildCount != 0 || node.OmittedChildren != 0 ||
                children.ContainsKey(node.Code)) continue;
            var path = new List<string>();
            var code = node.Code;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(code))
            {
                path.Add(code);
                if (code == RootCode) { yield return path; break; }
                if (!parents.TryGetValue(code, out var links) || links.Length != 1 || !links[0].Confirmed) break;
                code = links[0].Parent;
            }
        }
    }

    public bool CanExport => ExportPaths().Any();

    public Stream OpenTxtStream(CancellationToken cancellationToken = default) => new TxtStream(ExportPaths(), cancellationToken);

    public async Task WriteTxtAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true);
        foreach (var path in ExportPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync((string.Join('\t', path) + "\r\n").AsMemory(), cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
    }

    private static bool Valid(AggregationTreeNode node) => node.Loaded && node.Error is null;

    // Generate one row at a time. Large/deep exports never accumulate the entire file in server memory.
    private sealed class TxtStream(IEnumerable<IReadOnlyList<string>> paths, CancellationToken token) : Stream
    {
        private readonly IEnumerator<IReadOnlyList<string>> _paths = paths.GetEnumerator();
        private byte[] _row = [];
        private int _offset;
        private bool _finished;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.Length == 0 || _finished) return 0;
            if (_offset == _row.Length)
            {
                if (!_paths.MoveNext()) { _finished = true; _row = []; return 0; }
                _row = Encoding.UTF8.GetBytes(string.Join('\t', _paths.Current) + "\r\n");
                _offset = 0;
            }
            var length = Math.Min(buffer.Length, _row.Length - _offset);
            _row.AsSpan(_offset, length).CopyTo(buffer);
            _offset += length;
            return length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer.AsSpan(offset, count)));
        }
        protected override void Dispose(bool disposing) { if (disposing) _paths.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
