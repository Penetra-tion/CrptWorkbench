using CrptWorkbench.Application;

namespace CrptWorkbench.Web.Components;

public sealed record IncrementalAggregationRow(AggregationTreeRow Row, string? MoreFor = null, int VisibleChildren = 0, int TotalChildren = 0);

// Each parent owns its allowance. Iterative traversal also supports deeply nested trees.
public sealed class IncrementalAggregationRows(AggregationTreeResult result)
{
    private readonly Dictionary<string, AggregationTreeNode> _nodes = result.Nodes.ToDictionary(x => x.Code, StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _children = OrderedChildren(result);

    private static Dictionary<string, string[]> OrderedChildren(AggregationTreeResult tree)
    {
        var path = new Dictionary<string, string>(StringComparer.Ordinal);
        var parents = tree.Links.GroupBy(x => x.Child, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var child = tree.RequestedCode;
        while (seen.Add(child) && parents.TryGetValue(child, out var links) && links.Length == 1)
        {
            path[links[0].Parent] = child;
            child = links[0].Parent;
        }
        return tree.Links.GroupBy(x => x.Parent, StringComparer.Ordinal).ToDictionary(x => x.Key,
            x => x.Select(link => link.Child).Distinct(StringComparer.Ordinal)
                .OrderBy(code => code == path.GetValueOrDefault(x.Key) ? 0 : 1)
                .ThenBy(code => code, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    public List<IncrementalAggregationRow> Build(IReadOnlySet<string> expanded, IReadOnlyDictionary<string, int> limits)
    {
        var rows = new List<IncrementalAggregationRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(string Code, int Depth, bool More)>();
        stack.Push((result.RootCode, 0, false));
        while (stack.TryPop(out var entry))
        {
            if (!_nodes.TryGetValue(entry.Code, out var node)) continue;
            var children = (_children.GetValueOrDefault(entry.Code) ?? []).Where(_nodes.ContainsKey).ToArray();
            var visible = Math.Min(children.Length, limits.GetValueOrDefault(entry.Code, 100));
            if (entry.More)
            {
                rows.Add(new(new(node, entry.Depth + 1, false), entry.Code, visible, children.Length));
                continue;
            }
            if (!seen.Add(entry.Code)) continue;
            rows.Add(new(new(node, entry.Depth, children.Length > 0)));
            if (!expanded.Contains(entry.Code)) continue;
            if (children.Length > 0) stack.Push((entry.Code, entry.Depth, true));
            for (var i = visible - 1; i >= 0; i--) stack.Push((children[i], entry.Depth + 1, false));
        }
        return rows;
    }
}
