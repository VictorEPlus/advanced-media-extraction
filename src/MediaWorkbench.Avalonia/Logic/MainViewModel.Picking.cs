using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MediaWorkbench.Avalonia;

/// <summary>A tag that some, not all, of the picked files carry.</summary>
public sealed record TagShare(string Tag, int Count, int Total)
{
    public string CountText => $"{Count} of {Total}";
    public string ToolTipText => $"On {Count} of the {Total} picked files. Click + to add it to the rest, × to take it off all of them.";
}

/// <summary>
/// One of the tags already in use, how many of the files being tagged carry it, and (in the bar over the filmstrip) how many of
/// the files shown carry it.
/// </summary>
public sealed record TagChoice(string Tag, int Count, int Total, int Uses = 0)
{
    public bool IsOnAll => Total > 0 && Count == Total;
    public bool IsOnSome => Count > 0 && Count < Total;
    public string UsesText => Uses.ToString("N0");
    public string ToolTipText => (Uses > 0 ? $"{Uses:N0} of the files shown carry it. " : "")
        + (Total == 0 ? "Open a file, or pick some, to tag them with it."
        : IsOnAll ? (Total == 1 ? "On this file. Click to take it off." : $"On all {Total} picked files. Click to take it off all of them.")
        : IsOnSome ? $"On {Count} of the {Total} picked files. Click to add it to the rest."
        : Total == 1 ? "Click to add it to this file." : $"Click to add it to all {Total} picked files.");
}

// Picking several files in the filmstrip (Ctrl+click, Shift+click, or the round mark on a thumbnail), and the Tags tab working on
// whichever is being tagged: the picked files when there are any, otherwise the open file.
public sealed partial class MainViewModel
{
    private readonly List<AssetViewModel> picked = [];
    private AssetViewModel? pickAnchor;

    /// <summary>Tags some but not all of the picked files carry.</summary>
    public ObservableCollection<TagShare> PartialTags { get; } = [];
    /// <summary>Every tag in use, for choosing with a click instead of typing.</summary>
    public ObservableCollection<TagChoice> TagChoices { get; } = [];
    /// <summary>The tags of the files the filmstrip shows, most used first, in the bar right above it: tagging without crossing the window.</summary>
    public ObservableCollection<TagChoice> FolderBarTags { get; } = [];
    public bool HasFolderBarTags => FolderBarTags.Count > 0;
    [ObservableProperty] private string quickTagText = "";
    [ObservableProperty] private bool showTagChoices;
    [ObservableProperty] private string tagChoiceFilter = "";

    public IReadOnlyList<AssetViewModel> PickedAssets => picked;
    public int PickedCount => picked.Count;
    public bool HasPicks => picked.Count > 0;
    public string PickedLabel => picked.Count == 1 ? "1 file picked" : $"{picked.Count:N0} files picked";
    public string TagTargetTitle => !HasPicks ? "THIS FILE" : picked.Count == 1 ? "1 PICKED FILE" : $"{picked.Count:N0} PICKED FILES";
    public string TagBoxHint => HasPicks ? $"Tags for the {picked.Count:N0} picked files" : "Tags, separated by commas";
    public bool HasPartialTags => PartialTags.Count > 0;
    public bool CanTagTargets => HasPicks || SelectedAsset is not null;
    /// <summary>Collections, folder tags, suggestions and details belong to the open file, so they step aside while files are picked.</summary>
    public bool ShowFileTagSections => !HasPicks;
    public string TagChoicesLabel => ShowTagChoices ? "Hide your tags" : $"Choose from your tags ({KnownTags.Count:N0})";
    public bool HasNoTagChoices => TagChoices.Count == 0;

    /// <summary>What the Tags tab tags: the picked files, or the open file when nothing is picked.</summary>
    private IReadOnlyList<AssetViewModel> TagTargets => HasPicks ? picked : SelectedAsset is { } item ? [item] : [];

    /// <summary>Ctrl+click, or the round mark on a thumbnail: picks a file, or unpicks it. The open file does not change.</summary>
    [RelayCommand]
    public void TogglePick(AssetViewModel? item)
    {
        if (item is null) return;
        if (picked.Remove(item))
            item.IsPicked = false;
        else
        {
            picked.Add(item);
            item.IsPicked = true;
        }
        pickAnchor = item;
        PicksChanged();
    }

