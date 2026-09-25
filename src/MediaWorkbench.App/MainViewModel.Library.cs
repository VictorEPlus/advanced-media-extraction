using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

/// <summary>One visible row of the folder tree. The tree is flattened so it can use the same list styling and virtualization as the rest of the app.</summary>
public sealed partial class FolderRowViewModel(FolderNode node, int depth, bool isExpanded, int parentTotal) : ObservableObject
{
    public FolderNode Node { get; private set; } = node;
    public int Depth { get; } = depth;
    public bool IsExpanded { get; } = isExpanded;
    public bool HasChildren => Node.Children.Count > 0;
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    /// <summary>All folders is always open, so it has no arrow to click.</summary>
    public string Glyph => !HasChildren || Depth == 0 ? "" : IsExpanded ? "▾" : "▸";
    /// <summary>A workspace folder shows the name it was given, if any; everything else shows the folder's own name.</summary>
    public string Name => IsWorkspaceFolder && Folder is { } folder && Node.Name == folder.Label ? folder.DisplayName : Node.Name;
    [ObservableProperty] private bool isRenaming;
    [ObservableProperty] private string renameText = "";
    internal void RefreshName() => OnPropertyChanged(nameof(Name));
    public string CountText => Node.Total.ToString("N0");
    public string KindText => MainViewModel.DescribeKinds(Node.Photos, Node.Videos, Node.Audio, percentages: true);
    /// <summary>This folder as a share of its parent folder, for example 71%. Empty for the root.</summary>
    public string ShareText => Depth == 0 ? "" : MainViewModel.Percent(Node.Total, parentTotal);
    public string ToolTipText => $"{(Node.Path.Length == 0 ? Node.Name : Node.Path)}\n{KindText}, {MainViewModel.DescribeSize(Node.Bytes)}\n{Node.DirectTotal:N0} directly in this folder";

    /// <summary>The two pictures that stand for the folder, like the ends of an album: its first and its last visual file.</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasCover))] private ImageSource? coverFirst;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasSecondCover))] private ImageSource? coverLast;
    public bool HasCover => CoverFirst is not null;
    public bool HasSecondCover => CoverLast is not null;
    /// <summary>Set the first time the row appears, so scrolling past it again costs nothing.</summary>
    internal bool CoversRequested;
    /// <summary>A folder added to the workspace (the second level of the tree, under All folders).</summary>
    public bool IsWorkspaceFolder { get; init; }
    /// <summary>For a workspace folder row: the folder itself, whose reading state the row shows.</summary>
    public WorkspaceFolder? Folder { get; init; }
    /// <summary>The folder has tags of its own, which every file under it carries.</summary>
    public bool HasFolderTags { get; init; }
    public bool IsAllFolders => Depth == 0;

    /// <summary>Same place, name and counts: the old row can stay on screen instead of being redrawn.</summary>
    internal bool SameAs(FolderRowViewModel other) =>
        Depth == other.Depth && IsExpanded == other.IsExpanded && Node.Name == other.Node.Name && Node.Total == other.Node.Total
        && Node.Photos == other.Node.Photos && Node.Videos == other.Node.Videos && Node.Children.Count == other.Node.Children.Count
        && HasFolderTags == other.HasFolderTags;

    /// <summary>This row with the freshly built node behind it, keeping the row (and its covers) itself.</summary>
    internal FolderRowViewModel WithNode(FolderNode node)
    {
        Node = node;
        return this;
    }
}

public sealed partial class MainViewModel
{
    /// <summary>At most this many subfolders get their own bar; the rest fold into one "other folders" bar.</summary>
    public const int ChartBarLimit = 14;

    private FolderNode? folderRoot;
    private readonly HashSet<string> expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Workspace folders are open by default; these are the ones closed by hand.</summary>
    private readonly HashSet<string> collapsedTops = new(StringComparer.OrdinalIgnoreCase);
    private string folderFilter = "";
    private bool suppressFolderSelection;
    private DateTime lastFolderTreeBuild = DateTime.MinValue;

    /// <summary>Folders taken out of view with "Hide this folder". Nothing on disk changes; Show all folders brings them back.</summary>
    private readonly HashSet<string> removedFolders = new(StringComparer.OrdinalIgnoreCase);

    public bool ShowFolderEdits => removedFolders.Count > 0;
    public string FolderEditsLabel => removedFolders.Count == 1 ? "1 folder hidden" : $"{removedFolders.Count:N0} folders hidden";

    private static string LastSegment(string path) => path.Split('\\').LastOrDefault() is { Length: > 0 } name ? name : path;

