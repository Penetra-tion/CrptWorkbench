using CrptWorkbench.Application;
using CrptWorkbench.Web.Components;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class IncrementalAggregationTests
{
    [Fact]
    public void EachParentRevealsIndependentPortionsAndExportRetainsEveryLeaf()
    {
        var tree = Tree("root");
        var builder = new IncrementalAggregationRows(tree);
        var expanded = new HashSet<string> { "root", "child-000" };
        var limits = new Dictionary<string, int>();
        var first = builder.Build(expanded, limits);
        Assert.Equal(100, first.Count(x => x.MoreFor is null && x.Row.Depth == 1));
        Assert.Equal(100, first.Count(x => x.MoreFor is null && x.Row.Depth == 2));
        Assert.Equal(2, first.Count(x => x.MoreFor is not null));
        limits["root"] = 200;
        var second = builder.Build(expanded, limits);
        Assert.Equal(200, second.Count(x => x.MoreFor is null && x.Row.Depth == 1));
        Assert.Equal(100, second.Count(x => x.MoreFor is null && x.Row.Depth == 2));
        limits["root"] = 300;
        limits["child-000"] = 300;
        var final = builder.Build(expanded, limits);
        Assert.All(final.Where(x => x.MoreFor is not null), x => Assert.Equal(x.TotalChildren, x.VisibleChildren));
        var nodes = final.Where(x => x.MoreFor is null).ToArray();
        Assert.Equal(tree.Nodes.Count, nodes.Length);
        Assert.Equal(nodes.Length, nodes.Select(x => x.Row.Node.Code).Distinct().Count());
        Assert.Equal(499, tree.ExportPaths().Count());
        expanded.Remove("child-000");
        Assert.DoesNotContain(builder.Build(expanded, limits), x => x.MoreFor is null && x.Row.Depth == 2);
        expanded.Add("child-000");
        Assert.Equal(250, builder.Build(expanded, limits).Count(x => x.MoreFor is null && x.Row.Depth == 2));
    }

    [Fact]
    public void RequestedDescendantRemainsVisibleEvenWhenOriginallyAfterFirstHundredSiblings()
    {
        var rows = new IncrementalAggregationRows(Tree("leaf-249"))
            .Build(new HashSet<string> { "root", "child-000" }, new Dictionary<string, int>());
        Assert.Contains(rows, x => x.MoreFor is null && x.Row.Node.Code == "leaf-249");
        Assert.Equal(100, rows.Count(x => x.MoreFor is null && x.Row.Depth == 2));
    }

    private static AggregationTreeResult Tree(string requested)
    {
        var nodes = new List<AggregationTreeNode> { new("root", "BOX", "INTRODUCED", true, null, 250, 0) };
        var links = new List<AggregationTreeLink>();
        for (var i = 0; i < 250; i++)
        {
            var child = $"child-{i:D3}";
            nodes.Add(new(child, i == 0 ? "GROUP" : "UNIT", "INTRODUCED", true, null, i == 0 ? 250 : 0, 0));
            links.Add(new("root", child, true));
            var leaf = $"leaf-{i:D3}";
            nodes.Add(new(leaf, "UNIT", "INTRODUCED", true, null, 0, 0));
            links.Add(new("child-000", leaf, true));
        }
        return new(requested, true, "root", nodes, links, true, []);
    }
}