    /// <summary>Shift+click: picks every file shown between the last one picked and this one, both included.</summary>
    [RelayCommand]
    public void PickRange(AssetViewModel? item)
    {
        if (item is null) return;
        var to = IndexInView(item);
        var from = pickAnchor is { } anchor ? IndexInView(anchor) : -1;
        if (to < 0) return;
        if (from < 0) from = SelectedAsset is { } open && IndexInView(open) is >= 0 and var openIndex ? openIndex : to;
        for (var index = Math.Min(from, to); index <= Math.Max(from, to); index++)
            if (LibraryView[index] is var each && !each.IsPicked)
            {
                picked.Add(each);
                each.IsPicked = true;
            }
        pickAnchor = item;
        PicksChanged();
    }

    /// <summary>Picks every file the filmstrip shows (after the search and filters).</summary>
    [RelayCommand]
    private void PickAllShown()
    {
        foreach (var item in LibraryView)
            if (!item.IsPicked)
            {
                picked.Add(item);
                item.IsPicked = true;
            }
        PicksChanged();
    }

    /// <summary>"Tag them": the Tags tab, on the picked files.</summary>
    [RelayCommand]
    private void ShowTagsForPicks()
    {
        ShowInspector = true;
        InspectorTab = 1;
    }

    [RelayCommand]
    private void ClearPicks()
    {
        if (picked.Count == 0) return;
        foreach (var item in picked) item.IsPicked = false;
        picked.Clear();
        pickAnchor = null;
        PicksChanged();
    }

    /// <summary>Picks of files the filmstrip no longer shows (another folder, a filter, a file gone) are let go, so nothing unseen gets tagged.</summary>
    private void PrunePicks()
    {
        if (picked.Count == 0) return;
        var shown = new HashSet<AssetViewModel>(LibraryView);
        var gone = picked.Where(item => !shown.Contains(item)).ToList();
        if (gone.Count == 0) return;
        foreach (var item in gone)
        {
            item.IsPicked = false;
            picked.Remove(item);
        }
        if (pickAnchor is not null && !pickAnchor.IsPicked) pickAnchor = null;
        PicksChanged();
    }

    private int IndexInView(AssetViewModel item)
    {
        for (var index = 0; index < LibraryView.Count; index++)
            if (ReferenceEquals(LibraryView[index], item)) return index;
        return -1;
    }

    private void PicksChanged()
    {
        foreach (var property in new[] { nameof(PickedCount), nameof(HasPicks), nameof(PickedLabel), nameof(TagTargetTitle), nameof(TagBoxHint), nameof(CanTagTargets), nameof(ShowFileTagSections), nameof(PickedAssets) })
            OnPropertyChanged(property);
        ShowTagsOf(SelectedAsset);
    }

    partial void OnShowTagChoicesChanged(bool value)
    {
        OnPropertyChanged(nameof(TagChoicesLabel));
        UpdateTagChoices();
    }

    partial void OnTagChoiceFilterChanged(string value) => UpdateTagChoices();

    /// <summary>The tags shown on the Tags tab for what is being tagged: on all of it, and (for picked files) on some of it.</summary>
    private void ShowTargetTags()
    {
        SelectedTags.Clear();
        PartialTags.Clear();
        var targets = TagTargets;
        foreach (var (tag, count) in CountTags(targets).OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select(entry => (entry.Key, entry.Value)))
            if (count == targets.Count) SelectedTags.Add(tag);
            else PartialTags.Add(new TagShare(tag, count, targets.Count));
        OnPropertyChanged(nameof(HasPartialTags));
        OnPropertyChanged(nameof(CanTagTargets));
        UpdateTagChoices();
        UpdateFolderBarTags();
    }