    /// <summary>False for files under a hidden folder.</summary>
    private bool IsFolderIncluded(AssetViewModel item) =>
        removedFolders.Count == 0 || !removedFolders.Any(folder => FolderTree.Contains(folder, item.FolderKey));

    [RelayCommand]
    private void RemoveFolder(FolderRowViewModel? row)
    {
        if (row is null || row.Node.Path.Length == 0)
            return;
        var key = row.Node.Path;
        // A whole workspace folder is removed from the workspace rather than hidden.
        if (!key.Contains('\\') && FolderOf(key) is { } folder)
        {
            CloseFolder(folder, save: true);
            return;
        }
        removedFolders.Add(key);
        if (FolderTree.Contains(key, folderFilter))
            SelectFolder(key.Contains('\\') ? key[..key.LastIndexOf('\\')] : "");
        RefreshView();
        RebuildFolderTree();
        NotifyFolderEdits();
        Status = $"Hid {LastSegment(key)}. The files are untouched; Show all folders brings them back.";
    }

    [RelayCommand]
    private void ShowAllFolders()
    {
        if (!ShowFolderEdits)
            return;
        removedFolders.Clear();
        RefreshView();
        RebuildFolderTree();
        NotifyFolderEdits();
        Status = "Showing every folder again.";
    }

    /// <summary>A subfolder opened as a workspace folder of its own, so its tree starts at the top.</summary>
    [RelayCommand]
    private async Task AddSubfolderToWorkspaceAsync(FolderRowViewModel? row)
    {
        if (row is null || FolderOf(row.Node.Path) is not { IsVirtual: false } folder)
            return;
        var relative = row.Node.Path.Length > folder.Label.Length ? row.Node.Path[(folder.Label.Length + 1)..] : "";
        try { await AddFolderAsync(Path.Combine(folder.Path, relative)); }
        catch (Exception exception) { ReportError(exception); }
    }

    [RelayCommand]
    private void RevealFolder(FolderRowViewModel? row) => Guard(() =>
    {
        if (row is null || FolderOf(row.Node.Path) is not { IsVirtual: false } folder)
            return;
        var relative = row.Node.Path.Length > folder.Label.Length ? row.Node.Path[(folder.Label.Length + 1)..] : "";
        RevealPath(Path.Combine(folder.Path, relative));
    });

    /// <summary>
    /// Whether a folder key still has somewhere to show: a workspace folder (even one whose files are still being read), or a
    /// subfolder with files in view. Asked of the files, not the tree, because the tree merges a folder that is left with a single
    /// subfolder into one row, and that folder is still perfectly good to look at.
    /// </summary>
    private bool HasFolder(string path) =>
        path.Length == 0 || FolderOf(path) is not null && (!path.Contains('\\') || Assets.Any(item => IsFolderIncluded(item) && FolderTree.Contains(path, item.FolderKey)));

    private void NotifyFolderEdits()
    {
        OnPropertyChanged(nameof(ShowFolderEdits));
        OnPropertyChanged(nameof(FolderEditsLabel));
    }

    public ObservableCollection<FolderRowViewModel> FolderRows { get; } = [];
    public event EventHandler? TourRequested;

    /// <summary>0 = Library (folder tree and graph), 1 = Preview. Follows the selection but can be switched freely.</summary>
    [ObservableProperty] private int mainTab;
    [ObservableProperty] private FolderRowViewModel? selectedFolderRow;
    [ObservableProperty] private IReadOnlyList<FolderNode> chartNodes = [];
    [ObservableProperty] private string? chartSelectedPath;
    [ObservableProperty] private string chartTitle = "";
    [ObservableProperty] private string chartSubtitle = "";

    public bool IsLibraryTab { get => MainTab == 0; set { if (value) MainTab = 0; } }
    public bool IsPreviewTab { get => MainTab == 1; set { if (value) MainTab = 1; } }
    public bool IsStitchTab { get => MainTab == 2; set { if (value) MainTab = 2; } }
    public bool ShowLibraryEmpty => !hasSource || Assets.Count == 0;
    public bool ShowLibraryMap => !ShowLibraryEmpty;
    public bool HasFolderFilter => folderFilter.Length > 0;
    public string FolderFilterLabel => HasFolderFilter ? "Folder: " + folderFilter : "";
    /// <summary>The folder the overview describes: the one shown in the filmstrip, or everything.</summary>
    private FolderNode? ShownNode => folderRoot is null ? null : FolderTree.Find(folderRoot, folderFilter) ?? folderRoot;
    public string LibrarySummary => ShownNode is not { Total: > 0 } node ? "" :
        $"{node.Total:N0} files in {node.FolderCount + (node.DirectTotal > 0 ? 1 : 0):N0} {(node.FolderCount + (node.DirectTotal > 0 ? 1 : 0) == 1 ? "folder" : "folders")}, {DescribeSize(node.Bytes)}";
    public string LibraryKindSummary => ShownNode is not { Total: > 0 } node ? "" : DescribeKinds(node.Photos, node.Videos, node.Audio, percentages: true);

