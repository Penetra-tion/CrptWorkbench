namespace CrptWorkbench.Domain;

public sealed record AggregationResponseNode(string Code, string? Cis, string? Parent, string? PackageType,
    string? Status, bool ChildrenKnown, long ChildCount, string? Error);

public interface IAggregationTreeResponseSink
{
    ValueTask AddChildrenAsync(string code, IReadOnlyList<string> children, CancellationToken cancellationToken);
    ValueTask AddNodeAsync(AggregationResponseNode node, CancellationToken cancellationToken);
}
