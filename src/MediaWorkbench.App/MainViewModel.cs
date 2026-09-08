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
    private readonly CatalogStore catalog;
    private readonly SettingsStore settingsStore;
    private readonly string dataDirectory;
    private readonly LibVLC libVlc;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim exportGate = new(1);
    private CancellationTokenSource? scanCancellation;
    private CancellationTokenSource? selectionCancellation;
    private CancellationTokenSource? frameCancellation;
    private AppSettings settings;
    private MediaEngine engine;
    private MediaInfo? mediaInfo;
    private IReadOnlyList<VideoFrame> frames = [];
    private byte[]? displayedFrame;
    private int displayedFrameIndex = -1;
    private long playbackStart;
    private bool disposed;

    public BatchCollection<AssetViewModel> Assets { get; } = [];
    public ICollectionView LibraryView { get; }
    public ObservableCollection<ExportJobViewModel> Jobs { get; } = [];
    public ObservableCollection<AudioTrack> AudioTracks { get; } = [];
    public ObservableCollection<string> AudioChannels { get; } = ["All channels"];
    public string[] MediaFilters { get; } = ["All media", "Photos", "Videos", "Audio"];
    public MediaPlayer Player { get; }
    public bool CanExportFrame => PreviewImage is BitmapSource && !IsFrameLoading && !ShowPlayback;
    public bool HasFrames => frames.Count > 0;
    public bool CanPlay => SelectedAsset is not null && SelectedAsset.Asset.Kind != MediaKind.Photo && mediaInfo is not null;
    public bool HasAudio => AudioTracks.Count > 0;
    public int MaximumFrame => Math.Max(0, frames.Count - 1);
    public string FrameLabel => frames.Count == 0 ? "Select a video for frame-accurate editing" :
        $"Frame {CurrentFrame:N0} / {MaximumFrame:N0}  \u00B7  {frames[Math.Clamp(CurrentFrame, 0, MaximumFrame)].Time:0.000000}s  \u00B7  zero-based";
    public string SelectionLabel => SelectedAsset?.Name ?? "Your media, within reach.";
    public string LibraryLabel => $"{Assets.Count:N0} items \u00B7 {Assets.Count(asset => asset.IsFavorite):N0} favorites";
    public string RangeLabel => frames.Count == 0 ? "Video markers use inclusive frame indices." :
        $"Selection: {Math.Max(0, OutFrame - InFrame + 1):N0} frames. All: {frames.Count:N0}. PNG size varies; estimated all-frame output: {(displayedFrame?.Length ?? 0) * (double)frames.Count / 1048576:N0} MB.";

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
    [ObservableProperty] private int currentFrame;
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
        InitializeOrganization();
        LibVLCSharp.Shared.Core.Initialize();
        libVlc = new LibVLC("--no-video-title-show", "--quiet");
        Player = new MediaPlayer(libVlc);
        Player.Playing += OnPlaying;
        Player.EndReached += OnEndReached;
    }

    public async Task InitializeAsync(bool scanLastLibrary = true)
    {
        try
        {
            await ToolPaths.Resolve(settings.FfmpegDirectory).CheckAsync(lifetime.Token);
            Status = "Ready \u00B7 FFmpeg, FFprobe, SQLite and VLC available.";
            if (scanLastLibrary && Directory.Exists(LibraryRoot))
                await OpenLibraryAsync(LibraryRoot);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    partial void OnSearchTextChanged(string value) => RefreshView();
    partial void OnMediaFilterChanged(string value) => RefreshView();
    partial void OnFavoritesOnlyChanged(bool value) => RefreshView();
    partial void OnSelectedAssetChanged(AssetViewModel? value) => _ = LoadSelectionAsync(value);
    partial void OnCurrentFrameChanged(int value) => _ = SeekFrameAsync();
    partial void OnIsFrameLoadingChanged(bool value) { OnPropertyChanged(nameof(CanExportFrame)); OnPropertyChanged(nameof(CanCopy)); }
    partial void OnShowPlaybackChanged(bool value) { OnPropertyChanged(nameof(CanExportFrame)); OnPropertyChanged(nameof(CanCopy)); if (value) { IsCropping = false; CropSelection = null; } }
    partial void OnInFrameChanged(int value) { OnPropertyChanged(nameof(RangeLabel)); UpdateAudioRange(); }
    partial void OnOutFrameChanged(int value) { OnPropertyChanged(nameof(RangeLabel)); UpdateAudioRange(); }
    partial void OnSelectedAudioTrackChanged(AudioTrack? value)
    {
        AudioChannels.Clear();
        AudioChannels.Add("All channels");
        for (var channel = 0; channel < (value?.Channels ?? 0); channel++)
            AudioChannels.Add($"Channel {channel + 1} only");
        SelectedChannelIndex = 0;
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var path = PathPickerWindow.Select(PathPickerMode.Folder, "Choose a media library folder", LibraryRoot);
        if (path is not null)
            await OpenLibraryAsync(path);
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
        OnPropertyChanged(nameof(IsCollectionView));
        try
        {
            LibraryRoot = Path.GetFullPath(root);
            SourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(LibraryRoot));
            OnPropertyChanged(nameof(SourceSummary));
            var favorites = catalog.GetFavorites(LibraryRoot);
            var scanRoot = LibraryRoot;
            Status = "Scanning folders\u2026";
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
            settings = settings with { LastLibrary = scanRoot };
            settingsStore.Save(settings);
            Status = $"Found {Assets.Count:N0} media files. Unsupported files report errors inline; originals are never changed.";
        }
        catch (OperationCanceledException) { if (scanCancellation == cancellation) Status = "Scan cancelled. Already discovered items remain available."; }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            if (scanCancellation == cancellation) { IsScanning = false; scanCancellation = null; }
            OnPropertyChanged(nameof(LibraryLabel));
            OnPropertyChanged(nameof(VisibleCount));
        }
    }

    private async Task AddBatchAsync(MediaAsset[] batch, HashSet<string> favorites, CancellationToken cancellationToken)
    {
        catalog.Index(batch, cancellationToken);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = SelectedAsset;
            Assets.AddRange(batch.Select(asset => new AssetViewModel(asset, favorites.Contains(asset.RelativePath)) { Tags = tagIndex.GetValueOrDefault(asset.FullPath, []) }));
            if (selected is not null && LibraryView.Contains(selected)) SelectedAsset = selected;
            Status = $"Found {Assets.Count:N0} media files\u2026";
            OnPropertyChanged(nameof(LibraryLabel));
            OnPropertyChanged(nameof(VisibleCount));
        }, DispatcherPriority.Background, cancellationToken);
    }

    private bool FilterAsset(object value)
    {
        if (value is not AssetViewModel item)
            return false;
        return (!FavoritesOnly || item.IsFavorite)
            && (string.IsNullOrWhiteSpace(TagFilter) || item.Tags.Any(tag => tag.Contains(TagFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            && (string.IsNullOrWhiteSpace(SearchText) || item.Asset.RelativePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            && (MediaFilter == "All media" || MediaFilter == "Photos" && item.Asset.Kind == MediaKind.Photo
                || MediaFilter == "Videos" && item.Asset.Kind == MediaKind.Video || MediaFilter == "Audio" && item.Asset.Kind == MediaKind.Audio);
    }

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
        var path = PathPickerWindow.Select(PathPickerMode.SaveFavorites, "Export favorites", settings.ExportDirectory);
        if (path is null)
            return;
        File.WriteAllText(path, catalog.ExportFavorites(LibraryRoot));
        Status = "Favorites exported. Copy your media separately, then import after scanning its new location.";
    });

    [RelayCommand]
    private void ImportFavorites() => Guard(() =>
    {
        RequireLibrary();
        if (IsScanning)
            throw new InvalidOperationException("Wait for the folder scan to finish before importing favorites.");
        var path = PathPickerWindow.Select(PathPickerMode.OpenFavorites, "Import favorites", settings.ExportDirectory);
        if (path is null)
            return;
        var count = catalog.ImportFavorites(LibraryRoot, File.ReadAllText(path));
        var favorites = catalog.GetFavorites(LibraryRoot);
        foreach (var item in Assets)
            item.IsFavorite = favorites.Contains(item.Asset.RelativePath);
        LibraryView.Refresh();
        OnPropertyChanged(nameof(LibraryLabel));
        Status = $"Matched {count:N0} favorites to this folder. Unmatched relative paths were skipped.";
    });

    private async Task LoadSelectionAsync(AssetViewModel? item)
    {
        selectionCancellation?.Cancel();
        frameCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        selectionCancellation = cancellation;
        Player.Stop();
        ShowPlayback = false;
        PreviewImage = null;
        CropSelection = null;
        IsCropping = false;
        MediaSummary = "";
        Metadata.Clear();
        SelectedTags.Clear();
        if (item is not null) foreach (var tag in item.Tags) SelectedTags.Add(tag);
        displayedFrame = null;
        displayedFrameIndex = -1;
        frames = [];
        mediaInfo = null;
        AudioTracks.Clear();
        SelectedAudioTrack = null;
        CurrentFrame = 0;
        InFrame = 0;
        OutFrame = 0;
        AudioStart = 0;
        AudioEnd = 0;
        IsFrameLoading = item is not null;
        NotifyMediaProperties();
        if (item is null)
        {
            if (selectionCancellation == cancellation) selectionCancellation = null;
            return;
        }
        try
        {
            Status = $"Opening {item.Name}\u2026";
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
                    displayedFrameIndex = 0;
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
            NotifyMediaProperties();
            if (item.Asset.Kind == MediaKind.Video)
            {
                Status = "Indexing decoded frames and timestamps; long videos can take time\u2026";
                var indexedFrames = await selectedEngine.IndexFramesAsync(item.Asset.FullPath, info, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                frames = indexedFrames;
                OutFrame = MaximumFrame;
            }
            if (item.Asset.Kind != MediaKind.Audio)
            {
                var bytes = await selectedEngine.GetFrameAsync(item.Asset, 0, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                SetDisplayedFrame(bytes, 0);
            }
            ShowMetadata(item.Asset, info.Metadata);
            Status = item.Asset.Kind == MediaKind.Audio ? "Audio ready. Select a track, channel and time range to export." : "Ready. E exports the displayed frame; F toggles favorite.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (SelectedAsset == item) ReportError(exception); }
        finally
        {
            if (selectionCancellation == cancellation)
            {
                selectionCancellation = null;
                IsFrameLoading = false;
                NotifyMediaProperties();
            }
        }
    }

    private async Task SeekFrameAsync()
    {
        if (SelectedAsset is not { } item || frames.Count == 0 || selectionCancellation is not null)
            return;
        frameCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        frameCancellation = cancellation;
        IsFrameLoading = true;
        PreviewImage = null;
        CropSelection = null;
        displayedFrame = null;
        displayedFrameIndex = -1;
        ShowPlayback = false;
        if (Player.IsPlaying)
            Player.Pause();
        try
        {
            var index = Math.Clamp(CurrentFrame, 0, MaximumFrame);
            OnPropertyChanged(nameof(FrameLabel));
            Status = $"Decoding exact frame {index:N0}\u2026";
            await Task.Delay(70, cancellation.Token);
            var bytes = await engine.GetFrameAsync(item.Asset, index, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SetDisplayedFrame(bytes, index);
            Status = $"Frame {index:N0} ready. Export saves this exact decoded image.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            if (frameCancellation == cancellation) { frameCancellation = null; IsFrameLoading = false; }
        }
    }

    private void SetDisplayedFrame(byte[] bytes, int index)
    {
        CropSelection = null;
        PreviewImage = DecodeImage(bytes);
        displayedFrame = bytes;
        displayedFrameIndex = index;
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

    public static BitmapImage DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    [RelayCommand]
    private void PreviousFrame() => StepFrame(-1);

    [RelayCommand]
    private void NextFrame() => StepFrame(1);

    private void StepFrame(int delta) => Guard(() =>
    {
        if (!HasFrames)
            return;
        PauseAtPlaybackPosition();
        CurrentFrame = Math.Clamp(CurrentFrame + delta, 0, MaximumFrame);
    });

    private void PauseAtPlaybackPosition()
    {
        if (!ShowPlayback)
            return;
        var time = Player.Time / 1000.0;
        Player.SetPause(true);
        ShowPlayback = false;
        if (HasFrames)
            CurrentFrame = frames.LastOrDefault(frame => frame.Time <= time)?.Index ?? 0;
    }

    [RelayCommand]
    private void TogglePlayback() => Guard(() =>
    {
        if (!CanPlay || SelectedAsset is null)
            return;
        if (Player.IsPlaying)
        {
            PauseAtPlaybackPosition();
            if (HasFrames)
                _ = SeekFrameAsync();
            return;
        }
        frameCancellation?.Cancel();
        playbackStart = HasFrames ? (long)(frames[CurrentFrame].Time * 1000) : (long)(AudioStart * 1000);
        using var media = new Media(libVlc, SelectedAsset.Asset.FullPath, FromType.FromPath);
        ShowPlayback = true;
        if (!Player.Play(media))
            throw new InvalidOperationException("VLC could not play this media file.");
        Status = "Playing. Pause or step to return to exact frame preview.";
    });

    private void OnPlaying(object? sender, EventArgs args) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (!disposed && ShowPlayback)
            Player.Time = playbackStart;
    });

    private void OnEndReached(object? sender, EventArgs args) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (disposed)
            return;
        ShowPlayback = false;
        Status = "Playback finished.";
    });

    [RelayCommand]
    private void MarkIn() { PauseAtPlaybackPosition(); InFrame = CurrentFrame; }

    [RelayCommand]
    private void MarkOut() { PauseAtPlaybackPosition(); OutFrame = CurrentFrame; }

    private void UpdateAudioRange()
    {
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
        var index = displayedFrameIndex;
        var bytes = displayedFrame;
        var directory = settings.ExportDirectory;
        QueueExport($"Frame {index} \u00B7 {asset.Name}", async (_, token) =>
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
        var range = all ? new FrameRange(0, count - 1) : new FrameRange(InFrame, OutFrame);
        range.Validate(count);
        var selectedEngine = engine;
        var directory = settings.ExportDirectory;
        QueueExport($"{range.Count:N0} frames \u00B7 {asset.Name}", (progress, token) => selectedEngine.ExportFramesAsync(asset, range, count, directory, progress, token));
    });

    [RelayCommand]
    private void TrimVideo() => Guard(() =>
    {
        var asset = SelectedAsset?.Asset ?? throw new InvalidOperationException("Select a video first.");
        var info = mediaInfo ?? throw new InvalidOperationException("Wait for the media to load.");
        var range = new FrameRange(InFrame, OutFrame);
        range.Validate(frames.Count);
        var selectedFrames = frames;
        var selectedEngine = engine;
        var directory = settings.ExportDirectory;
        QueueExport($"Trim \u00B7 {asset.Name}", (progress, token) => selectedEngine.TrimVideoAsync(asset, range, selectedFrames, info, directory, progress, token));
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
        QueueExport($"Audio \u00B7 {asset.Name}", (progress, token) => selectedEngine.ExportAudioAsync(asset, info, track, channel, range, directory, progress, token));
    });

    private void QueueExport(string title, Func<IProgress<ExportProgress>, CancellationToken, Task<string>> action)
    {
        if (Jobs.Count(job => !job.IsFinished) >= 20)
            throw new InvalidOperationException("The queue is full. Wait for a job to finish or cancel one.");
        var job = new ExportJobViewModel(title, lifetime.Token);
        Jobs.Insert(0, job);
        InspectorTab = 2;
        Status = $"Queued: {title}";
        _ = RunExportAsync(job, action);
    }

    private async Task RunExportAsync(ExportJobViewModel job, Func<IProgress<ExportProgress>, CancellationToken, Task<string>> action)
    {
        var acquired = false;
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
            Status = $"Saved: {job.OutputPath}";
        }
        catch (OperationCanceledException) { job.Status = "Cancelled; frame sequences may retain partial output."; }
        catch (Exception exception) { job.Status = "Failed: " + exception.Message; ReportError(exception); }
        finally
        {
            job.IsFinished = true;
            if (acquired)
                exportGate.Release();
        }
    }

    [RelayCommand]
    private void CancelJob(ExportJobViewModel? job) { if (job is { IsFinished: false }) job.Cancellation.Cancel(); }

    [RelayCommand]
    private void BrowseExportDirectory()
    {
        var path = PathPickerWindow.Select(PathPickerMode.Folder, "Default export destination", ExportDirectory);
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
            Status = "Settings saved. Future exports use this destination without a save dialog.";
        }
        catch (Exception exception) { ReportError(exception); }
    }

    [RelayCommand]
    private void RevealFile() => Guard(() =>
    {
        if (SelectedAsset is null)
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SelectedAsset.Asset.FullPath}\"") { UseShellExecute = true });
    });

    [RelayCommand]
    private void OpenExports() => Guard(() =>
    {
        Directory.CreateDirectory(settings.ExportDirectory);
        Process.Start(new ProcessStartInfo(settings.ExportDirectory) { UseShellExecute = true });
    });

    private MediaEngine CreateEngine() => new(ToolPaths.Resolve(settings.FfmpegDirectory), Path.Combine(dataDirectory, "cache"), settings.CacheMegabytes);

    private void RequireLibrary()
    {
        if (virtualSource) throw new InvalidOperationException("Favorites transfer applies to a folder. Reopen the original media root first.");
        if (!Directory.Exists(LibraryRoot))
            throw new InvalidOperationException("Open a media folder first.");
    }

    private void NotifyMediaProperties()
    {
        foreach (var property in new[] { nameof(HasSelection), nameof(IsPhoto), nameof(IsVideo), nameof(IsAudio), nameof(IsVisual), nameof(CanCopy), nameof(CopyLabel), nameof(ExportLabel), nameof(SelectedKindLabel) })
            OnPropertyChanged(property);
        foreach (var property in new[] { nameof(CanExportFrame), nameof(HasFrames), nameof(CanPlay), nameof(HasAudio), nameof(MaximumFrame), nameof(FrameLabel), nameof(SelectionLabel), nameof(RangeLabel), nameof(CurrentFrame) })
            OnPropertyChanged(property);
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private void ReportError(Exception exception)
    {
        Status = exception.Message.Length <= 600 ? exception.Message : exception.Message[..600] + " (see app.log)";
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
        lifetime.Cancel();
        Player.Playing -= OnPlaying;
        Player.EndReached -= OnEndReached;
        Player.Stop();
        Player.Dispose();
        libVlc.Dispose();
    }
}
