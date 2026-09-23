using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using MediaWorkbench.Core;
using AudioTrack = MediaWorkbench.Core.AudioTrack;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MediaWorkbench.App;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Frame-sequence exports above this count require a second click on the same button.</summary>
    public const int LargeExportThreshold = 500;
    public const int MaximumNotifications = 4;

    private readonly CatalogStore catalog;
    private readonly SettingsStore settingsStore;
    private readonly JobHistoryStore jobHistoryStore;
    private readonly string dataDirectory;
    private readonly LibVLC libVlc;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim exportGate = new(1);
    private CancellationTokenSource? scanCancellation;
    private CancellationTokenSource? selectionCancellation;
    private CancellationTokenSource? frameCancellation;
    private CancellationTokenSource? indexCancellation;
    private AppSettings settings;
    private MediaEngine engine;
    private MediaInfo? mediaInfo;
    private IReadOnlyList<VideoFrame> frames = [];
    private AssetViewModel? loadedAsset;
    private byte[]? displayedFrameBytes;
    private int inFlightFrame = -1;
    private long playbackStart;
    private long? stopPlaybackAt;
    private string? playerMediaPath;
    private bool verifyStartPending;
    private bool holdingVideoSurface;
    private int holdVersion;
    private string? pendingSelectionPath;
    private int armedExportCount = -1;
    private static readonly TimeSpan SlowDecodeDelay = TimeSpan.FromMilliseconds(350);
    private readonly FrameMemoryCache frameMemory = new();
    private readonly object prefetchLock = new();
    private CancellationTokenSource prefetchCancellation = new();
    private Task? prefetchTask;
    private int prefetchIndex;
    private int prefetchDirection = 1;
    private bool slowDecode;
    private bool disposed;

    public BatchCollection<AssetViewModel> Assets { get; } = [];
    public ICollectionView LibraryView { get; }
    public ObservableCollection<ExportJobViewModel> Jobs { get; } = [];
    public ObservableCollection<ExportJobRecord> JobHistory { get; } = [];
    public ObservableCollection<Notification> Notifications { get; } = [];
    public ObservableCollection<RecentEntry> RecentLibraries { get; } = [];
    public ObservableCollection<AudioTrack> AudioTracks { get; } = [];
    public ObservableCollection<string> AudioChannels { get; } = ["All channels"];
    public string[] MediaFilters { get; } = ["All media", "Photos", "Videos", "Audio"];
    public MediaPlayer Player { get; }
    public IReadOnlyList<VideoFrame> FrameList => frames;
    public bool CanExportFrame => PreviewImage is BitmapSource && !IsFrameLoading && !ShowPlayback && DisplayedFrame >= 0;
    public bool HasFrames => frames.Count > 0;
    public bool CanPlay => SelectedAsset is not null && SelectedAsset.Asset.Kind != MediaKind.Photo && mediaInfo is not null;
    public bool HasAudio => AudioTracks.Count > 0;
    public int MaximumFrame => Math.Max(0, frames.Count - 1);
    public bool IsPreviewBusy => IsFrameLoading || IsIndexing;
    /// <summary>True while the visible still is not the requested frame: a decode is pending or failed.</summary>
    public bool IsPreviewStale => IsVideo && PreviewImage is not null && (IsFrameLoading ? slowDecode : DisplayedFrame != CurrentFrame);
    public bool ShowPendingOverlay => IsPreviewStale && !ShowPlayback;
    /// <summary>The thin progress line under the preview; like the overlay it only appears for decodes that are actually slow.</summary>
    public bool ShowFrameProgress => IsFrameLoading && (slowDecode || !IsVideo);
    public string PendingLabel => !IsVideo ? "" :
        IsFrameLoading ? $"Decoding frame {CurrentFrame:N0}…" :
        DisplayedFrame != CurrentFrame ? $"Showing frame {DisplayedFrame:N0}. Frame {CurrentFrame:N0} could not be decoded." :
        IsIndexing && !HasFrames ? "Indexing frame timestamps for exact stepping…" : "";
    public string FrameLabel => !HasFrames ? (IsIndexing ? "Indexing frame timestamps…" : "Select a video for frame-accurate editing") :
        $"Frame {CurrentFrame:N0} / {MaximumFrame:N0}  ·  {frames[Math.Clamp(CurrentFrame, 0, MaximumFrame)].Time:0.000000}s  ·  zero-based";
    public string RangeSummary => !HasFrames ? "" : $"In {InFrame:N0} to out {OutFrame:N0}, inclusive: {Math.Max(0, OutFrame - InFrame + 1):N0} frames";
    /// <summary>Large tabular frame readout beside the timeline; the ordinal is zero-based like the export filenames.</summary>
    public string FrameNumberText => HasFrames ? CurrentFrame.ToString("N0") : "0";
    public string FrameTotalText => HasFrames ? $"of {MaximumFrame:N0}" : IsIndexing ? (ShowIndexing ? "" : "indexing frames") : IsVideo ? "no frame index" : "";
    public string FrameTimeText => HasFrames ? $"{frames[Math.Clamp(CurrentFrame, 0, MaximumFrame)].Time:0.000} s" : "";
    public string SelectionLabel => (PeekedAsset ?? SelectedAsset)?.Name ?? "Nothing selected";
    public string LibraryLabel => $"{Assets.Count:N0} items · {Assets.Count(asset => asset.IsFavorite):N0} favorites";
    public string RangeLabel => frames.Count == 0 ? "Video markers use inclusive frame indices." :
        $"Selection: {Math.Max(0, OutFrame - InFrame + 1):N0} frames. All: {frames.Count:N0}. PNG size varies; estimated all-frame output: {(displayedFrameBytes?.Length ?? 0) * (double)frames.Count / 1048576:N0} MB.";
    public string SelectedFramesLabel => !HasFrames ? "Export selected frames" : ExportLabelFor(Math.Max(0, OutFrame - InFrame + 1), "");
    public string AllFramesLabel => !HasFrames ? "Export all frames" : ExportLabelFor(frames.Count, "all ");
    public int ActiveJobCount => Jobs.Count(job => !job.IsFinished);
    public string ExportBadge => ActiveJobCount > 0 ? $"Export ({ActiveJobCount})" : "Export";
    public string JobHistoryLabel => $"Previous exports ({JobHistory.Count})";
    public bool ShowQueueHint => Jobs.Count == 0;
    public bool ShowEmptyState => PreviewImage is null && !ShowPlayback && !IsAudio && !ShowInstantLayer;

    [ObservableProperty] private string status = "Choose a media folder to get started.";
    [ObservableProperty] private string libraryRoot = "";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string mediaFilter = "All media";
    [ObservableProperty] private bool favoritesOnly;
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private AssetViewModel? selectedAsset;
    [ObservableProperty] private ImageSource? previewImage;
    [ObservableProperty] private bool showPlayback;
    [ObservableProperty] private bool isFrameLoading;
    [ObservableProperty] private bool isIndexing;
    [ObservableProperty] private int currentFrame;
    [ObservableProperty] private int displayedFrame = -1;
    [ObservableProperty] private int playbackFrame = -1;
    [ObservableProperty] private int inFrame;
    [ObservableProperty] private int outFrame;
    [ObservableProperty] private double audioStart;
    [ObservableProperty] private double audioEnd;
    [ObservableProperty] private AudioTrack? selectedAudioTrack;
    [ObservableProperty] private int selectedChannelIndex;
    [ObservableProperty] private string exportDirectory;
    [ObservableProperty] private string ffmpegDirectory;
    [ObservableProperty] private int cacheMegabytes;

    public MainViewModel(string dataDirectory)
    {
        this.dataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        catalog = new CatalogStore(Path.Combine(dataDirectory, "catalog.db"));
        settingsStore = new SettingsStore(Path.Combine(dataDirectory, "settings.json"));
        jobHistoryStore = new JobHistoryStore(Path.Combine(dataDirectory, "export-history.json"));
        try { settings = settingsStore.Load(); }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            settings = new AppSettings();
            ReportError(exception);
        }
        exportDirectory = settings.ExportDirectory;
        ffmpegDirectory = settings.FfmpegDirectory;
        cacheMegabytes = settings.CacheMegabytes;
        libraryRoot = settings.LastLibrary;
        engine = CreateEngine();
        LibraryView = CollectionViewSource.GetDefaultView(Assets);
        LibraryView.Filter = FilterAsset;
        foreach (var record in jobHistoryStore.Load()) JobHistory.Add(record);
        RefreshRecentLibraries();
        InitializeOrganization();
        LibVLCSharp.Shared.Core.Initialize();
        libVlc = new LibVLC("--no-video-title-show", "--quiet");
        Player = new MediaPlayer(libVlc);
        Player.Playing += OnPlaying;
        Player.EndReached += OnEndReached;
        Player.TimeChanged += OnTimeChanged;
    }

    public async Task InitializeAsync(bool scanLastLibrary = true)
    {
        try
        {
            await ToolPaths.Resolve(settings.FfmpegDirectory).CheckAsync(lifetime.Token);
            Status = "Ready · FFmpeg, FFprobe, SQLite and VLC available.";
            if (!settings.TourOffered)
            {
                settings = settings with { TourOffered = true };
                Guard(() => settingsStore.Save(WithBrowsingPreferences(settings)));
                Notify(NotificationKind.Info, "New here? The tour points at every panel and button and says what it does.", "Tour the UI", () => TourRequested?.Invoke(this, EventArgs.Empty), sticky: true);
            }
            if (scanLastLibrary && Directory.Exists(LibraryRoot))
                await OpenLibraryAsync(LibraryRoot);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    partial void OnSearchTextChanged(string value) => RefreshView();
    partial void OnMediaFilterChanged(string value) => RefreshView();
    partial void OnFavoritesOnlyChanged(bool value) => RefreshView();
    partial void OnSelectedAssetChanged(AssetViewModel? value)
    {
        // The centre shows the Library map while nothing is selected and the Preview once a file is chosen. Stitching stays
        // put: picking the next picture to add must not throw you out of the tab you are adding it to.
        if (!IsStitchTab)
            MainTab = value is null ? 0 : 1;
        BeginInstantPreview(value);
        _ = LoadSelectionAsync(value);
    }
    partial void OnCurrentFrameChanged(int value) { armedExportCount = -1; NotifyExportLabels(); NotifyFrameState(); SyncAudioPositionToFrame(); _ = SeekFrameAsync(); }
    partial void OnDisplayedFrameChanged(int value) => NotifyFrameState();
    partial void OnPreviewImageChanged(ImageSource? value)
    {
        if (value is not null) EndInstantPreview();
        NotifyFrameState();
        OnPropertyChanged(nameof(ShowInstantLayer));
        OnPropertyChanged(nameof(ShowEmptyState));
        NotifyFraming();
    }
    partial void OnIsFrameLoadingChanged(bool value) => NotifyFrameState();
    partial void OnIsIndexingChanged(bool value) { NotifyFrameState(); NotifyIndexing(); }
    partial void OnShowPlaybackChanged(bool value)
    {
        NotifyFrameState();
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowVideoSurface));
        OnPropertyChanged(nameof(ShowInstantLayer));
        if (value) { IsCropping = false; CropSelection = null; }
        else { PlaybackFrame = -1; SyncAudioPositionToFrame(); }
    }
    partial void OnInFrameChanged(int value) { armedExportCount = -1; NotifyExportLabels(); UpdateAudioRange(); }
    partial void OnOutFrameChanged(int value) { armedExportCount = -1; NotifyExportLabels(); UpdateAudioRange(); }
    partial void OnSelectedAudioTrackChanged(AudioTrack? value)
    {
        AudioChannels.Clear();
        AudioChannels.Add("All channels");
        for (var channel = 0; channel < (value?.Channels ?? 0); channel++)
            AudioChannels.Add($"Channel {channel + 1} only");
        SelectedChannelIndex = 0;
        _ = LoadWaveformAsync();
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var path = NativeDialogs.PickFolder("Choose a media library folder", LibraryRoot);
        if (path is not null)
            await OpenLibraryAsync(path);
    }

    [RelayCommand]
    private Task OpenRecentAsync(RecentEntry? entry) => entry is null ? Task.CompletedTask : OpenPathAsync(entry.Path);

    /// <summary>Opens a dropped or recent path: a folder becomes the library; a file opens its folder and selects the file.</summary>
    public async Task OpenPathAsync(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (Directory.Exists(path))
            {
                await OpenLibraryAsync(path);
                return;
            }
            if (!File.Exists(path))
                throw new DirectoryNotFoundException($"Not found: {path}");
            var directory = Path.GetDirectoryName(path)!;
            var existing = Assets.FirstOrDefault(item => string.Equals(item.Asset.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !IsScanning)
            {
                SelectedAsset = existing;
                return;
            }
            if (LibraryScanner.ReadFile(path) is null)
                throw new NotSupportedException($"{Path.GetFileName(path)} is not a supported media type.");
            pendingSelectionPath = path;
            await OpenLibraryAsync(directory);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (activeCollectionPath is not null)
        {
            try
            {
                var collection = collectionStore.Load(activeCollectionPath);
                await LoadVirtualAsync(collection.Paths, collection.Name);
            }
            catch (Exception exception) { ReportError(exception); }
        }
        else if (virtualSource) await BrowseTaggedAsync();
        else await OpenLibraryAsync(LibraryRoot);
    }

    [RelayCommand]
    private void CancelScan() => scanCancellation?.Cancel();

    public async Task OpenLibraryAsync(string root)
    {
        scanCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        scanCancellation = cancellation;
        IsScanning = true;
        SelectedAsset = null;
        Assets.Clear();
        virtualSource = false;
        activeCollectionPath = null;
        hasSource = true;
        ResetFolderTree();
        OnPropertyChanged(nameof(IsCollectionView));
        UpdateVisibleCount();
        NotifyEmptyState();
        try
        {
            LibraryRoot = Path.GetFullPath(root);
            SourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(LibraryRoot));
            OnPropertyChanged(nameof(SourceSummary));
            var favorites = catalog.GetFavorites(LibraryRoot);
            var scanRoot = LibraryRoot;
            Status = "Scanning folders…";
            await Task.Run(async () =>
            {
                var batch = new List<MediaAsset>(200);
                foreach (var asset in new LibraryScanner().Scan(scanRoot, cancellation.Token))
                {
                    batch.Add(asset);
                    if (batch.Count < 200)
                        continue;
                    await AddBatchAsync(batch.ToArray(), favorites, cancellation.Token);
                    batch.Clear();
                }
                if (batch.Count > 0)
                    await AddBatchAsync(batch.ToArray(), favorites, cancellation.Token);
            }, cancellation.Token);
            settings = settings.WithRecentLibrary(scanRoot);
            settingsStore.Save(settings);
            RefreshRecentLibraries();
            Status = $"Found {Assets.Count:N0} media files. Unsupported files report errors inline; originals are never changed.";
            if (pendingSelectionPath is { } missing)
            {
                pendingSelectionPath = null;
                Notify(NotificationKind.Info, $"{Path.GetFileName(missing)} was not found in the scanned folder.");
            }
        }
        catch (OperationCanceledException) { if (scanCancellation == cancellation) Status = "Scan cancelled. Already discovered items remain available."; }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            if (scanCancellation == cancellation) { IsScanning = false; scanCancellation = null; }
            OnPropertyChanged(nameof(LibraryLabel));
            UpdateVisibleCount();
            RebuildFolderTree();
            NotifyEmptyState();
        }
    }

    private async Task AddBatchAsync(MediaAsset[] batch, HashSet<string> favorites, CancellationToken cancellationToken)
    {
        catalog.Index(batch, cancellationToken);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assets.AddRange(batch.Select(asset => new AssetViewModel(asset, favorites.Contains(asset.RelativePath)) { Tags = tagIndex.GetValueOrDefault(asset.FullPath, []), FolderKey = FolderKeyOf(asset) }));
            AfterBatchAdded();
            Status = $"Found {Assets.Count:N0} media files…";
        }, DispatcherPriority.Background, cancellationToken);
    }

    /// <summary>Re-syncs the filmstrip highlight after a collection reset and honours a pending drop/recent file selection.</summary>
    private void AfterBatchAdded()
    {
        OnPropertyChanged(nameof(LibraryLabel));
        UpdateVisibleCount();
        RebuildFolderTreeThrottled();
        NotifyEmptyState();
        if (pendingSelectionPath is { } pending && Assets.FirstOrDefault(item => string.Equals(item.Asset.FullPath, pending, StringComparison.OrdinalIgnoreCase)) is { } match)
        {
            pendingSelectionPath = null;
            SelectedAsset = match;
        }
        else OnPropertyChanged(nameof(SelectedAsset));
    }

    private bool FilterAsset(object value)
    {
        if (value is not AssetViewModel item)
            return false;
        return IsFolderIncluded(item)
            && FolderTree.Contains(folderFilter, FolderKeyOf(item))
            && (!FavoritesOnly || item.IsFavorite)
            && (string.IsNullOrWhiteSpace(TagFilter) || item.Tags.Any(tag => tag.Contains(TagFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            && (string.IsNullOrWhiteSpace(SearchText) || item.Asset.RelativePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            && (MediaFilter == "All media" || MediaFilter == "Photos" && item.Asset.Kind == MediaKind.Photo
                || MediaFilter == "Videos" && item.Asset.Kind == MediaKind.Video || MediaFilter == "Audio" && item.Asset.Kind == MediaKind.Audio);
    }

    /// <summary>Whether an item passes the current filters. O(1); used instead of scanning the view.</summary>
    public bool IsVisible(AssetViewModel item) => FilterAsset(item);

    [RelayCommand]
    private void ToggleFavorite() => Guard(() =>
    {
        if (SelectedAsset is not { } item)
            return;
        catalog.SetFavorite(item.Asset, !item.IsFavorite);
        item.IsFavorite = !item.IsFavorite;
        Status = item.IsFavorite ? $"Favorited {item.Name}" : $"Removed favorite: {item.Name}";
        RefreshView();
        OnPropertyChanged(nameof(LibraryLabel));
    });

    [RelayCommand]
    private void ExportFavorites() => Guard(() =>
    {
        RequireLibrary();
        var path = NativeDialogs.SaveJson("Export favorites", settings.ExportDirectory, "favorites.json");
        if (path is null)
            return;
        File.WriteAllText(path, catalog.ExportFavorites(LibraryRoot));
        Status = "Favorites exported. Copy your media separately, then import after scanning its new location.";
        Notify(NotificationKind.Success, "Favorites exported.", "Open", () => RevealPath(path));
    });

    [RelayCommand]
    private void ImportFavorites() => Guard(() =>
    {
        RequireLibrary();
        if (IsScanning)
            throw new InvalidOperationException("Wait for the folder scan to finish before importing favorites.");
        var path = NativeDialogs.OpenJson("Import favorites", settings.ExportDirectory);
        if (path is null)
            return;
        var count = catalog.ImportFavorites(LibraryRoot, File.ReadAllText(path));
        var favorites = catalog.GetFavorites(LibraryRoot);
        foreach (var item in Assets)
            item.IsFavorite = favorites.Contains(item.Asset.RelativePath);
        RefreshView();
        OnPropertyChanged(nameof(LibraryLabel));
        Status = $"Matched {count:N0} favorites to this folder. Unmatched relative paths were skipped.";
    });

    private async Task LoadSelectionAsync(AssetViewModel? item)
    {
        selectionCancellation?.Cancel();
        frameCancellation?.Cancel();
        indexCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        selectionCancellation = cancellation;
        loadedAsset = item;
        ResetFrameMemory();
        armedExportCount = -1;
        stopPlaybackAt = null;
        StopWatchingRangeEnd();
        Player.Stop();
        playerMediaPath = null;
        pauseVersion++;
        refiningPause = false;
        pausedAtFrame = -1;
        ReleaseVideoSurface(force: true);
        ShowPlayback = false;
        PreviewImage = null;
        CropSelection = null;
        IsCropping = false;
        IsMetadataTagMode = false;
        MediaSummary = "";
        AspectHighlight = "";
        Metadata.Clear();
        SelectedTags.Clear();
        if (item is not null) foreach (var tag in item.Tags) SelectedTags.Add(tag);
        displayedFrameBytes = null;
        DisplayedFrame = -1;
        frames = [];
        mediaInfo = null;
        AudioTracks.Clear();
        SelectedAudioTrack = null;
        CurrentFrame = 0;
        InFrame = 0;
        OutFrame = 0;
        AudioStart = 0;
        AudioEnd = 0;
        ResetAudioView();
        ResetFraming();
        IsIndexing = false;
        IsFrameLoading = item is not null;
        NotifyMediaProperties();
        OnPropertyChanged(nameof(IsSelectionHidden));
        NotifyEmptyState();
        if (item is null)
        {
            if (selectionCancellation == cancellation) selectionCancellation = null;
            return;
        }
        try
        {
            Status = $"Opening {item.Name}…";
            await Task.Delay(70, cancellation.Token);
            var selectedEngine = engine;
            if (item.Asset.Kind == MediaKind.Photo)
            {
                PhotoPreview? photo = null;
                await photoDecodeGate.WaitAsync(cancellation.Token);
                try
                {
                    try { photo = await Task.Run(() => ImageLoader.Load(item.Asset.FullPath), cancellation.Token); }
                    catch (Exception exception) when (exception is IOException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException) { }
                }
                finally { photoDecodeGate.Release(); }
                cancellation.Token.ThrowIfCancellationRequested();
                if (photo is not null)
                {
                    PreviewImage = photo.Image;
                    DisplayedFrame = 0;
                    ShowMetadata(item.Asset, photo.Metadata);
                    Status = "Image ready. Drag a crop and copy pixels, or tag/stage this file. No video controls needed.";
                    return;
                }
            }
            var info = await selectedEngine.ProbeAsync(item.Asset.FullPath, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            mediaInfo = info;
            foreach (var track in info.AudioTracks)
                AudioTracks.Add(track);
            SelectedAudioTrack = AudioTracks.FirstOrDefault();
            AudioEnd = info.Duration;
            UpdateFrameRate();
            ShowMetadata(item.Asset, info.Metadata);
            NotifyMediaProperties();
            if (item.Asset.Kind == MediaKind.Video)
            {
                _ = IndexFramesAsync(item, info, selectedEngine);
                await SeekFrameAsync();
                cancellation.Token.ThrowIfCancellationRequested();
                if (PreviewImage is not null)
                    ShowMetadata(item.Asset, info.Metadata);
                Status = HasFrames ? "Ready. E exports the displayed frame; F toggles favorite." : "First frame ready. Indexing timestamps so stepping stays frame-exact…";
            }
            else if (item.Asset.Kind == MediaKind.Photo)
            {
                var bytes = await selectedEngine.GetFrameAsync(item.Asset, 0, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                SetDisplayedFrame(DecodeImage(bytes), bytes, 0);
                ShowMetadata(item.Asset, info.Metadata);
                Status = "Image decoded with FFmpeg. Drag a crop and copy pixels, or tag/stage this file.";
            }
            else Status = "Audio ready. Select a track, channel and time range to export.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (SelectedAsset == item) ReportError(exception); }
        finally
        {
            if (selectionCancellation == cancellation)
            {
                selectionCancellation = null;
                if (frameCancellation is null) IsFrameLoading = false;
                NotifyMediaProperties();
            }
        }
    }

    private async Task IndexFramesAsync(AssetViewModel item, MediaInfo info, MediaEngine selectedEngine)
    {
        indexCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        indexCancellation = cancellation;
        indexedFrames = 0;
        indexPulses = 0;
        indexEstimate = 0;
        IsIndexing = true;
        try
        {
            var header = FrameRateInfo.FromRatio(info.Metadata.GetValueOrDefault("avg_frame_rate")) ?? FrameRateInfo.FromRatio(info.Metadata.GetValueOrDefault("r_frame_rate"));
            indexEstimate = header is null ? 0 : (int)Math.Round(info.Duration * header.FramesPerSecond);
            // Progress<T> is created here on the UI thread, so reports from the FFprobe reader arrive on it.
            var progress = new Progress<int>(count => { if (indexCancellation == cancellation) SetIndexProgress(count, indexEstimate); });
            var indexed = await selectedEngine.IndexFramesAsync(item.Asset, info, cancellation.Token, progress);
            cancellation.Token.ThrowIfCancellationRequested();
            if (loadedAsset != item)
                return;
            frames = indexed;
            InFrame = 0;
            OutFrame = MaximumFrame;
            NotifyMediaProperties();
            UpdateAudioRange();
            UpdateFrameRate();
            SyncAudioPositionToFrame();
            if (CurrentFrame > MaximumFrame)
                CurrentFrame = MaximumFrame;
            else if (DisplayedFrame != CurrentFrame && frameCancellation is null)
                _ = SeekFrameAsync();
            if (!IsFrameLoading)
                Status = $"{frames.Count:N0} frames indexed. Step with the timeline, arrow keys or comma/period.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (loadedAsset == item) ReportError(exception); }
        finally
        {
            if (indexCancellation == cancellation) { indexCancellation = null; IsIndexing = false; NotifyMediaProperties(); }
        }
    }

    /// <summary>
    /// Shows the requested frame. Three speeds, fastest first: a frame already decoded in memory is shown at once with no loading state;
    /// a frame on disk is read and decoded off the UI thread; anything else is decoded by FFmpeg (a verified seek when the index is ready).
    /// The "decoding" overlay only appears when a decode is actually slow, so ordinary stepping never flashes a message.
    /// </summary>
    private async Task SeekFrameAsync()
    {
        if (loadedAsset is not { Asset.Kind: MediaKind.Video } item || mediaInfo is null || loadedAsset != SelectedAsset)
            return;
        var index = HasFrames ? Math.Clamp(CurrentFrame, 0, MaximumFrame) : Math.Max(0, CurrentFrame);
        if (frameCancellation is not null && inFlightFrame == index)
            return;
        if (frameCancellation is null && DisplayedFrame == index && PreviewImage is not null)
            return;
        frameCancellation?.Cancel();
        if (Player.IsPlaying)
            Player.Pause();
        if (ShowPlayback)
            HoldVideoSurface();
        ShowPlayback = false;
        if (frameMemory.TryGet(index, out var readyBytes, out var readyImage))
        {
            frameCancellation = null;
            inFlightFrame = -1;
            slowDecode = false;
            IsFrameLoading = false;
            SetDisplayedFrame(readyImage, readyBytes, index);
            Status = $"Frame {index:N0}";
            SchedulePrefetch(item, index);
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        frameCancellation = cancellation;
        inFlightFrame = index;
        slowDecode = false;
        IsFrameLoading = true;
        NotifyFrameState();
        _ = RevealSlowDecodeAsync(cancellation);
        try
        {
            var asset = item.Asset;
            var selectedEngine = engine;
            var generation = frameMemory.Generation;
            var bytes = await Task.Run(() => selectedEngine.TryGetCachedFrame(asset, index), cancellation.Token);
            if (bytes is null)
            {
                // Only real decodes are debounced, so holding an arrow key does not start a process per key repeat.
                await Task.Delay(60, cancellation.Token);
                bytes = HasFrames
                    ? await selectedEngine.GetFrameAsync(asset, index, frames, mediaInfo, cancellation.Token)
                    : await selectedEngine.GetFrameAsync(asset, index, cancellation.Token);
            }
            var image = await Task.Run(() => DecodeImage(bytes), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            frameMemory.Add(generation, index, bytes, image);
            SetDisplayedFrame(image, bytes, index);
            Status = $"Frame {index:N0} ready. Export saves this exact decoded image.";
            SchedulePrefetch(item, index);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (frameCancellation == cancellation) ReportError(exception); }
        finally
        {
            if (frameCancellation == cancellation)
            {
                frameCancellation = null;
                inFlightFrame = -1;
                slowDecode = false;
                IsFrameLoading = false;
                // Decoded or failed, the paused video picture has done its job.
                ReleaseVideoSurface();
                NotifyFrameState();
            }
        }
    }

    private async Task RevealSlowDecodeAsync(CancellationTokenSource request)
    {
        try { await Task.Delay(SlowDecodeDelay, request.Token); }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }
        if (frameCancellation != request) return;
        slowDecode = true;
        Status = $"Decoding exact frame {inFlightFrame:N0}…";
        NotifyFrameState();
    }

    /// <summary>
    /// Keeps the frames around the current one decoded in memory, mostly in the direction of travel. One background loop serves all
    /// requests and simply re-reads the latest target, so rapid stepping never cancels a window decode that is already under way.
    /// </summary>
    private void SchedulePrefetch(AssetViewModel item, int index)
    {
        if (!HasFrames || mediaInfo is null) return;
        lock (prefetchLock)
        {
            prefetchDirection = index == prefetchIndex ? prefetchDirection : index > prefetchIndex ? 1 : -1;
            prefetchIndex = index;
            if (prefetchTask is { IsCompleted: false }) return;
            var asset = item.Asset;
            var selectedEngine = engine;
            var list = frames;
            var info = mediaInfo;
            var generation = frameMemory.Generation;
            var token = prefetchCancellation.Token;
            var ahead = PreviewImage is BitmapSource shown ? Math.Clamp(FrameMemoryCache.CapacityFor(shown) / 2, 2, 12) : 6;
            prefetchTask = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested && generation == frameMemory.Generation)
                    {
                        int centre, direction;
                        lock (prefetchLock) { centre = prefetchIndex; direction = prefetchDirection == 0 ? 1 : prefetchDirection; }
                        var target = Enumerable.Range(1, ahead).Select(step => centre + direction * step)
                            .Concat(Enumerable.Range(1, 2).Select(step => centre - direction * step))
                            .Where(candidate => candidate >= 0 && candidate < list.Count)
                            .Cast<int?>().FirstOrDefault(candidate => !frameMemory.Contains(candidate!.Value));
                        if (target is not { } next) return;
                        var bytes = await selectedEngine.GetFrameAsync(asset, next, list, info, token);
                        var image = DecodeImage(bytes);
                        frameMemory.Add(generation, next, bytes, image);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }, token);
        }
    }

    private void ResetFrameMemory()
    {
        lock (prefetchLock)
        {
            prefetchCancellation.Cancel();
            prefetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            prefetchTask = null;
            prefetchIndex = 0;
            prefetchDirection = 1;
        }
        frameMemory.Clear();
    }

    private void SetDisplayedFrame(BitmapSource image, byte[] bytes, int index)
    {
        // Crops survive frame steps within the same video; only a crop that no longer fits is dropped.
        if (CropSelection is { } crop && (crop.X + crop.Width > image.PixelWidth || crop.Y + crop.Height > image.PixelHeight))
            CropSelection = null;
        PreviewImage = image;
        displayedFrameBytes = bytes;
        DisplayedFrame = index;
        if (index == CurrentFrame)
            ReleaseVideoSurface();
        OnPropertyChanged(nameof(CanExportFrame));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(RangeLabel));
    }

    public async Task LoadThumbnailAsync(AssetViewModel item, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try
        {
            var image = await thumbnails.LoadAsync(item.Asset, engine, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            item.Thumbnail = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    /// <param name="decodeWidth">When set, the picture is decoded no wider than this, which is much quicker for comparisons that only need a small copy.</param>
    public static BitmapImage DecodeImage(byte[] bytes, int decodeWidth = 0)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    [RelayCommand]
    private void PreviousFrame() => StepFrame(-1);

    [RelayCommand]
    private void NextFrame() => StepFrame(1);

    /// <summary>Moves by whole frames, for the mouse wheel over the preview and the timeline.</summary>
    public void StepFrames(int delta) => StepFrame(delta);

    private void StepFrame(int delta) => Guard(() =>
    {
        if (!HasFrames)
            return;
        PauseAtPlaybackPosition();
        CurrentFrame = Math.Clamp(CurrentFrame + delta, 0, MaximumFrame);
    });

    /// <param name="refine">
    /// True for the Pause button itself: after pausing, the picture the player stopped on is compared with the decoded frames around
    /// the estimated position, so the still that replaces it is that very frame. Other callers move to a frame of their own next.
    /// </param>
    /// <param name="settled">
    /// Given the paused frame: first the clock's guess, then again the matched frame when refining changes it. Markers use this so
    /// they land on the frame that was on screen rather than one or two frames off it.
    /// </param>
    private void PauseAtPlaybackPosition(bool refine = false, Action<int>? settled = null)
    {
        if (!ShowPlayback)
            return;
        // The player only reports its time about four times a second; between reports the clock is carried forward.
        var time = EstimatedPlayerTimeMs() / 1000.0;
        stopPlaybackAt = null;
        StopWatchingRangeEnd();
        Player.SetPause(true);
        // Keep the paused video picture up until the exact still of that moment is ready, instead of flashing the
        // still from before playback started and then jumping to the right one.
        HoldVideoSurface();
        ShowPlayback = false;
        if (HasFrames)
        {
            var frame = FrameAtTime(time);
            pausedAtFrame = frame;
            settled?.Invoke(frame);
            if (refine && loadedAsset is { } item)
                _ = RefinePausedFrameAsync(item, frame, settled);
            if (frame == CurrentFrame)
                _ = SeekFrameAsync();
            else
                CurrentFrame = frame;
            if (frameCancellation is null && DisplayedFrame == CurrentFrame)
                ReleaseVideoSurface();
        }
        else
        {
            AudioPosition = Math.Max(0, time);
            ReleaseVideoSurface();
        }
    }

    /// <summary>Last indexed frame whose timestamp is at or before <paramref name="seconds"/>; binary search over the sorted index.</summary>
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
    private void TogglePlayback() => Guard(() =>
    {
        if (!CanPlay || SelectedAsset is null)
            return;
        if (Player.IsPlaying)
        {
            PauseAtPlaybackPosition(refine: true);
            if (HasFrames)
                _ = SeekFrameAsync();
            return;
        }
        // Rounded up to the millisecond: rounding down would land just before the frame and show the previous one first.
        var startMs = HasFrames ? (long)Math.Ceiling(frames[Math.Clamp(CurrentFrame, 0, MaximumFrame)].Time * 1000)
            : (long)((mediaInfo is { } info && AudioPosition < info.Duration - 0.05 ? AudioPosition : 0) * 1000);
        // Still on the frame the player was paused on: carry on from exactly where it stopped, with no seek at all.
        StartPlayback(startMs, null, continueFromPause: HasFrames && pausedAtFrame >= 0 && CurrentFrame == pausedAtFrame);
        Status = "Playing. Pause or step to return to exact frame preview.";
    });

    [RelayCommand]
    private void PlaySelection() => Guard(() =>
    {
        if (!CanPlay || SelectedAsset is null || mediaInfo is null)
            return;
        if (!HasFrames)
        {
            var section = new TimeRange(AudioStart, AudioEnd);
            section.Validate(mediaInfo.Duration);
            StartPlayback((long)(section.Start * 1000), (long)(section.End * 1000));
            Status = $"Playing {AudioSelectionText}; playback pauses at the end of the selection.";
            return;
        }
        var range = new FrameRange(InFrame, OutFrame);
        range.Validate(frames.Count);
        var times = range.ToTimeRange(frames, mediaInfo.Duration);
        StartPlayback((long)(times.Start * 1000), (long)(times.End * 1000));
        Status = $"Playing frames {InFrame:N0} to {OutFrame:N0}; playback pauses at the out marker.";
    });

    private void StartPlayback(long startMs, long? stopMs, bool continueFromPause = false)
    {
        pauseVersion++;
        refiningPause = false;
        pausedAtFrame = -1;
        lastPlayerStamp = 0;
        frameCancellation?.Cancel();
        playbackStart = startMs;
        stopPlaybackAt = stopMs;
        if (stopMs is null) StopWatchingRangeEnd(); else WatchForRangeEnd();
        var path = SelectedAsset!.Asset.FullPath;
        ShowPlayback = true;
        ReleaseVideoSurface(force: true);
        // Resuming the file that is already open and paused: move to the place and carry on. Opening it again for every
        // Play made the picture go black, start from the beginning and then jump, which showed as flicker and stutter.
        if (playerMediaPath == path && Player.State == VLCState.Paused)
        {
            verifyStartPending = false;
            if (!continueFromPause)
            {
                // Moved since pausing: the player still holds the old picture, so keep showing the still until it has jumped.
                ConcealVideoSurfaceUntilSeekLands();
                Player.Time = startMs;
            }
            Player.SetPause(false);
            return;
        }
        using var media = new Media(libVlc, path, FromType.FromPath);
        // Start at the right moment from the first picture, rather than starting at zero and seeking once it plays.
        media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        playerMediaPath = path;
        verifyStartPending = startMs > 0;
        if (!Player.Play(media))
        {
            playerMediaPath = null;
            throw new InvalidOperationException("VLC could not play this media file.");
        }
    }

    private void OnPlaying(object? sender, EventArgs args) { }

    private void OnEndReached(object? sender, EventArgs args) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (disposed)
            return;
        stopPlaybackAt = null;
        StopWatchingRangeEnd();
        playerMediaPath = null;
        ReleaseVideoSurface(force: true);
        ShowPlayback = false;
        if (!HasFrames) AudioPosition = 0;
        Status = "Playback finished.";
    });

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs args)
    {
        var time = args.Time;
        lastPlayerTimeMs = time;
        lastPlayerStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (disposed || !ShowPlayback)
                return;
            RevealVideoSurface();
            if (verifyStartPending)
            {
                // A container that ignored the start option begins at zero: one corrective seek, only then.
                verifyStartPending = false;
                if (playbackStart - time > 1500)
                {
                    Player.Time = playbackStart;
                    return;
                }
            }
            AudioPosition = time / 1000.0;
            if (HasFrames)
                PlaybackFrame = FrameAtTime(time / 1000.0);
            if (stopPlaybackAt is { } stop && time >= stop)
                FinishAtOutMarker();
        });
    }

    /// <summary>Stops a marked range at its out marker and shows that frame. The range watcher usually gets here first; a time report is the fallback.</summary>
    private void FinishAtOutMarker()
    {
        if (stopPlaybackAt is null)
            return;
        stopPlaybackAt = null;
        PauseAtPlaybackPosition();
        if (HasFrames)
        {
            CurrentFrame = Math.Clamp(OutFrame, 0, MaximumFrame);
            _ = SeekFrameAsync();
        }
        else AudioPosition = AudioStart;
        Status = HasFrames ? "Reached the out marker." : "Reached the end of the selection.";
    }

    [RelayCommand]
    private void MarkIn()
    {
        if (!HasFrames && IsAudio && mediaInfo is not null)
        {
            // Audio has no frames: I and O mark at the playhead, even while it plays.
            AudioStart = Math.Clamp(AudioPosition, 0, mediaInfo.Duration);
            if (AudioEnd <= AudioStart) AudioEnd = mediaInfo.Duration;
            return;
        }
        MarkAtPausedFrame(frame => { InFrame = frame; if (OutFrame < InFrame) OutFrame = InFrame; });
    }

    [RelayCommand]
    private void MarkOut()
    {
        if (!HasFrames && IsAudio && mediaInfo is not null)
        {
            AudioEnd = Math.Clamp(AudioPosition, 0, mediaInfo.Duration);
            if (AudioEnd <= AudioStart) AudioStart = 0;
            return;
        }
        MarkAtPausedFrame(frame => { OutFrame = frame; if (InFrame > OutFrame) InFrame = OutFrame; });
    }

    /// <summary>
    /// Puts a marker on the frame that was on screen. While the video plays, the clock only gives a frame or two of accuracy,
    /// so the marker is set from that guess at once and then corrected when the paused picture has been matched.
    /// </summary>
    private void MarkAtPausedFrame(Action<int> mark)
    {
        if (ShowPlayback)
            PauseAtPlaybackPosition(refine: true, settled: mark);
        else
            mark(Math.Clamp(CurrentFrame, 0, MaximumFrame));
    }

    private void SyncAudioPositionToFrame()
    {
        if (HasFrames && !ShowPlayback)
            AudioPosition = frames[Math.Clamp(CurrentFrame, 0, MaximumFrame)].Time;
    }

    private void UpdateAudioRange()
    {
        OnPropertyChanged(nameof(RangeLabel));
        OnPropertyChanged(nameof(RangeSummary));
        if (!HasFrames || mediaInfo is null)
            return;
        try
        {
            var range = new FrameRange(InFrame, OutFrame).ToTimeRange(frames, mediaInfo.Duration);
            AudioStart = range.Start;
            AudioEnd = range.End;
        }
        catch (ArgumentException) { }
    }

    [RelayCommand]
    private void ExportFrame() => Guard(() =>
    {
        if (!CanExportFrame || SelectedAsset is null || PreviewImage is not BitmapSource image)
            throw new InvalidOperationException("Wait for an exact frame preview, then export.");
        var asset = SelectedAsset.Asset;
        var index = DisplayedFrame;
        var bytes = displayedFrameBytes;
        var directory = settings.ExportDirectory;
        QueueExport($"Frame {index} from {asset.Name}", async (_, token) =>
        {
            var encoded = bytes ?? await Task.Run(() => ImageLoader.Encode(image), token);
            return await MediaEngine.SaveFrameAsync(asset, index, encoded, directory, token);
        });
    });

    [RelayCommand]
    private void ExportAllFrames() => ExportFrameRange(true);

    [RelayCommand]
    private void ExportSelectedFrames() => ExportFrameRange(false);

    private void ExportFrameRange(bool all) => Guard(() =>
    {
        var asset = SelectedAsset?.Asset ?? throw new InvalidOperationException("Select a video first.");
        var count = frames.Count;
        if (count == 0) throw new InvalidOperationException("Wait for the frame index to finish.");
        var range = all ? new FrameRange(0, count - 1) : new FrameRange(InFrame, OutFrame);
        range.Validate(count);
        if (range.Count > LargeExportThreshold && armedExportCount != range.Count)
        {
            armedExportCount = range.Count;
            NotifyExportLabels();
            Notify(NotificationKind.Info, $"This writes {range.Count:N0} PNG files to {settings.ExportDirectory}. Click the same button again to confirm.");
            Status = $"Confirm the {range.Count:N0}-frame export by clicking again.";
            return;
        }
        armedExportCount = -1;
        NotifyExportLabels();
        var selectedEngine = engine;
        var directory = settings.ExportDirectory;
        QueueExport($"{range.Count:N0} frames from {asset.Name}", (progress, token) => selectedEngine.ExportFramesAsync(asset, range, count, directory, progress, token));
    });

    [RelayCommand]
    private void TrimVideo() => Guard(() =>
    {
        var asset = SelectedAsset?.Asset ?? throw new InvalidOperationException("Select a video first.");
        var info = mediaInfo ?? throw new InvalidOperationException("Wait for the media to load.");
        if (!HasFrames) throw new InvalidOperationException("Wait for the frame index to finish.");
        var range = new FrameRange(InFrame, OutFrame);
        range.Validate(frames.Count);
        var selectedFrames = frames;
        var selectedEngine = engine;
        var directory = settings.ExportDirectory;
        var transform = HasVideoTransform ? CurrentTransform : null;
        var (width, height) = FrameSize;
        QueueExport(transform is null ? $"Trim of {asset.Name}" : $"Cropped/rotated trim of {asset.Name}", (progress, token) => selectedEngine.TrimVideoAsync(asset, range, selectedFrames, info, directory, progress, token, transform, width, height));
    });

    [RelayCommand]
    private void ExportAudio() => Guard(() =>
    {
        var asset = SelectedAsset?.Asset ?? throw new InvalidOperationException("Select media first.");
        var info = mediaInfo ?? throw new InvalidOperationException("Wait for the media to load.");
        var track = SelectedAudioTrack ?? throw new InvalidOperationException("Select an audio track.");
        var range = new TimeRange(AudioStart, AudioEnd);
        range.Validate(info.Duration);
        int? channel = SelectedChannelIndex > 0 ? SelectedChannelIndex - 1 : null;
        var selectedEngine = engine;
        var directory = settings.ExportDirectory;
        QueueExport($"Audio from {asset.Name}", (progress, token) => selectedEngine.ExportAudioAsync(asset, info, track, channel, range, directory, progress, token));
    });

    private void QueueExport(string title, Func<IProgress<ExportProgress>, CancellationToken, Task<string>> action)
    {
        if (ActiveJobCount >= 20)
            throw new InvalidOperationException("The queue is full. Wait for a job to finish or cancel one.");
        var job = new ExportJobViewModel(title, lifetime.Token);
        Jobs.Insert(0, job);
        NotifyJobState();
        Status = $"Queued: {title}. Output goes to {settings.ExportDirectory}.";
        _ = RunExportAsync(job, action);
    }

    private async Task RunExportAsync(ExportJobViewModel job, Func<IProgress<ExportProgress>, CancellationToken, Task<string>> action)
    {
        var acquired = false;
        var recordStatus = "Failed";
        try
        {
            await exportGate.WaitAsync(job.Cancellation.Token);
            acquired = true;
            job.Status = "Running";
            var progress = new Progress<ExportProgress>(value =>
            {
                if (!job.IsFinished) { job.Progress = value.Fraction * 100; job.Status = value.Message; }
            });
            job.OutputPath = await action(progress, job.Cancellation.Token);
            job.Progress = 100;
            job.Status = "Complete";
            recordStatus = "Complete";
            Status = $"Saved: {job.OutputPath}";
            var output = job.OutputPath;
            Notify(NotificationKind.Success, $"Saved {Path.GetFileName(output)}", "Open output", () => RevealPath(output));
        }
        catch (OperationCanceledException)
        {
            job.Status = "Cancelled; frame sequences may retain partial output.";
            recordStatus = "Cancelled";
        }
        catch (Exception exception)
        {
            job.Status = "Failed: " + exception.Message;
            recordStatus = job.Status;
            ReportError(exception);
        }
        finally
        {
            job.IsFinished = true;
            if (acquired)
                exportGate.Release();
            RecordJob(job, recordStatus);
            NotifyJobState();
        }
    }

    private void RecordJob(ExportJobViewModel job, string status)
    {
        JobHistory.Insert(0, new ExportJobRecord(job.Title, status, job.OutputPath, DateTimeOffset.Now));
        while (JobHistory.Count > JobHistoryStore.Capacity)
            JobHistory.RemoveAt(JobHistory.Count - 1);
        Guard(() => jobHistoryStore.Save(JobHistory));
        OnPropertyChanged(nameof(JobHistoryLabel));
    }

    private void NotifyJobState()
    {
        OnPropertyChanged(nameof(ActiveJobCount));
        OnPropertyChanged(nameof(ExportBadge));
        OnPropertyChanged(nameof(ShowQueueHint));
    }

    [RelayCommand]
    private void CancelJob(ExportJobViewModel? job) { if (job is { IsFinished: false }) job.Cancellation.Cancel(); }

    [RelayCommand]
    private void OpenJobOutput(ExportJobViewModel? job) => Guard(() => { if (job?.OutputPath is { } path) RevealPath(path); });

    [RelayCommand]
    private void OpenHistoryOutput(ExportJobRecord? record) => Guard(() => { if (record?.OutputPath is { } path) RevealPath(path); });

    [RelayCommand]
    private void BrowseExportDirectory()
    {
        var path = NativeDialogs.PickFolder("Default export destination", ExportDirectory);
        if (path is not null)
            ExportDirectory = path;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            await ToolPaths.Resolve(FfmpegDirectory).CheckAsync(lifetime.Token);
            var updated = WithBrowsingPreferences(settings with { ExportDirectory = ExportDirectory.Trim(), FfmpegDirectory = FfmpegDirectory.Trim(), CacheMegabytes = CacheMegabytes });
            settingsStore.Save(updated);
            settings = updated;
            engine = CreateEngine();
            OnPropertyChanged(nameof(ExportDestination));
            Status = "Settings saved. Future exports use this destination without a save dialog.";
        }
        catch (Exception exception) { ReportError(exception); }
    }

    public string ExportDestination => settings.ExportDirectory;

    [RelayCommand]
    private void RevealFile() => Guard(() =>
    {
        if (SelectedAsset is null)
            return;
        RevealPath(SelectedAsset.Asset.FullPath);
    });

    [RelayCommand]
    private void OpenExports() => Guard(() =>
    {
        Directory.CreateDirectory(settings.ExportDirectory);
        Process.Start(new ProcessStartInfo(settings.ExportDirectory) { UseShellExecute = true });
    });

    private static void RevealPath(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            throw new FileNotFoundException("The output no longer exists.", path);
    }

    public void Notify(NotificationKind kind, string message, string? actionLabel = null, Action? action = null, bool sticky = false)
    {
        var notification = new Notification(kind, message, actionLabel, action);
        Notifications.Insert(0, notification);
        while (Notifications.Count > MaximumNotifications)
            Notifications.RemoveAt(Notifications.Count - 1);
        if (kind != NotificationKind.Error && !sticky)
            _ = DismissLaterAsync(notification);
    }

    private async Task DismissLaterAsync(Notification notification)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(7), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        Notifications.Remove(notification);
    }

    [RelayCommand]
    private void DismissNotification(Notification? notification) { if (notification is not null) Notifications.Remove(notification); }

    [RelayCommand]
    private void RunNotificationAction(Notification? notification) => Guard(() =>
    {
        if (notification is null) return;
        notification.Action?.Invoke();
        Notifications.Remove(notification);
    });

    private void RefreshRecentLibraries()
    {
        RecentLibraries.Clear();
        foreach (var path in settings.RecentLibraries)
            RecentLibraries.Add(new RecentEntry(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path, path));
    }

    private MediaEngine CreateEngine() => new(ToolPaths.Resolve(settings.FfmpegDirectory), Path.Combine(dataDirectory, "cache"), settings.CacheMegabytes);

    private void RequireLibrary()
    {
        if (virtualSource) throw new InvalidOperationException("Favorites transfer applies to a folder. Reopen the original media root first.");
        if (!Directory.Exists(LibraryRoot))
            throw new InvalidOperationException("Open a media folder first.");
    }

    private string ExportLabelFor(int count, string prefix) => armedExportCount == count ? $"Confirm {count:N0} PNGs" : $"Export {prefix}{count:N0} PNGs";

    private void NotifyExportLabels()
    {
        OnPropertyChanged(nameof(SelectedFramesLabel));
        OnPropertyChanged(nameof(AllFramesLabel));
        OnPropertyChanged(nameof(RangeLabel));
        OnPropertyChanged(nameof(RangeSummary));
    }

    private void NotifyFrameState()
    {
        foreach (var property in new[] { nameof(IsPreviewStale), nameof(ShowPendingOverlay), nameof(ShowFrameProgress), nameof(PendingLabel), nameof(FrameLabel), nameof(FrameNumberText), nameof(FrameTotalText), nameof(FrameTimeText), nameof(IsPreviewBusy), nameof(CanExportFrame), nameof(CanCopy) })
            OnPropertyChanged(property);
    }

    private void NotifyMediaProperties()
    {
        foreach (var property in new[] { nameof(HasSelection), nameof(IsPhoto), nameof(IsVideo), nameof(IsAudio), nameof(IsVisual), nameof(CanCopy), nameof(CopyLabel), nameof(ExportLabel), nameof(SelectedKindLabel) })
            OnPropertyChanged(property);
        foreach (var property in new[] { nameof(CanExportFrame), nameof(HasFrames), nameof(CanPlay), nameof(HasAudio), nameof(MaximumFrame), nameof(FrameLabel), nameof(FrameNumberText), nameof(FrameTotalText), nameof(FrameTimeText), nameof(SelectionLabel), nameof(RangeLabel), nameof(RangeSummary), nameof(CurrentFrame), nameof(FrameList), nameof(SelectedFramesLabel), nameof(AllFramesLabel), nameof(IsPreviewBusy), nameof(ShowPendingOverlay), nameof(PendingLabel), nameof(IsPreviewStale), nameof(ShowEmptyState), nameof(EmptyTitle), nameof(EmptyText) })
            OnPropertyChanged(property);
        NotifyAudioState();
        NotifyFrameRate();
        NotifyInstant();
        NotifyFraming();
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private void ReportError(Exception exception)
    {
        var message = exception.Message.Length <= 600 ? exception.Message : exception.Message[..600] + " (see app.log)";
        Status = message;
        Notify(NotificationKind.Error, message);
        try { File.AppendAllText(Path.Combine(dataDirectory, "app.log"), $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Guard(() => settingsStore.Save(WithBrowsingPreferences(settings)));
        foreach (var job in Jobs.Where(job => !job.IsFinished).ToArray())
        {
            job.IsFinished = true;
            job.Status = "Interrupted by shutdown";
            JobHistory.Insert(0, new ExportJobRecord(job.Title, "Interrupted", job.OutputPath, DateTimeOffset.Now));
        }
        Guard(() => jobHistoryStore.Save(JobHistory));
        prefetchCancellation.Cancel();
        lifetime.Cancel();
        Player.Playing -= OnPlaying;
        Player.EndReached -= OnEndReached;
        Player.TimeChanged -= OnTimeChanged;
        Player.Stop();
        Player.Dispose();
        libVlc.Dispose();
    }
}
