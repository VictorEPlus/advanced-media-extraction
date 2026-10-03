using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MediaWorkbench.Avalonia;

/// <summary>A tag in the filter drop-down: ticked when the filmstrip is narrowed to it, with how many files in the workspace carry it.</summary>
public sealed partial class TagFilterOption(string tag, int files, bool selected, Action<TagFilterOption> changed) : ObservableObject
{
    public string Tag { get; } = tag;
    public int Files { get; } = files;
    public string FilesText => Files.ToString("N0");
    [ObservableProperty] private bool isSelected = selected;
    partial void OnIsSelectedChanged(bool value) => changed(this);
}

// Filtering the filmstrip by tags: tick any number of tags in the drop-down (bottom left), and see files with any of them, or
// with all of them. The tag bubbles on the Overview tick and untick the same tags.
public sealed partial class MainViewModel
{
    private readonly List<TagFilterOption> tagFilterOptions = [];
    private bool rebuildingTagFilter;

    /// <summary>The ticked tags, in the order they were ticked.</summary>
    public ObservableCollection<string> FilterTags { get; } = [];
    /// <summary>The drop-down's list: every tag in use, narrowed by the find box.</summary>
    public ObservableCollection<TagFilterOption> VisibleTagFilterOptions { get; } = [];
    [ObservableProperty] private string tagFilterSearch = "";
    /// <summary>With two or more tags ticked: files that carry all of them (true) or any of them (false).</summary>
    [ObservableProperty] private bool matchAllTags;

    public bool HasFilterTags => FilterTags.Count > 0;
    public bool HasSeveralFilterTags => FilterTags.Count > 1;
    public bool HasNoTagFilterOptions => VisibleTagFilterOptions.Count == 0;
    public bool MatchAnyTag
    {
        get => !MatchAllTags;
        set => MatchAllTags = !value;
    }
    public string TagFilterLabel => FilterTags.Count switch
    {
        0 => KnownTags.Count == 0 ? "No tags yet" : "Choose tags…",
        1 => FilterTags[0],
        var count => $"{count} tags ({(MatchAllTags ? "all" : "any")})"
    };
    public string TagFilterSummary => FilterTags.Count == 0 ? "" : MatchAllTags || FilterTags.Count == 1
        ? $"Showing files tagged {string.Join(" and ", FilterTags)}."
        : $"Showing files tagged {string.Join(" or ", FilterTags)}.";

    /// <summary>Whether a file passes the tag filter: no tags ticked lets everything through.</summary>
    private bool MatchesTagFilter(AssetViewModel item)
    {
        if (FilterTags.Count == 0) return true;
        return MatchAllTags
            ? FilterTags.All(tag => item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            : FilterTags.Any(tag => item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Re-lists the drop-down after tags change, keeping what is ticked; a ticked tag no longer in use is let go.</summary>
    internal void RebuildTagFilterOptions()
    {
        var files = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Assets)
            foreach (var tag in item.Tags)
                files[tag] = files.GetValueOrDefault(tag) + 1;
        rebuildingTagFilter = true;
        try
        {
            tagFilterOptions.Clear();
            foreach (var tag in KnownTags)
                tagFilterOptions.Add(new TagFilterOption(tag, files.GetValueOrDefault(tag), FilterTags.Contains(tag, StringComparer.OrdinalIgnoreCase), OnTagFilterOptionChanged));
        }
        finally { rebuildingTagFilter = false; }
        var gone = FilterTags.Where(tag => !KnownTags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var tag in gone) FilterTags.Remove(tag);
        ShowTagFilterOptions();
        if (gone.Count > 0) TagFilterChanged();
        else NotifyTagFilter();
    }

    private void ShowTagFilterOptions()
    {
        var search = TagFilterSearch.Trim();
        VisibleTagFilterOptions.Clear();
        foreach (var option in tagFilterOptions.Where(option => search.Length == 0 || option.Tag.Contains(search, StringComparison.OrdinalIgnoreCase)))
            VisibleTagFilterOptions.Add(option);
        OnPropertyChanged(nameof(HasNoTagFilterOptions));
    }

    partial void OnTagFilterSearchChanged(string value) => ShowTagFilterOptions();

    partial void OnMatchAllTagsChanged(bool value)
    {
        OnPropertyChanged(nameof(MatchAnyTag));
        if (FilterTags.Count > 1) TagFilterChanged();
        else NotifyTagFilter();
    }

    private void OnTagFilterOptionChanged(TagFilterOption option)
    {
        if (rebuildingTagFilter) return;
        SetFilterTag(option.Tag, option.IsSelected);
    }

    /// <summary>Ticks or unticks a tag in the filter, wherever that was done (drop-down, its bubble, an Overview tag).</summary>
    private void SetFilterTag(string tag, bool on)
    {
        var existing = FilterTags.FirstOrDefault(each => string.Equals(each, tag, StringComparison.OrdinalIgnoreCase));
        if (on == (existing is not null)) return;
        if (on) FilterTags.Add(tag);
        else FilterTags.Remove(existing!);
        if (tagFilterOptions.FirstOrDefault(option => string.Equals(option.Tag, tag, StringComparison.OrdinalIgnoreCase)) is { } option && option.IsSelected != on)
        {
            rebuildingTagFilter = true;
            try { option.IsSelected = on; }
            finally { rebuildingTagFilter = false; }
        }
        TagFilterChanged();
    }

    private void TagFilterChanged()
    {
        RefreshView();
        UpdateOverviewTags();
        NotifyTagFilter();
        if (FilterTags.Count > 0) Status = $"{TagFilterSummary} {visibleCount:N0} files.";
    }

    private void NotifyTagFilter()
    {
        foreach (var property in new[] { nameof(HasFilterTags), nameof(HasSeveralFilterTags), nameof(TagFilterLabel), nameof(TagFilterSummary), nameof(HasActiveFilters) })
            OnPropertyChanged(property);
    }

    /// <summary>The × on a ticked tag's bubble.</summary>
    [RelayCommand]
    private void RemoveFilterTag(string? tag)
    {
        if (tag is not null) SetFilterTag(tag, false);
    }

    /// <summary>Clear: no tag filter, every file shows again (the other filters stay).</summary>
    [RelayCommand]
    private void ClearTagFilter()
    {
        if (FilterTags.Count == 0) return;
        rebuildingTagFilter = true;
        try
        {
            foreach (var option in tagFilterOptions) option.IsSelected = false;
        }
        finally { rebuildingTagFilter = false; }
        FilterTags.Clear();
        TagFilterChanged();
        Status = "Showing files with any tag again.";
    }
}
