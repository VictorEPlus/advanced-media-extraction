using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using MediaWorkbench.Avalonia.Services;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia.ViewModels;

/// <summary>
/// The whole window: the workspace folders and their tree, the filmstrip of the folder chosen, and the viewer with its
/// frame-exact stepping and playback. The media work itself (scanning, the catalog, FFmpeg) is MediaWorkbench.Core, shared with
/// the WPF app.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWorkbench.Avalonia");

    private readonly string dataDirectory;
    private readonly SettingsStore settingsStore;
    private readonly CatalogStore catalog;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<WorkspaceRoot> roots = [];
    private AppSettings settings;
    private readonly ToolPaths tools;
    private MediaEngine engine;
    private ThumbnailService thumbnails;
    private LibVLC? libVlc;
    private MediaPlayer? player;
    private VideoBridge? bridge;
    private CancellationTokenSource? openCancellation;
    private CancellationTokenSource? seekCancellation;
    private IReadOnlyList<VideoFrame> frames = [];
    private MediaInfo? info;
    private DispatcherTimer? playClock;
    private bool settingFrameFromPlayer;
    private bool disposed;

    public ShellViewModel(string dataDirectory)
    {
        this.dataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        settingsStore = new SettingsStore(Path.Combine(dataDirectory, "settings.json"));
        try { settings = settingsStore.Load(); }
        catch (Exception) { settings = new AppSettings(); }
        catalog = new CatalogStore(Path.Combine(dataDirectory, "catalog.db"));
        tools = ToolPaths.Resolve(settings.FfmpegDirectory);
        engine = new MediaEngine(tools, Path.Combine(dataDirectory, "cache"), settings.CacheMegabytes);
        thumbnails = new ThumbnailService(engine);
        isSidebarOpen = settings.ShowSources;
    }

    // ---------------------------------------------------------------- Workspace and tree

    public ObservableCollection<FolderNodeViewModel> Folders { get; } = [];
    [ObservableProperty] private FolderNodeViewModel? selectedFolder;
    [ObservableProperty] private IReadOnlyList<MediaItemViewModel> items = [];
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool isSidebarOpen;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool isScanning;

    public bool HasFolders => roots.Count > 0;
    public bool IsEmpty => roots.Count == 0;
    public string LocationTitle => SelectedFolder is null ? "All media" : SelectedFolder.Name;
    public string LocationCount => $"{Items.Count:N0} {(Items.Count == 1 ? "file" : "files")}";
    public double SidebarWidth => IsSidebarOpen ? 264 : 0;

    partial void OnIsSidebarOpenChanged(bool value) => OnPropertyChanged(nameof(SidebarWidth));
    partial void OnSelectedFolderChanged(FolderNodeViewModel? value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnItemsChanged(IReadOnlyList<MediaItemViewModel> value) => OnPropertyChanged(nameof(LocationCount));

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private void ShowAll() => SelectedFolder = null;

    /// <summary>Starts the app: the folders of last time (or, the first time, those of the WPF app), each from its saved index.</summary>
    /// <summary>For checking by hand: start playing the file given with --open as soon as it is ready.</summary>
    public bool PlayWhenReady { get; set; }

    public async Task StartAsync(string? openPath = null)
    {
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            libVlc = new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--quiet");
            player = new MediaPlayer(libVlc);
            bridge = new VideoBridge();
            bridge.Attach(player);
            bridge.PictureShown += OnLivePicture;
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(OnEndReached);
        }
        catch (Exception exception) { Status = "Video playback is unavailable: " + exception.Message; }

        var saved = settings.WorkspaceRoots.Length > 0 ? settings.WorkspaceRoots : ImportFromClassicApp();
        foreach (var root in saved.Where(Directory.Exists))
            await AddFolderAsync(root, select: false);
        if (openPath is not null && FindItem(Path.GetFullPath(openPath)) is { } requested)
            SelectedItem = requested;
        else if (settings.SelectedFile.Length > 0 && FindItem(settings.SelectedFile) is { } last)
            SelectedItem = last;
        try { await tools.CheckAsync(lifetime.Token); }
        catch (Exception exception) { Status = "FFmpeg was not found, so videos cannot be stepped or thumbnailed: " + exception.Message; }
    }

    /// <summary>The first time, the folders open in the WPF app are opened here too.</summary>
    private static string[] ImportFromClassicApp()
    {
        try
        {
            var classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWorkbench", "settings.json");
            return File.Exists(classic) ? new SettingsStore(classic).Load().WorkspaceRoots : [];
        }
        catch (Exception) { return []; }
    }

    /// <summary>Adds a folder beside the open ones. One already open is just shown.</summary>
    public async Task AddFolderAsync(string path, bool select = true)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (roots.FirstOrDefault(root => root.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            SelectedFolder = open.Node;
            Status = $"{path} is already in the workspace.";
            return;
        }
        if (!Directory.Exists(path))
        {
            Status = "Not found: " + path;
            return;
        }
        var root = new WorkspaceRoot(path, Workspace.Label(path, roots.Select(other => other.Label)));
        roots.Add(root);
        var indexed = await Task.Run(() => catalog.ReadIndex(path));
        AddItems(root, indexed.Select(entry => entry.Asset));
        RebuildTree(root);
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(IsEmpty));
        SaveSettings();
        if (select) SelectedFolder = root.Node;
        else ApplyFilter();
        _ = ScanAsync(root);
    }

    [RelayCommand]
    private void RemoveFolder(FolderNodeViewModel? node)
    {
        if (node?.Root is not { } root) return;
        root.Scan?.Cancel();
        roots.Remove(root);
        if (root.Node is not null) Folders.Remove(root.Node);
        if (SelectedFolder?.Root == root) SelectedFolder = null;
        if (SelectedItem?.RootLabel == root.Label) SelectedItem = null;
        ApplyFilter();
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(IsEmpty));
        SaveSettings();
        Status = $"{root.DisplayName} is no longer in the workspace. Nothing on disk was changed.";
    }

    [RelayCommand]
    private void RevealFolder(FolderNodeViewModel? node)
    {
        if (node?.Root is not { } root) return;
        var relative = node.Key.Length > root.Label.Length ? node.Key[(root.Label.Length + 1)..] : "";
        Open(Path.Combine(root.Path, relative));
    }

    /// <summary>Reads the folder again and brings the list up to date with what is on disk.</summary>
    private async Task ScanAsync(WorkspaceRoot root)
    {
        root.Scan?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        root.Scan = cancellation;
        if (root.Node is not null) root.Node.IsBusy = true;
        IsScanning = true;
        try
        {
            var scanned = await Task.Run(() =>
            {
                var list = new LibraryScanner().Scan(root.Path, cancellation.Token).ToList();
                catalog.Index(list, cancellation.Token);
                catalog.Prune(root.Path, list.Select(asset => asset.RelativePath).ToList());
                return list;
            }, cancellation.Token);
            var keep = scanned.Select(asset => asset.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gone = root.Items.Keys.Where(path => !keep.Contains(path)).ToList();
            foreach (var path in gone) root.Items.Remove(path);
            var changed = gone.Count > 0;
            foreach (var asset in scanned)
            {
                if (root.Items.TryGetValue(asset.FullPath, out var existing) && existing.Asset == asset) continue;
                root.Items[asset.FullPath] = CreateItem(root, asset);
                changed = true;
            }
            if (changed)
            {
                RebuildTree(root);
                ApplyFilter();
            }
            Status = $"{root.DisplayName}: {root.Items.Count:N0} files.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Status = $"Could not read {root.Path}: {exception.Message}"; }
        finally
        {
            if (root.Scan == cancellation) root.Scan = null;
            if (root.Node is not null) root.Node.IsBusy = false;
            IsScanning = roots.Any(other => other.Scan is not null);
        }
    }

    private void AddItems(WorkspaceRoot root, IEnumerable<MediaAsset> assets)
    {
        foreach (var asset in assets)
            root.Items[asset.FullPath] = CreateItem(root, asset);
    }

    private MediaItemViewModel CreateItem(WorkspaceRoot root, MediaAsset asset) =>
        new(asset, root.Label, root.FolderKeyOf(asset)) { RequestThumbnail = LoadThumbnailAsync };

    private async Task LoadThumbnailAsync(MediaItemViewModel item)
    {
        var bitmap = await thumbnails.LoadAsync(item, lifetime.Token);
        if (bitmap is null) return;
        item.SetThumbnail(bitmap);
        foreach (var old in thumbnails.Touch(item)) old.ReleaseThumbnail();
    }

    /// <summary>Builds the folder's tree from its files; the folder's row keeps its place and its open subfolders stay open.</summary>
    private void RebuildTree(WorkspaceRoot root)
    {
        var tree = FolderTree.Build(root.Items.Values.Select(item => (item.FolderKey, item.Asset)), "");
        var top = tree.Children.FirstOrDefault(child => child.Path.Equals(root.Label, StringComparison.OrdinalIgnoreCase))
            ?? (tree.Children.FirstOrDefault() is { } merged && merged.Path.StartsWith(root.Label + "\\", StringComparison.OrdinalIgnoreCase) ? Wrap(root.Label, merged) : new FolderNode(root.Label, root.Label));
        if (root.Node is null)
        {
            root.Node = new FolderNodeViewModel(top, root, isRoot: true);
            Folders.Add(root.Node);
        }
        else root.Node.Update(top);
    }

    /// <summary>A workspace folder that only holds one subfolder chain is merged by the tree builder; it keeps its own row here.</summary>
    private static FolderNode Wrap(string label, FolderNode merged)
    {
        var top = new FolderNode(label, label) { Photos = merged.Photos, Videos = merged.Videos, Audio = merged.Audio, Bytes = merged.Bytes };
        merged.Name = merged.Path[(label.Length + 1)..];
        top.Children.Add(merged);
        return top;
    }

    private void ApplyFilter()
    {
        var folder = SelectedFolder;
        var query = SearchText.Trim();
        IEnumerable<MediaItemViewModel> source = folder is null ? roots.SelectMany(root => root.Items.Values) : folder.Root.Items.Values;
        if (folder is not null && !folder.IsRoot)
            source = source.Where(item => FolderTree.Contains(folder.Key, item.FolderKey));
        if (query.Length > 0)
            source = source.Where(item => item.Asset.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase));
        Items = source.OrderBy(item => item.Asset.RelativePath, Comparer<string>.Create(NaturalOrder.Compare)).ToList();
        OnPropertyChanged(nameof(LocationTitle));
    }

    private MediaItemViewModel? FindItem(string path) =>
        roots.Select(root => root.Items.GetValueOrDefault(path)).FirstOrDefault(item => item is not null);

    // ---------------------------------------------------------------- Viewer

    [ObservableProperty] private MediaItemViewModel? selectedItem;
    /// <summary>What the viewer draws: a photo, the exact decoded frame, or the live picture while playing.</summary>
    [ObservableProperty] private Bitmap? picture;
    /// <summary>Goes up when the live bitmap's pixels change, so the viewer redraws the same bitmap object.</summary>
    [ObservableProperty] private int pictureRevision;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isPlaying;
    [ObservableProperty] private bool isIndexing;
    [ObservableProperty] private int currentFrame;
    [ObservableProperty] private string facts = "";

    public bool HasSelection => SelectedItem is not null;
    public bool IsVideo => SelectedItem?.IsVideo == true;
    public bool CanPlay => SelectedItem is { IsPhoto: false } && player is not null;
    public bool CanStep => IsVideo && frames.Count > 0;
    public int MaximumFrame => Math.Max(0, frames.Count - 1);
    /// <summary>The scrubber's end; never zero, so an empty scrubber rests at the start rather than the end.</summary>
    public int ScrubberMaximum => Math.Max(1, MaximumFrame);
    /// <summary>The player's own clock, shown while the frame index is still being built.</summary>
    [ObservableProperty] private double playerSeconds;
    public string Title => SelectedItem?.Name ?? "";
    public string Subtitle => SelectedItem is { } item ? Path.GetDirectoryName(item.Asset.FullPath) ?? "" : "";
    public string FrameText => CanStep ? $"{CurrentFrame + 1:N0} / {frames.Count:N0}" : IsIndexing ? "Indexing frames…" : "";
    public string TimeText => CanStep ? Timecode(frames[Math.Clamp(CurrentFrame, 0, MaximumFrame)].Time) : info is null ? "" : Timecode(PlayerSeconds);
    public string DurationText => info is { } media ? Timecode(media.Duration) : "";

    private static string Timecode(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture) : time.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    partial void OnSelectedItemChanged(MediaItemViewModel? value) => _ = OpenAsync(value);
    partial void OnPlayerSecondsChanged(double value) => OnPropertyChanged(nameof(TimeText));

    partial void OnCurrentFrameChanged(int value)
    {
        NotifyFrame();
        if (!settingFrameFromPlayer && CanStep)
            _ = ShowFrameAsync(value);
    }

    private void NotifyFrame()
    {
        OnPropertyChanged(nameof(FrameText));
        OnPropertyChanged(nameof(TimeText));
    }

    private void NotifyMedia()
    {
        foreach (var property in new[] { nameof(HasSelection), nameof(IsVideo), nameof(CanPlay), nameof(CanStep), nameof(MaximumFrame), nameof(ScrubberMaximum), nameof(Title), nameof(Subtitle), nameof(DurationText) })
            OnPropertyChanged(property);
        NotifyFrame();
    }

    /// <summary>Opens a file: a photo is decoded at screen size; a video shows its first frame and is indexed for exact stepping.</summary>
    private async Task OpenAsync(MediaItemViewModel? item)
    {
        StopPlayback();
        openCancellation?.Cancel();
        seekCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        openCancellation = cancellation;
        frames = [];
        info = null;
        Facts = "";
        PlayerSeconds = 0;
        settingFrameFromPlayer = true;
        CurrentFrame = 0;
        settingFrameFromPlayer = false;
        NotifyMedia();
        if (item is null)
        {
            Picture = null;
            return;
        }
        IsLoading = true;
        try
        {
            var asset = item.Asset;
            if (asset.Kind == MediaKind.Photo)
            {
                var photo = await Task.Run(() => DecodePhoto(asset), cancellation.Token);
                if (photo is null)
                {
                    var bytes = await engine.GetFrameAsync(asset, 0, cancellation.Token);
                    photo = await Task.Run(() => Decode(bytes), cancellation.Token);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                Picture = photo;
                Facts = Join($"{photo.PixelSize.Width} × {photo.PixelSize.Height}", Size(asset.Length));
                return;
            }
            var probe = await engine.ProbeAsync(asset.FullPath, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            info = probe;
            var rate = FrameRateInfo.FromRatio(probe.Metadata.GetValueOrDefault("avg_frame_rate"));
            int.TryParse(probe.Metadata.GetValueOrDefault("width"), out var width);
            int.TryParse(probe.Metadata.GetValueOrDefault("height"), out var height);
            Facts = Join(width > 0 ? $"{width} × {height}" : "", rate is null ? "" : $"{rate.FramesPerSecond:0.##} fps", Timecode(probe.Duration), Size(asset.Length));
            NotifyMedia();
            if (asset.Kind == MediaKind.Audio)
            {
                Picture = null;
                return;
            }
            var first = engine.TryGetCachedFrame(asset, 0) ?? await engine.GetFrameAsync(asset, 0, cancellation.Token);
            Picture = await Task.Run(() => Decode(first), cancellation.Token);
            _ = IndexAsync(item, probe, cancellation.Token);
            // Playing does not wait for the frame index; only stepping needs it.
            if (PlayWhenReady)
            {
                PlayWhenReady = false;
                PlayPause();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (SelectedItem == item)
            {
                Picture = null;
                Status = $"{item.Name} could not be opened. It may be damaged, still being copied, or not really a {item.Asset.Kind.ToString().ToLowerInvariant()} file.";
            }
        }
        finally
        {
            if (openCancellation == cancellation) IsLoading = false;
        }
    }

    private async Task IndexAsync(MediaItemViewModel item, MediaInfo probe, CancellationToken token)
    {
        IsIndexing = true;
        NotifyFrame();
        try
        {
            var indexed = await engine.IndexFramesAsync(item.Asset, probe, token);
            if (SelectedItem != item) return;
            frames = indexed;
            NotifyMedia();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Status = "Frames could not be indexed: " + exception.Message; }
        finally
        {
            IsIndexing = false;
            NotifyFrame();
        }
    }

    /// <summary>Shows exactly one decoded frame. Rapid steps cancel the ones before, so holding an arrow key never queues decodes.</summary>
    private async Task ShowFrameAsync(int index)
    {
        if (SelectedItem is not { } item || info is not { } media) return;
        seekCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        seekCancellation = cancellation;
        try
        {
            var bytes = await Task.Run(() => engine.TryGetCachedFrame(item.Asset, index), cancellation.Token);
            if (bytes is null)
            {
                await Task.Delay(40, cancellation.Token);
                bytes = await engine.GetFrameAsync(item.Asset, index, frames, media, cancellation.Token);
            }
            var bitmap = await Task.Run(() => Decode(bytes), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsPlaying) Picture = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Status = $"Frame {index + 1:N0} could not be decoded: {exception.Message}"; }
    }

    [RelayCommand]
    private void StepForward() { if (CanStep && !IsPlaying) CurrentFrame = Math.Min(MaximumFrame, CurrentFrame + 1); }

    [RelayCommand]
    private void StepBack() { if (CanStep && !IsPlaying) CurrentFrame = Math.Max(0, CurrentFrame - 1); }

    /// <summary>Play from the frame on screen, or pause on the frame that was showing.</summary>
    [RelayCommand]
    private void PlayPause()
    {
        if (player is null || libVlc is null || SelectedItem is not { IsPhoto: false } item) return;
        if (IsPlaying)
        {
            PauseOnShownFrame();
            return;
        }
        // Rounded up to the millisecond: rounding down lands just before the frame and shows the previous one first.
        var startMs = CanStep ? (long)Math.Ceiling(frames[Math.Clamp(CurrentFrame, 0, MaximumFrame)].Time * 1000)
            : info is { } probed && PlayerSeconds < probed.Duration - 0.05 ? (long)(PlayerSeconds * 1000) : 0;
        using var media = new Media(libVlc, item.Asset.FullPath, FromType.FromPath);
        media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        if (!player.Play(media))
        {
            Status = "VLC could not play this file.";
            return;
        }
        IsPlaying = true;
        playClock ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => FollowPlayer());
        playClock.Start();
    }

    [RelayCommand]
    private void Restart()
    {
        if (!CanPlay) return;
        StopPlayback();
        PlayerSeconds = 0;
        if (CanStep) CurrentFrame = 0;
        PlayPause();
    }

    /// <summary>The frame counter and scrubber follow the player while it plays.</summary>
    private void FollowPlayer()
    {
        if (player is null || !IsPlaying) return;
        PlayerSeconds = player.Time / 1000.0;
        if (!CanStep) return;
        settingFrameFromPlayer = true;
        CurrentFrame = FrameAtTime(player.Time / 1000.0);
        settingFrameFromPlayer = false;
    }

    private void PauseOnShownFrame()
    {
        if (player is null) return;
        var seconds = player.Time / 1000.0;
        PlayerSeconds = seconds;
        player.SetPause(true);
        StopPlayback();
        if (!CanStep) return;
        // The live picture stays up until the exact frame of that moment has been decoded.
        settingFrameFromPlayer = true;
        CurrentFrame = FrameAtTime(seconds);
        settingFrameFromPlayer = false;
        _ = ShowFrameAsync(CurrentFrame);
    }

    private void StopPlayback()
    {
        playClock?.Stop();
        if (IsPlaying && player is not null) player.Stop();
        IsPlaying = false;
    }

    private void OnEndReached()
    {
        playClock?.Stop();
        IsPlaying = false;
        if (CanStep)
        {
            settingFrameFromPlayer = true;
            CurrentFrame = MaximumFrame;
            settingFrameFromPlayer = false;
        }
    }

    private void OnLivePicture(object? sender, EventArgs args)
    {
        if (!IsPlaying || bridge?.Bitmap is not { } live) return;
        Picture = live;
        PictureRevision++;
    }

    /// <summary>Last indexed frame whose time is at or before <paramref name="seconds"/>.</summary>
    private int FrameAtTime(double seconds)
    {
        if (frames.Count == 0) return 0;
        var low = 0;
        var high = frames.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (frames[middle].Time <= seconds) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    [RelayCommand]
    private void RevealSelected()
    {
        if (SelectedItem is not { } item) return;
        if (File.Exists(item.Asset.FullPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Asset.FullPath}\"") { UseShellExecute = true });
        else Open(Path.GetDirectoryName(item.Asset.FullPath) ?? "");
    }

    private static void Open(string folder)
    {
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    /// <summary>Photos are decoded at up to 4K wide; Avalonia cannot read every format, so null hands it to FFmpeg.</summary>
    private static Bitmap? DecodePhoto(MediaAsset asset)
    {
        try
        {
            using var stream = File.OpenRead(asset.FullPath);
            var full = new Bitmap(stream);
            if (full.PixelSize.Width <= 3840) return full;
            // Only very large pictures are decoded again at a size the screen can use; smaller ones are never scaled up.
            full.Dispose();
            stream.Position = 0;
            return Bitmap.DecodeToWidth(stream, 3840, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception) { return null; }
    }

    private static Bitmap Decode(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes);
        return new Bitmap(memory);
    }

    private static string Size(long bytes) => bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0.0} MB";
    private static string Join(params string[] parts) => string.Join("   ·   ", parts.Where(part => part.Length > 0));

    // ---------------------------------------------------------------- Settings

    private void SaveSettings()
    {
        if (disposed) return;
        settings = settings with
        {
            WorkspaceRoots = roots.Select(root => root.Path).ToArray(),
            ShowSources = IsSidebarOpen,
            SelectedFile = SelectedItem?.Asset.FullPath ?? ""
        };
        try { settingsStore.Save(settings); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        SaveSettings();
        disposed = true;
        lifetime.Cancel();
        playClock?.Stop();
        try { player?.Stop(); } catch (Exception) { }
        player?.Dispose();
        bridge?.Dispose();
        libVlc?.Dispose();
    }
}
