using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

/// <summary>
/// One folder open in the workspace, or a collection or tag search shown alongside them. Everything inside it has a folder key
/// that begins with <see cref="Label"/>, so the one tree, filter and filmstrip serve every folder at once.
/// </summary>
public sealed partial class WorkspaceFolder(string path, string label, string? collectionPath = null, bool isVirtual = false) : ObservableObject
{
    /// <summary>The folder on disk; empty for a collection or a tag search.</summary>
    public string Path { get; } = path;
    public string Label { get; } = label;
    public bool IsVirtual { get; } = isVirtual;
    /// <summary>For a collection: the JSON file that lists its files.</summary>
    public string? CollectionPath { get; } = collectionPath;
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private string state = "";
    internal CancellationTokenSource? Scan;
    internal FileSystemWatcher? Watcher;
    internal DispatcherTimer? Settle;
    internal bool ChangedWhileScanning;
}

/// <summary>A folder view open as a tab over the filmstrip. Clicking a folder in the tree moves the selected tab there, like a browser.</summary>
public sealed partial class FolderTab : ObservableObject
{
    [ObservableProperty] private string path = "";
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string countText = "";
}

public sealed partial class MainViewModel
{
    public ObservableCollection<WorkspaceFolder> WorkspaceFolders { get; } = [];
    public ObservableCollection<FolderTab> Tabs { get; } = [];
    [ObservableProperty] private FolderTab? selectedTab;
    public bool CanCloseTab => Tabs.Count > 1;

    /// <summary>The workspace folder the selected tab is in, or null for the view of everything.</summary>
    public WorkspaceFolder? CurrentFolder => FolderOf(folderFilter);

