using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

public sealed class TagGraphTests
{
    private static TagGraph Sample() => TagGraph.Build(
    [
        ["client A", "job 7"], ["client A", "job 7", "sunset"], ["Client A", "client A"], ["sunset", "beach"],
        ["beach"], ["solo"], ["job 7", "client A"]
    ]);

    [Fact]
    public void TagsAreCountedOncePerFileAndLinkedByTheFilesTheyShare()
    {
        var graph = Sample();
        Assert.Equal(["client A", "job 7", "beach", "sunset", "solo"], graph.Nodes.Select(node => node.Tag));
        Assert.Equal(4, graph.Find("CLIENT A")!.Files);
        var link = graph.Edges.Single(edge => edge.From == "client A" && edge.To == "job 7");
        Assert.Equal(3, link.Shared);
        // 3 files carry both, 4 carry client A and 3 carry job 7: 3 / (4 + 3 - 3).
        Assert.Equal(0.75, link.Strength, 6);
        Assert.Equal(["job 7", "sunset"], graph.NeighboursOf("client A").Select(neighbour => neighbour.Tag));
        Assert.Empty(graph.NeighboursOf("solo"));
        Assert.Empty(graph.NeighboursOf("not a tag"));
    }

    [Fact]
    public void TheMapIsTheSameEachTimeFitsItsSquareAndKeepsLinkedTagsClose()
    {
        var graph = Sample();
        var first = TagGraphLayout.Overview(graph);
        var again = TagGraphLayout.Overview(graph);
        Assert.Equal(first.OrderBy(pair => pair.Key), again.OrderBy(pair => pair.Key));
        Assert.All(first.Values, point => Assert.True(Math.Abs(point.X) <= 1.0001 && Math.Abs(point.Y) <= 1.0001));
        double Distance(string a, string b) => Math.Sqrt(Math.Pow(first[a].X - first[b].X, 2) + Math.Pow(first[a].Y - first[b].Y, 2));
        Assert.True(Distance("client A", "job 7") < Distance("client A", "beach"), "Tags used together should sit closer than tags never used together.");
        Assert.Single(TagGraphLayout.Overview(TagGraph.Build([["only"]])));
        Assert.Empty(TagGraphLayout.Overview(TagGraph.Empty));
    }
}
