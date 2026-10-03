namespace MediaWorkbench.Core;

/// <summary>A tag in the tag graph and how many files carry it.</summary>
public sealed record TagNode(string Tag, int Files);

/// <summary>Two tags used on the same files, and on how many.</summary>
public sealed record TagEdge(string From, string To, int Shared)
{
    /// <summary>How closely the two go together, 0 to 1: the files carrying both, out of the files carrying either (Jaccard).</summary>
    public double Strength { get; init; }
}

/// <summary>A tag next to another in the graph: the files they share, and how strongly they go together.</summary>
public sealed record TagNeighbour(string Tag, int Shared, int Files, double Strength);

/// <summary>
/// The tags as a graph: a node for each tag, sized by the files that carry it, and a link between two tags whenever some file
/// carries both, as strong as the share of their files they have in common.
/// </summary>
public sealed class TagGraph
{
    private readonly Dictionary<string, TagNode> nodes;
    private readonly Dictionary<string, List<TagNeighbour>> neighbours;

    private TagGraph(List<TagNode> nodeList, List<TagEdge> edgeList)
    {
        Nodes = nodeList;
        Edges = edgeList;
        nodes = nodeList.ToDictionary(node => node.Tag, StringComparer.OrdinalIgnoreCase);
        neighbours = nodeList.ToDictionary(node => node.Tag, _ => new List<TagNeighbour>(), StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edgeList)
        {
            neighbours[edge.From].Add(new TagNeighbour(edge.To, edge.Shared, nodes[edge.To].Files, edge.Strength));
            neighbours[edge.To].Add(new TagNeighbour(edge.From, edge.Shared, nodes[edge.From].Files, edge.Strength));
        }
        foreach (var list in neighbours.Values)
            list.Sort((left, right) => right.Shared != left.Shared ? right.Shared.CompareTo(left.Shared) : string.Compare(left.Tag, right.Tag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tags, most used first.</summary>
    public IReadOnlyList<TagNode> Nodes { get; }
    public IReadOnlyList<TagEdge> Edges { get; }
    public static TagGraph Empty { get; } = new([], []);

    public TagNode? Find(string tag) => nodes.GetValueOrDefault(tag);

    /// <summary>The tags used together with <paramref name="tag"/>, most shared files first.</summary>
    public IReadOnlyList<TagNeighbour> NeighboursOf(string tag) => neighbours.TryGetValue(tag, out var list) ? list : [];

    /// <summary>Builds the graph from the tags of each file (a tag written twice on one file, or in another case, counts once).</summary>
    public static TagGraph Build(IEnumerable<IEnumerable<string>> files)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var spelled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new Dictionary<(string, string), int>();
        foreach (var file in files)
        {
            var tags = file.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var tag in tags)
            {
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
                spelled.TryAdd(tag, tag);
            }
            for (var first = 0; first < tags.Count; first++)
                for (var second = first + 1; second < tags.Count; second++)
                {
                    var key = (spelled[tags[first]], spelled[tags[second]]);
                    pairs[key] = pairs.GetValueOrDefault(key) + 1;
                }
        }
        var nodeList = counts.Select(pair => new TagNode(spelled[pair.Key], pair.Value))
            .OrderByDescending(node => node.Files).ThenBy(node => node.Tag, StringComparer.OrdinalIgnoreCase).ToList();
        var edgeList = pairs.Select(pair => new TagEdge(pair.Key.Item1, pair.Key.Item2, pair.Value)
        {
            Strength = pair.Value / (double)(counts[pair.Key.Item1] + counts[pair.Key.Item2] - pair.Value)
        }).OrderByDescending(edge => edge.Shared).ToList();
        return new TagGraph(nodeList, edgeList);
    }
}

/// <summary>
/// Places the tags for the overview: tags used together pull towards each other, every pair pushes apart a little, and a gentle
/// pull to the middle keeps loose tags in view. Deterministic (the start comes from the tag names), so the map looks the same each
/// time it is opened. Positions are in a square from -1 to 1.
/// </summary>
public static class TagGraphLayout
{
    public static Dictionary<string, (double X, double Y)> Overview(TagGraph graph, IReadOnlyCollection<string>? only = null, int iterations = 260)
    {
        var tags = (only ?? graph.Nodes.Select(node => node.Tag).ToList()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var index = tags.Select((tag, position) => (tag, position)).ToDictionary(pair => pair.tag, pair => pair.position, StringComparer.OrdinalIgnoreCase);
        var count = tags.Count;
        var x = new double[count];
        var y = new double[count];
        for (var node = 0; node < count; node++)
        {
            // A start spread on a spiral, turned by the tag's own name, so the layout does not depend on run or machine.
            var seed = Seed(tags[node]);
            var angle = node * 2.39996 + seed % 628 / 100.0;
            var radius = 0.15 + 0.8 * Math.Sqrt((node + 0.5) / Math.Max(1, count));
            x[node] = Math.Cos(angle) * radius;
            y[node] = Math.Sin(angle) * radius;
        }
        if (count <= 1) return Result();
        var links = graph.Edges.Where(edge => index.ContainsKey(edge.From) && index.ContainsKey(edge.To))
            .Select(edge => (A: index[edge.From], B: index[edge.To], edge.Strength)).ToList();
        var ideal = 1.6 / Math.Sqrt(count);
        var dx = new double[count];
        var dy = new double[count];
        for (var step = 0; step < iterations; step++)
        {
            var temperature = 0.08 * (1 - step / (double)iterations) + 0.004;
            Array.Clear(dx);
            Array.Clear(dy);
            for (var a = 0; a < count; a++)
                for (var b = a + 1; b < count; b++)
                {
                    var ox = x[a] - x[b];
                    var oy = y[a] - y[b];
                    var distance = Math.Max(0.01, Math.Sqrt(ox * ox + oy * oy));
                    var push = ideal * ideal / distance;
                    dx[a] += ox / distance * push; dy[a] += oy / distance * push;
                    dx[b] -= ox / distance * push; dy[b] -= oy / distance * push;
                }
            foreach (var (a, b, strength) in links)
            {
                var ox = x[a] - x[b];
                var oy = y[a] - y[b];
                var distance = Math.Max(0.01, Math.Sqrt(ox * ox + oy * oy));
                var pull = distance * distance / ideal * (0.4 + 1.6 * strength);
                dx[a] -= ox / distance * pull; dy[a] -= oy / distance * pull;
                dx[b] += ox / distance * pull; dy[b] += oy / distance * pull;
            }
            for (var node = 0; node < count; node++)
            {
                dx[node] -= x[node] * 0.6;
                dy[node] -= y[node] * 0.6;
                var length = Math.Sqrt(dx[node] * dx[node] + dy[node] * dy[node]);
                if (length > 0)
                {
                    var move = Math.Min(length, temperature);
                    x[node] += dx[node] / length * move;
                    y[node] += dy[node] / length * move;
                }
            }
        }
        // Fit into the square, keeping the shape.
        var reach = Math.Max(1e-6, Enumerable.Range(0, count).Max(node => Math.Max(Math.Abs(x[node]), Math.Abs(y[node]))));
        for (var node = 0; node < count; node++)
        {
            x[node] /= reach;
            y[node] /= reach;
        }
        return Result();

        Dictionary<string, (double X, double Y)> Result() =>
            tags.Select((tag, node) => (tag, node)).ToDictionary(pair => pair.tag, pair => (x[pair.node], y[pair.node]), StringComparer.OrdinalIgnoreCase);
    }

    private static uint Seed(string tag)
    {
        var hash = 2166136261u;
        foreach (var character in tag.ToLowerInvariant())
            hash = (hash ^ character) * 16777619u;
        return hash;
    }
}
