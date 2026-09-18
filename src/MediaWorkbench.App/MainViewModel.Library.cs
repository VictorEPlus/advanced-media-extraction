using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

/// <summary>One visible row of the folder tree. The tree is flattened so it can use the same list styling and virtualization as the rest of the app.</summary>
public sealed partial class FolderRowViewModel(FolderNode node, int depth, bool isExpanded, int parentTotal) : ObservableObject
{
    public FolderNode Node { get; } = node;
    public int Depth { get; } = depth;
    public bool IsExpanded { get; } = isExpanded;
    public bool HasChildren => Node.Children.Count > 0;
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public string Glyph => !HasChildren ? "" : IsExpanded ? "▾" : "▸";
    public string Name => Node.Name;
    public string CountText => Node.Total.ToString("N0");
    public string KindText => MainViewModel.DescribeKinds(Node.Photos, Node.Videos, Node.Audio, percentages: true);
    /// <summary>This folder as a share of its parent folder, for example 71%. Empty for the root.</summary>
    public string ShareText => Depth == 0 ? "" : MainViewModel.Percent(Node.Total, parentTotal);
    public string ToolTipText => $"{(Node.Path.Length == 0 ? Node.Name : Node.Path)}\n{KindText}, {MainViewModel.DescribeSize(Node.Bytes)}\n{Node.DirectTotal:N0} directly in this folder";
}

public sealed partial class MainViewModel
{
    /// <summary>At most this many subfolders get their own bar; the rest fold into one "other folders" bar.</summary>
    public const int ChartBarLimit = 14;

    private FolderNode? folderRoot;
    private readonly HashSet<string> expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    private string folderFilter = "";
    private bool suppressFolderSelection;
    private DateTime lastFolderTreeBuild = DateTime.MinValue;

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
    public bool ShowLibraryEmpty => !hasSource || Assets.Count == 0;
    public bool ShowLibraryMap => !ShowLibraryEmpty;
    public bool HasFolderFilter => folderFilter.Length > 0;
    public string FolderFilterLabel => HasFolderFilter ? "Folder: " + folderFilter : "";
    public string LibrarySummary => folderRoot is not { Total: > 0 } root ? "" :
        $"{root.Total:N0} files in {root.FolderCount + (root.DirectTotal > 0 ? 1 : 0):N0} {(root.FolderCount + (root.DirectTotal > 0 ? 1 : 0) == 1 ? "folder" : "folders")}, {DescribeSize(root.Bytes)}";
    public string LibraryKindSummary => folderRoot is not { Total: > 0 } root ? "" : DescribeKinds(root.Photos, root.Videos, root.Audio, percentages: true);

    partial void OnMainTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsLibraryTab));
        OnPropertyChanged(nameof(IsPreviewTab));
    }

    partial void OnSelectedFolderRowChanged(FolderRowViewModel? value)
    {
        if (suppressFolderSelection || value is null)
            return;
        if (folderFilter.Equals(value.Node.Path, StringComparison.OrdinalIgnoreCase))
            return;
        folderFilter = value.Node.Path;
        RefreshView();
        UpdateChart();
        NotifyFolderFilter();
        Status = HasFolderFilter ? $"Showing {visibleCount:N0} files in {value.Node.Name} and its subfolders." : "Showing every folder.";
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
        if (!expandedFolders.Remove(row.Node.Path)) expandedFolders.Add(row.Node.Path);
        RebuildFolderRows();
    }

    [RelayCommand]
    private void ExpandAllFolders()
    {
        if (folderRoot is null) return;
        void Walk(FolderNode node) { foreach (var child in node.Children) { if (child.Children.Count > 0) expandedFolders.Add(child.Path); Walk(child); } }
        Walk(folderRoot);
        RebuildFolderRows();
    }

    [RelayCommand]
    private void CollapseAllFolders()
    {
        expandedFolders.Clear();
        RebuildFolderRows();
    }

    [RelayCommand]
    private void ClearFolderFilter() => SelectFolder("");

    /// <summary>Selects a folder by its normalized path, expanding its ancestors so the row is visible.</summary>
    public void SelectFolder(string path)
    {
        if (folderRoot is null || FolderTree.Find(folderRoot, path) is null)
            return;
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
        folderFilter = path;
        RebuildFolderRows();
        RefreshView();
        UpdateChart();
        NotifyFolderFilter();
    }

    private void ResetFolderTree()
    {
        folderRoot = null;
        folderFilter = "";
        expandedFolders.Clear();
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
        folderRoot = FolderTree.Build(Assets.Select(item => (item.FolderKey, item.Asset)), string.IsNullOrWhiteSpace(SourceName) ? "All folders" : SourceName);
        if (folderFilter.Length > 0 && FolderTree.Find(folderRoot, folderFilter) is null)
        {
            folderFilter = "";
            RefreshView();
        }
        RebuildFolderRows();
        UpdateChart();
        NotifyFolderFilter();
    }

    private void RebuildFolderRows()
    {
        suppressFolderSelection = true;
        try
        {
            FolderRows.Clear();
            if (folderRoot is not null)
                AddRows(folderRoot, 0, folderRoot.Total);
            SelectedFolderRow = FolderRows.FirstOrDefault(row => row.Node.Path.Equals(folderFilter, StringComparison.OrdinalIgnoreCase)) ?? FolderRows.FirstOrDefault();
        }
        finally { suppressFolderSelection = false; }
    }

    private void AddRows(FolderNode node, int depth, int parentTotal)
    {
        var expanded = node.Path.Length == 0 || expandedFolders.Contains(node.Path);
        FolderRows.Add(new FolderRowViewModel(node, depth, expanded, parentTotal));
        if (!expanded) return;
        foreach (var child in node.Children)
            AddRows(child, depth + 1, node.Total);
    }

    private void UpdateChart()
    {
        if (folderRoot is null) { ChartNodes = []; ChartTitle = ""; ChartSubtitle = ""; return; }
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
        ChartTitle = children.Count == 0 ? $"Inside {node.Name}" : $"Subfolders of {node.Name}";
        ChartSubtitle = $"{node.Total:N0} files: {DescribeKinds(node.Photos, node.Videos, node.Audio, percentages: true)}. Each bar shows its share of this folder.";
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

    private string FolderKeyOf(MediaAsset asset) =>
        FolderTree.Normalize(virtualSource ? asset.Root : System.IO.Path.GetDirectoryName(asset.RelativePath));

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