    /// <summary>Recounts the bar over the filmstrip: every tag the shown files carry (their own and their folders'), most used first.</summary>
    private void UpdateFolderBarTags()
    {
        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in LibraryView)
            foreach (var tag in item.Tags)
                uses[tag] = uses.GetValueOrDefault(tag) + 1;
        var targets = TagTargets;
        var counts = CountTags(targets);
        var choices = uses.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new TagChoice(entry.Key, counts.GetValueOrDefault(entry.Key), targets.Count, entry.Value)).ToList();
        if (choices.SequenceEqual(FolderBarTags)) return;
        FolderBarTags.Clear();
        foreach (var choice in choices) FolderBarTags.Add(choice);
        OnPropertyChanged(nameof(HasFolderBarTags));
    }

    /// <summary>The box in the bar over the filmstrip: Enter tags the open file (or the picked files) with what was typed.</summary>
    [RelayCommand]
    private void AddQuickTags() => Guard(() =>
    {
        var tags = SplitTags(QuickTagText);
        if (tags.Length == 0 || !CanTagTargets) return;
        AddTagsToTargets(tags, "manual");
        QuickTagText = "";
    });

    /// <summary>How many of <paramref name="targets"/> carry each of their own tags (folder tags are not counted: they are changed on the folder).</summary>
    private Dictionary<string, int> CountTags(IReadOnlyList<AssetViewModel> targets)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in targets)
            foreach (var tag in tagIndex.GetValueOrDefault(item.Asset.FullPath, []).Distinct(StringComparer.OrdinalIgnoreCase))
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
        return counts;
    }

    private void UpdateTagChoices()
    {
        OnPropertyChanged(nameof(TagChoicesLabel));
        if (!ShowTagChoices)
        {
            if (TagChoices.Count > 0) TagChoices.Clear();
            OnPropertyChanged(nameof(HasNoTagChoices));
            return;
        }
        var targets = TagTargets;
        var counts = CountTags(targets);
        var filter = TagChoiceFilter.Trim();
        TagChoices.Clear();
        foreach (var tag in KnownTags.Where(tag => filter.Length == 0 || tag.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            TagChoices.Add(new TagChoice(tag, counts.GetValueOrDefault(tag), targets.Count));
        OnPropertyChanged(nameof(HasNoTagChoices));
    }

    /// <summary>A tag chosen from the list: on everything being tagged already, it comes off; otherwise it goes on everything.</summary>
    [RelayCommand]
    private void ToggleTagChoice(TagChoice? choice) => Guard(() =>
    {
        if (choice is null || TagTargets.Count == 0) return;
        if (choice.IsOnAll) RemoveTagFromTargets(choice.Tag);
        else AddTagsToTargets([choice.Tag], "manual");
    });

    /// <summary>The + on a tag some of the picked files carry: the rest get it too.</summary>
    [RelayCommand]
    private void AddSharedTagToAll(TagShare? share) => Guard(() =>
    {
        if (share is not null) AddTagsToTargets([share.Tag], "manual");
    });

    [RelayCommand]
    private void RemoveSharedTag(TagShare? share) => Guard(() =>
    {
        if (share is not null) RemoveTagFromTargets(share.Tag);
    });

    private void AddTagsToTargets(IReadOnlyList<string> tags, string source)
    {
        var targets = TagTargets.ToList();
        if (targets.Count == 0) return;
        if (targets.Count == 1)
        {
            TagFile(targets[0], tags, source);
            Status = $"Added {string.Join(", ", tags)} to {targets[0].Name}. The file itself was not changed.";
            return;
        }
        tagStore.AddToFiles(targets.Select(item => item.Asset.FullPath), tags, source);
        ReloadTags();
        RefreshView();
        _ = FingerprintTaggedFilesAsync();
        Status = $"Added {string.Join(", ", tags)} to {targets.Count:N0} files. The files themselves were not changed.";
    }

    private void RemoveTagFromTargets(string tag)
    {
        var targets = TagTargets.ToList();
        if (targets.Count == 0) return;
        tagStore.RemoveFromFiles(targets.Select(item => item.Asset.FullPath), tag);
        ReloadTags();
        RefreshView();
        Status = targets.Count == 1 ? $"Took {tag} off {targets[0].Name}." : $"Took {tag} off {targets.Count:N0} files.";
    }
}
