using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia;

/// <summary>A tag next to the one in focus in the graph, for the list beside it.</summary>
public sealed record GraphNeighbour(string Tag, int Shared, int Files, double Strength)
{
    public string SharedText => Shared == 1 ? "1 file together" : $"{Shared:N0} files together";
    /// <summary>The bar under it: how strongly the two go together, as a width out of 100.</summary>
    public double StrengthWidth => Math.Max(4, Strength * 100);
}

// The Graph tab: every tag as a node, linked to the tags used on the same files. Clicking a tag puts it in focus, with the tags it
// goes with around it; going from tag to tag leaves a trail to come back along. Its files can be shown in the filmstrip, or its
// tag added to the tag filter.
public sealed partial class MainViewModel
{
    [ObservableProperty] private TagGraph tagGraph = TagGraph.Empty;
    [ObservableProperty] private string? graphFocus;
    [ObservableProperty] private string graphSearch = "";
    public ObservableCollection<GraphNeighbour> GraphNeighbours { get; } = [];
    /// <summary>The tags visited before the one in focus, oldest first.</summary>
    public ObservableCollection<string> GraphTrail { get; } = [];
    private bool walkingBack;

    public bool IsGraphTab { get => MainTab == 3; set { if (value) MainTab = 3; } }
    public bool HasGraphFocus => GraphFocus is not null;
    public bool HasGraphTrail => GraphTrail.Count > 0;
    public bool HasNoGraphNeighbours => GraphFocus is not null && GraphNeighbours.Count == 0;
    public int GraphFocusFiles => GraphFocus is { } tag ? TagGraph.Find(tag)?.Files ?? 0 : 0;
    public string GraphFocusSummary => GraphFocus is not { } tag || TagGraph.Find(tag) is not { } node ? ""
        : $"{node.Files:N0} {(node.Files == 1 ? "file" : "files")} · {GraphNeighbours.Count:N0} related {(GraphNeighbours.Count == 1 ? "tag" : "tags")}";
    public string GraphSummary => TagGraph.Nodes.Count == 0 ? "No tags yet."
        : $"{TagGraph.Nodes.Count:N0} tags, {TagGraph.Edges.Count:N0} links between them.";
    public IEnumerable<TagNode> GraphTopTags => TagGraph.Nodes.Take(8);

    /// <summary>The tags of every tagged file: those in the workspace with their folders' tags too, and tagged files anywhere else.</summary>
    private void BuildTagGraph()
    {
        var files = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, tags) in tagIndex) files[path] = tags;
        foreach (var item in Assets)
            if (item.Tags.Length > 0) files[item.Asset.FullPath] = item.Tags;
        TagGraph = TagGraph.Build(files.Values);
        if (GraphFocus is { } focus && TagGraph.Find(focus) is null) GraphFocus = null;
        else ShowGraphFocus();
        foreach (var property in new[] { nameof(GraphSummary), nameof(GraphTopTags) })
            OnPropertyChanged(property);
    }

    partial void OnGraphFocusChanged(string? oldValue, string? newValue)
    {
        // Going from tag to tag leaves a trail; going back along it, or to the overview, does not.
        if (!walkingBack && oldValue is not null && newValue is not null && !string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
            GraphTrail.Add(oldValue);
        if (newValue is null) GraphTrail.Clear();
        ShowGraphFocus();
    }

    private void ShowGraphFocus()
    {
        GraphNeighbours.Clear();
        if (GraphFocus is { } tag)
            foreach (var neighbour in TagGraph.NeighboursOf(tag))
                GraphNeighbours.Add(new GraphNeighbour(neighbour.Tag, neighbour.Shared, neighbour.Files, neighbour.Strength));
        foreach (var property in new[] { nameof(HasGraphFocus), nameof(HasGraphTrail), nameof(HasNoGraphNeighbours), nameof(GraphFocusFiles), nameof(GraphFocusSummary) })
            OnPropertyChanged(property);
        if (GraphFocus is { } focus) Status = $"{focus}: {GraphFocusSummary}. Click a related tag to go on; double-click shows its files.";
    }

    /// <summary>A tag from the list beside the graph, the trail or the find box: put it in focus.</summary>
    [RelayCommand]
    private void FocusGraphTag(string? tag)
    {
        if (tag is null || TagGraph.Find(tag) is not { } node) return;
        GraphFocus = node.Tag;
    }

    /// <summary>Back along the trail to the tag visited before this one.</summary>
    [RelayCommand]
    private void GraphBack()
    {
        if (GraphTrail.Count == 0)
        {
            GraphFocus = null;
            return;
        }
        var previous = GraphTrail[^1];
        GraphTrail.RemoveAt(GraphTrail.Count - 1);
        walkingBack = true;
        try { GraphFocus = previous; }
        finally { walkingBack = false; }
    }

    /// <summary>Back to the whole map.</summary>
    [RelayCommand]
    private void GraphOverview() => GraphFocus = null;

    /// <summary>The find box: Enter puts the first tag that matches in focus.</summary>
    [RelayCommand]
    private void FindGraphTag()
    {
        var text = GraphSearch.Trim();
        if (text.Length == 0) return;
        var match = TagGraph.Nodes.FirstOrDefault(node => string.Equals(node.Tag, text, StringComparison.OrdinalIgnoreCase))
            ?? TagGraph.Nodes.FirstOrDefault(node => node.Tag.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Status = $"No tag matches {text}.";
            return;
        }
        GraphFocus = match.Tag;
        GraphSearch = "";
    }

    /// <summary>"Show its files": the filmstrip narrowed to just this tag (other ticked tags are let go).</summary>
    [RelayCommand]
    private void ShowGraphFiles(string? tag)
    {
        tag ??= GraphFocus;
        if (tag is null) return;
        foreach (var other in FilterTags.Where(each => !string.Equals(each, tag, StringComparison.OrdinalIgnoreCase)).ToList())
            SetFilterTag(other, false);
        SetFilterTag(tag, true);
        Status = $"The filmstrip shows the {visibleCount:N0} files tagged {tag}. Clear the tag filter (bottom left) to see everything again.";
    }

    /// <summary>"Add to filter": this tag joins the ones already ticked in the tag filter.</summary>
    [RelayCommand]
    private void AddGraphTagToFilter()
    {
        if (GraphFocus is { } tag) SetFilterTag(tag, true);
    }
}
