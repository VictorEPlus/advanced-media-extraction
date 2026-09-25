using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

/// <summary>A tag carried by files in the folder the overview shows, with how many of them carry it.</summary>
public sealed record OverviewTag(string Tag, int Files, bool IsActive);

/// <summary>
/// The overview of the folder shown in the filmstrip: its totals, the tags its files carry (click one to filter the filmstrip
/// to it), and a card for each folder inside it to click through, with Up to go back out.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Cards shown at once; a folder with more subfolders than this says how many more there are.</summary>
    public const int OverviewFolderLimit = 300;
    private const int OverviewTagLimit = 80;

    public ObservableCollection<FolderRowViewModel> OverviewFolders { get; } = [];
    public ObservableCollection<OverviewTag> OverviewTags { get; } = [];
    public bool HasOverviewFolders => OverviewFolders.Count > 0;
    public bool HasNoOverviewTags => OverviewTags.Count == 0;
    public bool CanGoUp => folderFilter.Length > 0;
    public string OverviewTitle => folderFilter.Length == 0 ? "ALL FOLDERS" : DisplayKey(folderFilter).ToUpperInvariant();
    public string OverviewFoldersTitle { get; private set; } = "";
    public string OverviewMoreText { get; private set; } = "";
    /// <summary>Set when the tag filter came from a tag chip, so it matches that tag exactly rather than every tag containing the text.</summary>
    private bool exactTagFilter;
    private bool settingTagFilter;

    private void UpdateOverview()
    {
        var node = folderRoot is null ? null : FolderTree.Find(folderRoot, folderFilter) ?? folderRoot;
        var children = node is null ? [] : node.Children.Order(Comparer<FolderNode>.Create((left, right) => NaturalOrder.Compare(NodeTitle(left), NodeTitle(right)))).ToList();
        var previous = OverviewFolders.ToDictionary(card => card.Node.Path, StringComparer.OrdinalIgnoreCase);
        var atTop = node is null || node.Path.Length == 0;
        var cards = children.Take(OverviewFolderLimit).Select(child =>
        {
            var card = new FolderRowViewModel(child, atTop ? 1 : 2, false, node!.Total)
            {
                IsWorkspaceFolder = atTop, Folder = atTop ? FolderOf(child.Path) : null, HasFolderTags = FolderHasTags(child)
            };
            return previous.TryGetValue(child.Path, out var kept) && kept.SameAs(card) ? kept.WithNode(child) : card;
        }).ToList();
        if (!cards.SequenceEqual(OverviewFolders, ReferenceEqualityComparer.Instance))
        {
            OverviewFolders.Clear();
            foreach (var card in cards) OverviewFolders.Add(card);
        }
        OverviewFoldersTitle = children.Count == 0 ? "" : atTop ? $"WORKSPACE FOLDERS ({children.Count:N0})" : $"FOLDERS INSIDE ({children.Count:N0})";
        OverviewMoreText = children.Count > OverviewFolderLimit ? $"{children.Count - OverviewFolderLimit:N0} more folders are in the tree on the left." : "";
        UpdateOverviewTags();
        foreach (var property in new[] { nameof(HasOverviewFolders), nameof(CanGoUp), nameof(OverviewTitle), nameof(OverviewFoldersTitle), nameof(OverviewMoreText) })
            OnPropertyChanged(property);
    }

    /// <summary>Counts the tags of every file in the folder, whatever the other filters, so any of them can be picked next.</summary>
    private void UpdateOverviewTags()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Assets)
        {
            if (item.Tags.Length == 0 || !IsFolderIncluded(item) || !FolderTree.Contains(folderFilter, item.FolderKey))
                continue;
            foreach (var tag in item.Tags)
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
        }
        var active = exactTagFilter ? TagFilter.Trim() : null;
        var tags = counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(OverviewTagLimit)
            .Select(pair => new OverviewTag(pair.Key, pair.Value, string.Equals(pair.Key, active, StringComparison.OrdinalIgnoreCase))).ToList();
        if (!tags.SequenceEqual(OverviewTags))
        {
            OverviewTags.Clear();
            foreach (var tag in tags) OverviewTags.Add(tag);
        }
        OnPropertyChanged(nameof(HasNoOverviewTags));
    }

    [RelayCommand]
    private void OpenOverviewFolder(FolderRowViewModel? card)
    {
        if (card is not null) SelectFolder(card.Node.Path);
    }

    /// <summary>Up goes to the folder around this one as the tree shows it, so a row that merges a chain of single folders is left in one step.</summary>
    [RelayCommand]
    private void OverviewUp()
    {
        if (folderRoot is null || folderFilter.Length == 0) return;
        FolderNode? Parent(FolderNode node)
        {
            foreach (var child in node.Children)
            {
                if (child.Path.Equals(folderFilter, StringComparison.OrdinalIgnoreCase)) return node;
                if (FolderTree.Contains(child.Path, folderFilter) && Parent(child) is { } found) return found;
            }
            return null;
        }
        SelectFolder(Parent(folderRoot)?.Path ?? "");
    }

    /// <summary>A tag chip: the filmstrip shows only files with exactly that tag; clicking it again shows everything again.</summary>
    [RelayCommand]
    private void ToggleOverviewTag(OverviewTag? tag)
    {
        if (tag is null) return;
        settingTagFilter = true;
        try
        {
            exactTagFilter = !tag.IsActive;
            TagFilter = tag.IsActive ? "" : tag.Tag;
        }
        finally { settingTagFilter = false; }
        UpdateOverviewTags();
        Status = tag.IsActive ? "Showing files with any tag again." : $"Showing the {visibleCount:N0} files tagged {tag.Tag}. Click the tag again to show everything.";
    }

    private bool MatchesTagFilter(AssetViewModel item)
    {
        var query = TagFilter.Trim();
        if (query.Length == 0) return true;
        return exactTagFilter
            ? item.Tags.Any(tag => string.Equals(tag, query, StringComparison.OrdinalIgnoreCase))
            : item.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