    private WorkspaceFolder? FolderOf(string key)
    {
        var label = key.Split('\\', 2)[0];
        return label.Length == 0 ? null : WorkspaceFolders.FirstOrDefault(folder => folder.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a folder beside the ones already open. A folder opened before shows at once from its saved index; changes are picked up in the background.</summary>
    public async Task<WorkspaceFolder> AddFolderAsync(string path, bool show = true)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Not found: {path}");
        if (WorkspaceFolders.FirstOrDefault(folder => !folder.IsVirtual && folder.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            if (show) ShowFolder(open.Label);
            return open;
        }
        var added = new WorkspaceFolder(path, Workspace.Label(path, WorkspaceFolders.Select(folder => folder.Label)));
        WorkspaceFolders.Add(added);
        hasSource = true;
        var indexed = await Task.Run(() => catalog.ReadIndex(path), lifetime.Token);
        if (!WorkspaceFolders.Contains(added))
            return added;
        Assets.AddRange(indexed.Select(entry => CreateItem(added, entry.Asset, entry.Favorite)));
        added.State = indexed.Count == 0 ? "Reading…" : $"{indexed.Count:N0} files";
        AfterAssetsChanged();
        if (show) ShowFolder(added.Label);
        SaveWorkspace();
        Watch(added);
        _ = ScanFolderAsync(added, firstTime: indexed.Count == 0);
        return added;
    }

    /// <summary>For the tests and the probe: the workspace holds only this folder, fully scanned, with it selected.</summary>
    public async Task OpenLibraryAsync(string root)
    {
        foreach (var folder in WorkspaceFolders.ToArray())
            CloseFolder(folder, save: false);
        SelectedAsset = null;
        Assets.Clear();
        Tabs.Clear();
        var added = await AddFolderAsync(root);
        LibraryRoot = added.Path;
        SourceName = added.Label;
        while (added.IsScanning || added.Scan is not null)
            await Task.Delay(20, lifetime.Token);
    }

    private AssetViewModel CreateItem(WorkspaceFolder folder, MediaAsset asset, bool favorite) =>
        new(asset, favorite)
        {
            Owner = folder,
            Tags = TagsFor(asset),
            // A folder on disk: its label, then the file's subfolders. A collection or tag search: its label, then the file's whole folder.
            FolderKey = folder.IsVirtual ? Workspace.FolderKey(folder.Label, FolderTree.Normalize(asset.Root)) : Workspace.FolderKey(folder.Label, Path.GetDirectoryName(asset.RelativePath))
        };

    /// <summary>
    /// Reads the folder and brings the workspace up to date with it. The first time, files appear in batches as they are found;
    /// after that the whole folder is listed first and only what changed is added, updated or removed, in one step.
    /// </summary>
    private async Task ScanFolderAsync(WorkspaceFolder folder, bool firstTime)
    {
        if (folder.IsVirtual)
            return;
        if (folder.Scan is not null)
        {
            folder.ChangedWhileScanning = true;
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        folder.Scan = cancellation;
        folder.IsScanning = true;
        folder.ChangedWhileScanning = false;
        UpdateScanning();
        var root = folder.Path;
        try
        {
            if (firstTime)
            {
                var favorites = catalog.GetFavorites(root);
                await Task.Run(async () =>
                {
                    var batch = new List<MediaAsset>(500);
                    foreach (var asset in new LibraryScanner().Scan(root, cancellation.Token))
                    {
                        batch.Add(asset);
                        if (batch.Count < 500) continue;
                        await AddScannedAsync(folder, batch.ToArray(), favorites, cancellation.Token);
                        batch.Clear();
                    }
                    if (batch.Count > 0)
                        await AddScannedAsync(folder, batch.ToArray(), favorites, cancellation.Token);
                }, cancellation.Token);
            }
            else
            {
                var current = Assets.Where(item => item.Owner == folder).Select(item => item.Asset).ToList();
                var (added, changed, removed) = await Task.Run(() =>
                {
                    var scanned = new LibraryScanner().Scan(root, cancellation.Token).ToList();
                    var difference = Workspace.Diff(current, scanned);
                    catalog.Index(difference.Added.Concat(difference.Changed), cancellation.Token);
                    catalog.Prune(root, scanned.Select(asset => asset.RelativePath).ToList());
                    return difference;
                }, cancellation.Token);
                if (added.Count + changed.Count + removed.Count > 0)
                {
                    var gone = new HashSet<string>(removed.Concat(changed.Select(asset => asset.RelativePath)), StringComparer.OrdinalIgnoreCase);
                    var favorites = changed.Count > 0 ? catalog.GetFavorites(root) : [];
                    Assets.RemoveWhere(item => item.Owner == folder && gone.Contains(item.Asset.RelativePath));
                    Assets.AddRange(added.Concat(changed).Select(asset => CreateItem(folder, asset, favorites.Contains(asset.RelativePath))));
                    AfterAssetsChanged();
                    Status = $"{folder.Label}: {added.Count:N0} new, {changed.Count:N0} changed, {removed.Count:N0} gone.";
                }
            }
            settings = settings.WithRecentLibrary(root);
            Guard(() => settingsStore.Save(WithBrowsingPreferences(settings)));
            RefreshRecentLibraries();
            if (pendingSelectionPath is { } missing && missing.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                pendingSelectionPath = null;
                Notify(NotificationKind.Info, $"{Path.GetFileName(missing)} was not found in {folder.Label}.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            if (folder.Scan == cancellation)
            {
                folder.Scan = null;
                folder.IsScanning = false;
                folder.State = $"{Assets.Count(item => item.Owner == folder):N0} files";
            }
            UpdateScanning();
            AfterAssetsChanged();
            if (folder.ChangedWhileScanning && WorkspaceFolders.Contains(folder))
                _ = ScanFolderAsync(folder, firstTime: false);
            else if (WorkspaceFolders.Contains(folder) && !cancellation.IsCancellationRequested)
                _ = ReconcileTagsAsync(folder);
        }
    }

    private async Task AddScannedAsync(WorkspaceFolder folder, MediaAsset[] batch, HashSet<string> favorites, CancellationToken token)
    {
        catalog.Index(batch, token);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            Assets.AddRange(batch.Select(asset => CreateItem(folder, asset, favorites.Contains(asset.RelativePath))));
            folder.State = $"Reading… {Assets.Count(item => item.Owner == folder):N0} files";
            AfterBatchAdded();
        }, DispatcherPriority.Background, token);
    }

    /// <summary>Changes on disk are picked up while the app runs: a folder that changes is checked again once it has been quiet for a moment.</summary>
    private void Watch(WorkspaceFolder folder)
    {
        try
        {
            var watcher = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            folder.Settle = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) =>
            {
                folder.Settle?.Stop();
                if (WorkspaceFolders.Contains(folder))
                    _ = ScanFolderAsync(folder, firstTime: false);
            }, Application.Current.Dispatcher);
            folder.Settle.Stop();
            void Changed() => Application.Current.Dispatcher.BeginInvoke(() => { folder.Settle?.Stop(); folder.Settle?.Start(); });
            watcher.Created += (_, _) => Changed();
            watcher.Deleted += (_, _) => Changed();
            watcher.Renamed += (_, _) => Changed();
            watcher.Changed += (_, _) => Changed();
            watcher.Error += (_, _) => Changed();
            watcher.EnableRaisingEvents = true;
            folder.Watcher = watcher;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    [RelayCommand]
    private void RemoveWorkspaceFolder(FolderRowViewModel? row)
    {
        if (row is null || FolderOf(row.Node.Path) is not { } folder)
            return;
        CloseFolder(folder, save: true);
        Status = $"{folder.Label} is no longer in the workspace. Nothing on disk was changed.";
    }

    private void CloseFolder(WorkspaceFolder folder, bool save)
    {
        folder.Scan?.Cancel();
        folder.Scan = null;
        folder.Settle?.Stop();
        folder.Watcher?.Dispose();
        folder.Watcher = null;
        WorkspaceFolders.Remove(folder);
        if (SelectedAsset?.Owner == folder)
            SelectedAsset = null;
        Assets.RemoveWhere(item => item.Owner == folder);
        foreach (var tab in Tabs.Where(tab => FolderTree.Contains(folder.Label, tab.Path)).ToArray())
            tab.Path = "";
        hasSource = WorkspaceFolders.Count > 0;
        UpdateScanning();
        AfterAssetsChanged();
        if (save) SaveWorkspace();
    }

    /// <summary>Shows a collection or a tag search as one more entry in the workspace, beside the folders rather than instead of them.</summary>
    private async Task ShowVirtualFolderAsync(string label, string[] paths, string? collectionPath)
    {
        if (WorkspaceFolders.FirstOrDefault(folder => folder.IsVirtual && folder.Label.Equals(label, StringComparison.OrdinalIgnoreCase)) is { } previous)
            CloseFolder(previous, save: false);
        var folder = new WorkspaceFolder("", Workspace.Label(label, WorkspaceFolders.Select(item => item.Label)), collectionPath, isVirtual: true);
        WorkspaceFolders.Add(folder);
        hasSource = true;
        folder.IsScanning = true;
        var missing = 0;
        try
        {
            var found = await Task.Run(() =>
            {
                var list = new List<MediaAsset>();
                foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    MediaAsset? asset = null;
                    try { asset = LibraryScanner.ReadFile(path); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                    if (asset is null) missing++;
                    else list.Add(asset);
                }
                catalog.Index(list);
                return list;
            }, lifetime.Token);
            var favorites = catalog.GetFavoritePaths();
            Assets.AddRange(found.Select(asset => CreateItem(folder, asset, favorites.Contains(asset.FullPath))));
            folder.State = $"{found.Count:N0} files" + (missing > 0 ? $", {missing:N0} missing" : "");
            if (missing > 0)
                Notify(NotificationKind.Info, $"{missing:N0} referenced files are missing or unsupported. Their references were kept.");
        }
        catch (OperationCanceledException) { }
        finally
        {
            folder.IsScanning = false;
            AfterAssetsChanged();
            ShowFolder(folder.Label);
        }
    }

    /// <summary>Everything that depends on the set of files: counts, the tree and the empty states.</summary>
    private void AfterAssetsChanged()
    {
        OnPropertyChanged(nameof(LibraryLabel));
        UpdateVisibleCount();
        RebuildFolderTree();
        NotifyEmptyState();
        OnPropertyChanged(nameof(IsSelectionHidden));
        OnPropertyChanged(nameof(CurrentFolder));
        OnPropertyChanged(nameof(IsCollectionView));
    }

    private void UpdateScanning() => IsScanning = WorkspaceFolders.Any(folder => folder.IsScanning);

    /// <summary>Moves the selected tab to a folder (or opens the first tab). Nothing is rescanned; the filmstrip just shows that folder.</summary>
    public void ShowFolder(string path)
    {
        if (SelectedTab is null)
        {
            var tab = new FolderTab();
            Tabs.Add(tab);
            SelectedTab = tab;
            OnPropertyChanged(nameof(CanCloseTab));
        }
        SelectFolder(path);
    }

    partial void OnSelectedTabChanged(FolderTab? value)
    {
        if (value is null) return;
        SelectFolder(value.Path);
        SaveWorkspace();
    }

    [RelayCommand]
    private void NewTab()
    {
        if (Tabs.Count >= AppSettings.WorkspaceTabLimit) return;
        var tab = new FolderTab { Path = folderFilter };
        Tabs.Add(tab);
        SelectedTab = tab;
        OnPropertyChanged(nameof(CanCloseTab));
    }

    /// <summary>Opens a folder from the tree in a tab of its own, keeping the current tab where it is.</summary>
    [RelayCommand]
    private void OpenInNewTab(FolderRowViewModel? row)
    {
        if (row is null || Tabs.Count >= AppSettings.WorkspaceTabLimit) return;
        var tab = new FolderTab { Path = row.Node.Path };
        Tabs.Add(tab);
        SelectedTab = tab;
        OnPropertyChanged(nameof(CanCloseTab));
    }

    [RelayCommand]
    private void CloseTab(FolderTab? tab)
    {
        if (tab is null || Tabs.Count <= 1) return;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        OnPropertyChanged(nameof(CanCloseTab));
        SaveWorkspace();
    }

    /// <summary>Tab names and counts follow the tree: the folder's own name and how many files it holds, subfolders included.</summary>
    private void UpdateTabs()
    {
        foreach (var tab in Tabs)
        {
            var node = folderRoot is null ? null : FolderTree.Find(folderRoot, tab.Path);
            tab.Title = tab.Path.Length == 0 ? "All folders" : node?.Name.Split('\\')[^1] ?? tab.Path.Split('\\')[^1];
            tab.CountText = node is null ? "" : node.Total.ToString("N0");
        }
    }

    private void SaveWorkspace()
    {
        if (disposed) return;
        settings = settings with
        {
            WorkspaceRoots = WorkspaceFolders.Where(folder => !folder.IsVirtual).Select(folder => folder.Path).Take(AppSettings.WorkspaceRootLimit).ToArray(),
            WorkspaceTabs = Tabs.Select(tab => tab.Path).Take(AppSettings.WorkspaceTabLimit).ToArray(),
            SelectedTab = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab))
        };
        Guard(() => settingsStore.Save(WithBrowsingPreferences(settings)));
    }

    /// <summary>Opens the folders and tabs of the last session, each from its saved index, then checks them for changes one after another.</summary>
    private async Task RestoreWorkspaceAsync()
    {
        var roots = settings.WorkspaceRoots.Length > 0 ? settings.WorkspaceRoots
            : Directory.Exists(settings.LastLibrary) ? [settings.LastLibrary] : [];
        var tabs = settings.WorkspaceTabs;
        var selected = settings.SelectedTab;
        foreach (var root in roots.Where(Directory.Exists))
            await AddFolderAsync(root, show: false);
        Tabs.Clear();
        foreach (var path in tabs.Length > 0 ? tabs : WorkspaceFolders.Take(1).Select(folder => folder.Label))
            Tabs.Add(new FolderTab { Path = folderRoot is not null && FolderTree.Find(folderRoot, path) is not null ? path : "" });
        if (Tabs.Count == 0 && WorkspaceFolders.Count > 0)
            Tabs.Add(new FolderTab { Path = WorkspaceFolders[0].Label });
        OnPropertyChanged(nameof(CanCloseTab));
        if (Tabs.Count > 0)
            SelectedTab = Tabs[Math.Clamp(selected, 0, Tabs.Count - 1)];
    }

    private void DisposeWorkspace()
    {
        foreach (var folder in WorkspaceFolders)
        {
            folder.Settle?.Stop();
            folder.Watcher?.Dispose();
        }
    }
}