    partial void OnMainTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsLibraryTab));
        OnPropertyChanged(nameof(IsPreviewTab));
        OnPropertyChanged(nameof(IsStitchTab));
    }

    /// <summary>
    /// Fetches a folder's two cover pictures. Only rows that have actually been scrolled into view ask for them, and they share
    /// the filmstrip's thumbnail cache and its two workers, so a library of hundreds of folders never floods the machine.
    /// </summary>
    internal async Task LoadFolderCoversAsync(FolderRowViewModel row)
    {
        if (row.CoversRequested)
            return;
        row.CoversRequested = true;
        var path = row.Node.Path;
        var inFolder = Assets
            .Where(item => item.Asset.Kind != MediaKind.Audio && FolderTree.Contains(path, item.FolderKey))
            .Order(Comparer<AssetViewModel>.Create((left, right) => NaturalOrder.Compare(left.Asset.RelativePath, right.Asset.RelativePath)))
            .ToList();
        if (inFolder.Count == 0)
            return;
        try
        {
            row.CoverFirst = await thumbnails.LoadAsync(inFolder[0].Asset, engine, lifetime.Token);
            if (inFolder.Count > 1)
                row.CoverLast = await thumbnails.LoadAsync(inFolder[^1].Asset, engine, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    /// <summary>Clicking a folder in the tree moves the selected tab there. The filmstrip changes at once; nothing is rescanned.</summary>
    partial void OnSelectedFolderRowChanged(FolderRowViewModel? value)
    {
        ShowFolderTags();
        if (suppressFolderSelection || value is null)
            return;
        if (folderFilter.Equals(value.Node.Path, StringComparison.OrdinalIgnoreCase))
            return;
        ApplyFolder(value.Node.Path);
        Status = HasFolderFilter ? $"Showing {visibleCount:N0} files in {value.Node.Name.Split('\\')[^1]} and its subfolders." : "Showing every folder of the workspace.";
    }

    private void ApplyFolder(string path)
    {
        folderFilter = path;
        if (SelectedTab is { } tab && !tab.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            tab.Path = path;
            SaveWorkspace();
        }
        RefreshView();
        UpdateChart();
        UpdateTabs();
        NotifyFolderFilter();
    }

    partial void OnChartSelectedPathChanged(string? value)
    {
        if (value is null) return;
        SelectFolder(value);
        ChartSelectedPath = null;
    }

    [RelayCommand]
    private void StartTour() => TourRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleFolderRow(FolderRowViewModel? row)
    {
        if (row is not { HasChildren: true } || row.Node.Path.Length == 0) return;
        // Decided by what the row shows, so a click always does the opposite of what is on screen.
        if (row.IsWorkspaceFolder)
        {
            if (row.IsExpanded) collapsedTops.Add(row.Node.Path);
            else collapsedTops.Remove(row.Node.Path);
        }
        else if (row.IsExpanded) expandedFolders.Remove(row.Node.Path);
        else expandedFolders.Add(row.Node.Path);
        RebuildFolderRows();
    }

    [RelayCommand]
    private void ExpandAllFolders()
    {
        if (folderRoot is null) return;
        void Walk(FolderNode node) { foreach (var child in node.Children) { if (child.Children.Count > 0) expandedFolders.Add(child.Path); Walk(child); } }
        Walk(folderRoot);
        collapsedTops.Clear();
        RebuildFolderRows();
    }

    [RelayCommand]
    private void CollapseAllFolders()
    {
        expandedFolders.Clear();
        foreach (var folder in WorkspaceFolders) collapsedTops.Add(folder.Label);
        RebuildFolderRows();
    }

    [RelayCommand]
    private void ClearFolderFilter() => SelectFolder("");

    /// <summary>Selects a folder by its normalized path, expanding its ancestors so the row is visible.</summary>
    public void SelectFolder(string path)
    {
        if (!HasFolder(path))
            path = "";
        if (folderRoot is null)
        {
            folderFilter = path;
            return;
        }
        var ancestor = "";
        foreach (var segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ancestor.Length > 0) expandedFolders.Add(ancestor);
            ancestor = ancestor.Length == 0 ? segment : ancestor + "\\" + segment;
        }
        // Collapsed chains use a deeper path than any single segment; expand every real ancestor node on the way down.
        for (var node = folderRoot; node is not null && !node.Path.Equals(path, StringComparison.OrdinalIgnoreCase);
             node = node.Children.FirstOrDefault(child => FolderTree.Contains(child.Path, path)))
            if (node.Path.Length > 0) expandedFolders.Add(node.Path);
        // A workspace folder is opened and closed by collapsedTops alone; going into one of its subfolders opens it.
        if (path.Contains('\\'))
            collapsedTops.Remove(path.Split('\\', 2)[0]);
        RebuildFolderRows(path);
        ApplyFolder(path);
    }

    private void ResetFolderTree()
    {
        folderRoot = null;
        folderFilter = "";
        expandedFolders.Clear();
        removedFolders.Clear();
        NotifyFolderEdits();
        lastFolderTreeBuild = DateTime.MinValue;
        suppressFolderSelection = true;
        try { FolderRows.Clear(); SelectedFolderRow = null; }
        finally { suppressFolderSelection = false; }
        ChartNodes = [];
        ChartTitle = "";
        ChartSubtitle = "";
        NotifyFolderFilter();
    }

    /// <summary>Rebuilds during a scan at most once a second so very large folders do not rebuild the tree for every batch.</summary>
    private void RebuildFolderTreeThrottled()
    {
        if (DateTime.UtcNow - lastFolderTreeBuild > TimeSpan.FromSeconds(1))
            RebuildFolderTree();
    }

    internal void RebuildFolderTree()
    {
        lastFolderTreeBuild = DateTime.UtcNow;
        folderRoot = FolderTree.Build(Assets.Where(IsFolderIncluded).Select(item => (item.FolderKey, item.Asset)), "All folders");
        // Workspace folders are never merged into their only subfolder: each keeps a row of its own at the top.
        folderRoot = KeepWorkspaceRows(folderRoot);
        if (!HasFolder(folderFilter) && !IsScanning)
        {
            folderFilter = "";
            RefreshView();
        }
        RebuildFolderRows(folderFilter);
        UpdateChart();
        UpdateTabs();
        NotifyFolderFilter();
    }

    /// <summary>
    /// Rebuilds the visible rows, reusing the row of every folder that is still there with the same counts, so the list does not
    /// redraw rows that did not change (and their album covers do not blink).
    /// </summary>
    private void RebuildFolderRows(string? select = null)
    {
        select ??= folderFilter;
        suppressFolderSelection = true;
        try
        {
            var previous = FolderRows.ToDictionary(row => row.Node.Path, StringComparer.OrdinalIgnoreCase);
            var rows = new List<FolderRowViewModel>();
            if (folderRoot is not null)
                AddRows(folderRoot, 0, folderRoot.Total, rows);
            for (var index = 0; index < rows.Count; index++)
                if (previous.TryGetValue(rows[index].Node.Path, out var kept) && kept.SameAs(rows[index]))
                    rows[index] = kept.WithNode(rows[index].Node);
            var same = rows.Count == FolderRows.Count && rows.Select((row, index) => ReferenceEquals(row, FolderRows[index])).All(match => match);
            if (!same)
            {
                FolderRows.Clear();
                foreach (var row in rows) FolderRows.Add(row);
            }
            // When the open folder is inside a folder that was just closed, nothing is highlighted rather than the wrong row;
            // the filmstrip stays where it is, and clicking any row goes there.
            SelectedFolderRow = FolderRows.FirstOrDefault(row => row.Node.Path.Equals(select, StringComparison.OrdinalIgnoreCase));
        }
        finally { suppressFolderSelection = false; }
    }

    /// <summary>Undoes the chain merging for the workspace folders themselves, which must each keep a row labelled with their own name.</summary>
    private FolderNode KeepWorkspaceRows(FolderNode root)
    {
        for (var index = 0; index < root.Children.Count; index++)
        {
            var child = root.Children[index];
            if (child.Path.Contains('\\') && FolderOf(child.Path) is { } folder)
            {
                // The merged node's path runs past the label; rebuild the label node above it.
                var top = new FolderNode(folder.Label, folder.Label)
                {
                    Photos = child.Photos, Videos = child.Videos, Audio = child.Audio, Bytes = child.Bytes, FolderCount = child.FolderCount + 1
                };
                child.Name = child.Path[(folder.Label.Length + 1)..];
                top.Children.Add(child);
                root.Children[index] = top;
            }
        }
        return root;
    }

    private void AddRows(FolderNode node, int depth, int parentTotal, List<FolderRowViewModel> rows)
    {
        // Workspace folders start open, so their first level is in view the moment they are added.
        // Workspace folders start open and are closed only through collapsedTops; deeper folders start closed.
        var expanded = depth == 0 || (depth == 1 ? !collapsedTops.Contains(node.Path) : expandedFolders.Contains(node.Path));
        rows.Add(new FolderRowViewModel(node, depth, expanded, parentTotal) { IsWorkspaceFolder = depth == 1, Folder = depth == 1 ? FolderOf(node.Path) : null, HasFolderTags = depth > 0 && FolderHasTags(node) });
        if (!expanded) return;
        foreach (var child in node.Children)
            AddRows(child, depth + 1, node.Total, rows);
    }

    private void UpdateChart()
    {
        if (folderRoot is null) { ChartNodes = []; ChartTitle = ""; ChartSubtitle = ""; UpdateOverview(); return; }
        var node = FolderTree.Find(folderRoot, folderFilter) ?? folderRoot;
        var bars = new List<FolderNode>();
        var children = node.Children.OrderByDescending(child => child.Total).ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase).ToList();
        bars.AddRange(children.Take(ChartBarLimit));
        if (children.Count > ChartBarLimit)
            bars.Add(Synthetic($"{children.Count - ChartBarLimit:N0} other folders", node.Path, children.Skip(ChartBarLimit)));
        if (node.DirectTotal > 0)
            bars.Add(new FolderNode(children.Count == 0 ? "Files in this folder" : "Files directly in this folder", node.Path)
            {
                Photos = node.DirectPhotos, Videos = node.DirectVideos, Audio = node.DirectAudio, Bytes = node.DirectBytes,
                DirectPhotos = node.DirectPhotos, DirectVideos = node.DirectVideos, DirectAudio = node.DirectAudio, DirectBytes = node.DirectBytes
            });
        ChartNodes = bars;
        ChartTitle = children.Count == 0 ? $"Inside {NodeTitle(node)}" : $"Subfolders of {NodeTitle(node)}";
        ChartSubtitle = $"{node.Total:N0} files: {DescribeKinds(node.Photos, node.Videos, node.Audio, percentages: true)}. Each bar shows its share of this folder.";
        UpdateOverview();
    }

    private static FolderNode Synthetic(string name, string path, IEnumerable<FolderNode> nodes)
    {
        var list = nodes.ToList();
        return new FolderNode(name, path) { Photos = list.Sum(item => item.Photos), Videos = list.Sum(item => item.Videos), Audio = list.Sum(item => item.Audio), Bytes = list.Sum(item => item.Bytes) };
    }

    private void NotifyFolderFilter()
    {
        foreach (var property in new[] { nameof(HasFolderFilter), nameof(FolderFilterLabel), nameof(VisibleCount), nameof(HasActiveFilters), nameof(LibrarySummary), nameof(LibraryKindSummary), nameof(ShowLibraryEmpty), nameof(ShowLibraryMap) })
            OnPropertyChanged(property);
    }


    public static string DescribeKinds(int photos, int videos, int audio, bool percentages = false)
    {
        var total = photos + videos + audio;
        string Share(int count) => percentages && total > 0 ? $" ({Percent(count, total)})" : "";
        var parts = new List<string>(3);
        if (photos > 0) parts.Add($"{photos:N0} {(photos == 1 ? "photo" : "photos")}{Share(photos)}");
        if (videos > 0) parts.Add($"{videos:N0} {(videos == 1 ? "video" : "videos")}{Share(videos)}");
        if (audio > 0) parts.Add($"{audio:N0} audio{Share(audio)}");
        return parts.Count == 0 ? "no media" : string.Join(", ", parts);
    }

    /// <summary>Whole-number percentage that never hides a small non-zero share and never rounds a partial share up to 100%.</summary>
    public static string Percent(long part, long total)
    {
        if (total <= 0 || part <= 0) return "0%";
        if (part >= total) return "100%";
        var value = 100.0 * part / total;
        return value < 1 ? "<1%" : value > 99 ? "99%" : $"{Math.Round(value):0}%";
    }

    public static string DescribeSize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):N1} GB" : $"{bytes / 1048576.0:N1} MB";
}
