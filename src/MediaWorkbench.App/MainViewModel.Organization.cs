using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

public sealed partial class MainViewModel
{
    private TagStore tagStore = null!;
    private Dictionary<string, string[]> tagIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly CollectionStore collectionStore = new();
    private readonly ThumbnailLoader thumbnails = new();
    private readonly SemaphoreSlim photoDecodeGate = new(1);
    private string? activeCollectionPath;
    private bool virtualSource;
    private bool hasSource;
    private int visibleCount;
    public ObservableCollection<MetadataRow> Metadata { get; } = [];
    public ObservableCollection<string> SelectedTags { get; } = [];
    public ObservableCollection<string> KnownTags { get; } = [];
    public ObservableCollection<CollectionItem> Collections { get; } = [];
    public string[] SortOptions { get; } = ["Name (natural)", "Name (reverse)", "Newest modified", "Oldest modified", "Largest first", "Smallest first", "Media type", "Full path"];
    public bool HasSelection => SelectedAsset is not null;
    public bool IsPhoto => SelectedAsset?.Asset.Kind == MediaKind.Photo;
    public bool IsVideo => SelectedAsset?.Asset.Kind == MediaKind.Video;
    public bool IsAudio => SelectedAsset?.Asset.Kind == MediaKind.Audio;
    public bool IsVisual => IsPhoto || IsVideo;
    public bool HasCrop => CropSelection is not null;
    public bool CanCopy => PreviewImage is BitmapSource && !IsFrameLoading && !ShowPlayback;
    public string CopyLabel => HasCrop ? "Copy crop" : IsVideo ? "Copy frame" : "Copy image";
    public string ExportLabel => IsPhoto ? "Export PNG" : "Export frame";
    public string CropLabel => CropSelection is { } crop ? $"{crop.Width} x {crop.Height} px / {MediaDimensions.DescribeAspect(crop.Width, crop.Height)}" : "Drag over the preview to select pixels. Clipboard only; originals stay unchanged.";
    public string SelectedKindLabel => SelectedAsset?.Asset.Kind.ToString() ?? "No selection";
    public string SourceSummary => virtualSource ? SourceName : LibraryRoot;
    public string VisibleCount => $"{visibleCount:N0} of {Assets.Count:N0} files" + (HasFolderFilter ? $" in {folderFilter}" : "");
    public bool IsCollectionView => activeCollectionPath is not null;
    public double ThumbnailWidth => ThumbnailHeight * 1.6;
    public double FilmstripHeight => ThumbnailHeight + 48;
    /// <summary>The inspected file is still selected but no longer passes the filters. It is kept rather than torn down.</summary>
    public bool IsSelectionHidden => SelectedAsset is { } selected && !FilterAsset(selected);
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText) || MediaFilter != "All media" || FavoritesOnly || !string.IsNullOrWhiteSpace(TagFilter) || HasFolderFilter;
    public string StageFilteredLabel => $"Stage {visibleCount:N0} filtered";
    public bool HasSource => hasSource;
    public string EmptyTitle => !hasSource ? "Open a folder to begin"
        : HasSelection ? (IsAudio ? "Audio file selected" : "Loading preview…")
        : IsScanning && Assets.Count == 0 ? "Scanning…"
        : Assets.Count == 0 ? "No supported media here"
        : visibleCount == 0 ? "Nothing matches the current filters"
        : "Select a file from the filmstrip";
    public string EmptyText => !hasSource ? "Browse local photos, videos and audio, or drop a folder onto this window. Originals are never modified; every edit is a separate export."
        : HasSelection ? (IsAudio ? "Play it with Space, or pick a track, channel and time range in the Export tab." : "Decoding the first still image.")
        : IsScanning && Assets.Count == 0 ? "Media files appear in the filmstrip as folders are read."
        : Assets.Count == 0 ? "Supported extensions include common photo, video and audio formats. Use Rescan after adding files."
        : visibleCount == 0 ? "Clear the search, media type, favorites or tag filters to see files again."
        : "Its tools and metadata appear here. Press F to favorite, S to stage, E to export.";
    public bool ShowEmptyOpenFolder => !hasSource;
    public bool ShowEmptyClearFilters => hasSource && !HasSelection && Assets.Count > 0 && visibleCount == 0;

    [ObservableProperty] private string sortMethod = "Name (natural)";
    [ObservableProperty] private string sourceName = "Open a folder to begin";
    [ObservableProperty] private bool showSources = true;
    [ObservableProperty] private bool showInspector = true;
    [ObservableProperty] private int inspectorTab;
    [ObservableProperty] private double thumbnailHeight = 84;
    [ObservableProperty] private string tagText = "";
    [ObservableProperty] private string tagFilter = "";
    [ObservableProperty] private string? selectedKnownTag;
    [ObservableProperty] private string collectionName = "New collection";
    [ObservableProperty] private CollectionItem? selectedCollection;
    [ObservableProperty] private PixelCrop? cropSelection;
    [ObservableProperty] private bool isCropping;
    [ObservableProperty] private string mediaSummary = "";
    [ObservableProperty] private bool isMetadataTagMode;

    private void InitializeOrganization()
    {
        tagStore = new TagStore(Path.Combine(dataDirectory, "catalog.db"));
        ReloadTags();
        RefreshCollections();
        SortMethod = SortOptions.Contains(settings.SortMethod) ? settings.SortMethod : SortOptions[0];
        // One-time: the filmstrip header row is gone, so its height goes to the thumbnails.
        ThumbnailHeight = settings.LayoutVersion < 2 ? Math.Min(168, settings.ThumbnailHeight + 32) : settings.ThumbnailHeight;
        ShowSources = settings.ShowSources;
        ShowInspector = settings.ShowInspector;
        FollowFilmstrip = settings.FollowFilmstrip;
        ApplySort();
    }

    partial void OnSortMethodChanged(string value) => ApplySort();
    partial void OnTagFilterChanged(string value) => RefreshView();
    partial void OnThumbnailHeightChanged(double value) { OnPropertyChanged(nameof(ThumbnailWidth)); OnPropertyChanged(nameof(FilmstripHeight)); }
    partial void OnSourceNameChanged(string value) => OnPropertyChanged(nameof(SourceSummary));
    partial void OnCropSelectionChanged(PixelCrop? value) { OnPropertyChanged(nameof(CopyLabel)); OnPropertyChanged(nameof(CropLabel)); OnPropertyChanged(nameof(HasCrop)); }
    partial void OnIsCroppingChanged(bool value) { if (value && ShowPlayback) { PauseAtPlaybackPosition(); _ = SeekFrameAsync(); } }
    partial void OnSelectedKnownTagChanged(string? value) { if (value is not null) TagFilter = value; }
    partial void OnIsScanningChanged(bool value) => NotifyEmptyState();

    private void RefreshView()
    {
        LibraryView.Refresh();
        UpdateVisibleCount();
        OnPropertyChanged(nameof(IsSelectionHidden));
        OnPropertyChanged(nameof(HasActiveFilters));
        // The filmstrip highlight follows the view; the inspected asset itself is never cleared by a filter.
        OnPropertyChanged(nameof(SelectedAsset));
        NotifyEmptyState();
    }

    private void UpdateVisibleCount()
    {
        visibleCount = LibraryView is ListCollectionView view ? view.Count : LibraryView.Cast<object>().Count();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(StageFilteredLabel));
    }

    private void NotifyEmptyState()
    {
        foreach (var property in new[] { nameof(EmptyTitle), nameof(EmptyText), nameof(ShowEmptyOpenFolder), nameof(ShowEmptyClearFilters), nameof(HasSource), nameof(ShowEmptyState), nameof(ShowLibraryEmpty), nameof(ShowLibraryMap) })
            OnPropertyChanged(property);
    }

    private void ApplySort()
    {
        if (LibraryView is ListCollectionView view) view.CustomSort = new AssetComparer(SortMethod);
        UpdateVisibleCount();
    }

    private sealed class AssetComparer(string method) : IComparer
    {
        public int Compare(object? left, object? right)
        {
            if (left is not AssetViewModel first || right is not AssetViewModel second) return 0;
            var comparison = method switch
            {
                "Name (reverse)" => NaturalOrder.Compare(second.Name, first.Name),
                "Newest modified" => second.Asset.ModifiedTicks.CompareTo(first.Asset.ModifiedTicks),
                "Oldest modified" => first.Asset.ModifiedTicks.CompareTo(second.Asset.ModifiedTicks),
                "Largest first" => second.Asset.Length.CompareTo(first.Asset.Length),
                "Smallest first" => first.Asset.Length.CompareTo(second.Asset.Length),
                "Media type" => first.Asset.Kind.CompareTo(second.Asset.Kind),
                "Full path" => NaturalOrder.Compare(first.Asset.FullPath, second.Asset.FullPath),
                _ => NaturalOrder.Compare(first.Name, second.Name)
            };
            return comparison != 0 ? comparison : NaturalOrder.Compare(first.Asset.FullPath, second.Asset.FullPath);
        }
    }

    private void ReloadTags()
    {
        tagIndex = tagStore.ReadAll();
        KnownTags.Clear();
        foreach (var tag in tagIndex.Values.SelectMany(tags => tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)) KnownTags.Add(tag);
        foreach (var item in Assets) item.Tags = tagIndex.GetValueOrDefault(item.Asset.FullPath, []);
        SelectedTags.Clear();
        if (SelectedAsset is { } selected)
            foreach (var tag in selected.Tags) SelectedTags.Add(tag);
    }

    [RelayCommand]
    private void AddTags() => Guard(() =>
    {
        if (SelectedAsset is not { } item) return;
        var tags = TagText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tags.Length == 0) throw new ArgumentException("Type one or more comma-separated tags.");
        tagStore.Add(item.Asset.FullPath, tags);
        TagText = "";
        ReloadTags();
        RefreshView();
        Status = "Tags saved locally. Media files were not modified.";
    });

    [RelayCommand]
    private void RemoveTag(string? tag) => Guard(() =>
    {
        if (SelectedAsset is null || tag is null) return;
        tagStore.Remove(SelectedAsset.Asset.FullPath, tag);
        ReloadTags();
        RefreshView();
    });

    [RelayCommand]
    private void ToggleMetadataTagMode()
    {
        IsMetadataTagMode = !IsMetadataTagMode;
        if (!IsMetadataTagMode)
            foreach (var row in Metadata) row.IsSelected = false;
    }

    [RelayCommand]
    private void ConfirmMetadataTags() => Guard(() =>
    {
        if (SelectedAsset is null) return;
        var selected = Metadata.Where(row => row.CanTag && row.IsSelected).Select(row => row.Tag).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Check the metadata values you want, then confirm.");
        tagStore.Add(SelectedAsset.Asset.FullPath, selected, "confirmed metadata");
        foreach (var row in Metadata) row.IsSelected = false;
        IsMetadataTagMode = false;
        ReloadTags();
        RefreshView();
        Status = $"Added {selected.Length} confirmed metadata tags. Nothing is tagged automatically.";
        Notify(NotificationKind.Success, $"Added {selected.Length} metadata tags to {SelectedAsset.Name}.");
    });

    [RelayCommand]
    private async Task BrowseTaggedAsync()
    {
        var query = TagFilter.Trim();
        var paths = tagIndex.Where(pair => query.Length == 0 || pair.Value.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase))).Select(pair => pair.Key).ToArray();
        activeCollectionPath = null;
        await LoadVirtualAsync(paths, query.Length == 0 ? "All tagged media" : $"Tag search: {query}");
    }

    [RelayCommand]
    private async Task FindRelatedAsync()
    {
        if (SelectedAsset is not { Tags.Length: > 0 } selected) { Status = "Add a tag before finding related files."; return; }
        var sourcePath = selected.Asset.FullPath;
        var tags = selected.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = tagIndex.Where(pair => !string.Equals(pair.Key, sourcePath, StringComparison.OrdinalIgnoreCase) && pair.Value.Any(tags.Contains)).Select(pair => pair.Key).ToArray();
        SearchText = "";
        TagFilter = "";
        MediaFilter = "All media";
        FavoritesOnly = false;
        activeCollectionPath = null;
        await LoadVirtualAsync(paths, $"Shared tags with {selected.Name}");
    }

    [RelayCommand]
    private void ExportTagGraph() => Guard(() =>
    {
        using var output = OutputReservation.Create(settings.ExportDirectory, "media-tag-relationships", ".json");
        File.WriteAllText(output.Path, tagStore.ExportJson());
        output.Complete();
        Status = $"Tag relationships exported: {output.Path}. Includes absolute file paths.";
        var path = output.Path;
        Notify(NotificationKind.Success, "Tag relationships exported. The file includes absolute paths.", "Open output", () => RevealPath(path));
    });

    private void RefreshCollections()
    {
        var directory = Path.Combine(dataDirectory, "collections");
        Directory.CreateDirectory(directory);
        var selectedPath = SelectedCollection?.FilePath;
        Collections.Clear();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order())
        {
            try { Collections.Add(new CollectionItem(collectionStore.Load(path).Name, path)); }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException) { ReportError(exception); }
        }
        SelectedCollection = Collections.FirstOrDefault(item => item.FilePath == selectedPath) ?? Collections.FirstOrDefault();
    }

    [RelayCommand]
    private void CreateCollection() => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(CollectionName)) throw new ArgumentException("Enter a collection name.");
        var path = Path.Combine(dataDirectory, "collections", Guid.NewGuid().ToString("N") + ".json");
        collectionStore.Save(path, new StagingCollection(1, CollectionName, []));
        RefreshCollections();
        SelectedCollection = Collections.Single(item => item.FilePath == path);
        Status = "Collection created. Stage an item, or stage all filtered results; originals stay in place.";
    });

    [RelayCommand]
    private void StageSelected() => Guard(() =>
    {
        if (SelectedAsset is not { } item) throw new InvalidOperationException("Select media to stage.");
        StagePaths([item.Asset.FullPath]);
    });

    [RelayCommand]
    private void StageFiltered() => Guard(() => StagePaths(LibraryView.Cast<AssetViewModel>().Select(item => item.Asset.FullPath).ToArray()));

    private void StagePaths(string[] paths)
    {
        if (SelectedCollection is not { } target) throw new InvalidOperationException("Create or select a staging collection first.");
        var collection = collectionStore.Load(target.FilePath);
        var updated = CollectionStore.Add(collection, paths);
        collectionStore.Save(target.FilePath, updated);
        Status = $"Added {updated.Paths.Length - collection.Paths.Length:N0} items to {target.Name}. References only; no media copied.";
    }

    [RelayCommand]
    private async Task OpenCollectionAsync()
    {
        try
        {
            if (SelectedCollection is not { } selected) return;
            var collection = collectionStore.Load(selected.FilePath);
            activeCollectionPath = selected.FilePath;
            await LoadVirtualAsync(collection.Paths, collection.Name);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    [RelayCommand]
    private async Task OpenCollectionItemAsync(CollectionItem? item)
    {
        if (item is null) return;
        SelectedCollection = item;
        await OpenCollectionAsync();
    }

    [RelayCommand]
    private async Task RemoveFromCollectionAsync()
    {
        try
        {
            if (activeCollectionPath is null || SelectedAsset is null) return;
            var collection = CollectionStore.Remove(collectionStore.Load(activeCollectionPath), SelectedAsset.Asset.FullPath);
            collectionStore.Save(activeCollectionPath, collection);
            await LoadVirtualAsync(collection.Paths, collection.Name);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    [RelayCommand]
    private async Task LoadCollectionJsonAsync()
    {
        try
        {
            var path = NativeDialogs.OpenJson("Load collection JSON", settings.ExportDirectory);
            if (path is null) return;
            var collection = collectionStore.Load(path);
            var local = Path.Combine(dataDirectory, "collections", Guid.NewGuid().ToString("N") + ".json");
            collectionStore.Save(local, collection);
            RefreshCollections();
            SelectedCollection = Collections.Single(item => item.FilePath == local);
            await OpenCollectionAsync();
        }
        catch (Exception exception) { ReportError(exception); }
    }

    [RelayCommand]
    private void ExportCollectionJson() => Guard(() =>
    {
        if (SelectedCollection is not { } item) return;
        using var output = OutputReservation.Create(settings.ExportDirectory, item.Name + "_collection", ".json");
        collectionStore.Save(output.Path, collectionStore.Load(item.FilePath));
        output.Complete();
        Status = "Saved collection JSON: " + output.Path;
        var path = output.Path;
        Notify(NotificationKind.Success, $"Saved collection JSON for {item.Name}.", "Open output", () => RevealPath(path));
    });

    private async Task LoadVirtualAsync(string[] paths, string name)
    {
        scanCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        scanCancellation = cancellation;
        IsScanning = true;
        SelectedAsset = null;
        Assets.Clear();
        virtualSource = true;
        hasSource = true;
        ResetFolderTree();
        SourceName = name;
        OnPropertyChanged(nameof(IsCollectionView));
        UpdateVisibleCount();
        NotifyEmptyState();
        var missing = 0;
        try
        {
            await Task.Run(async () =>
            {
                var batch = new List<MediaAsset>(200);
                foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    MediaAsset? asset = null;
                    try { asset = LibraryScanner.ReadFile(path); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                    if (asset is null) { missing++; continue; }
                    batch.Add(asset);
                    if (batch.Count < 200) continue;
                    await AddVirtualBatchAsync(batch.ToArray(), cancellation.Token);
                    batch.Clear();
                }
                if (batch.Count > 0) await AddVirtualBatchAsync(batch.ToArray(), cancellation.Token);
            }, cancellation.Token);
            Status = $"{Assets.Count:N0} available items / {missing:N0} missing or unsupported references. Collection paths are preserved.";
            if (missing > 0)
                Notify(NotificationKind.Info, $"{missing:N0} referenced files are missing or unsupported. Their references were kept.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            if (scanCancellation == cancellation) { scanCancellation = null; IsScanning = false; }
            RebuildFolderTree();
            RefreshView();
            OnPropertyChanged(nameof(LibraryLabel));
        }
    }

    private async Task AddVirtualBatchAsync(MediaAsset[] batch, CancellationToken token)
    {
        catalog.Index(batch, token);
        var favorites = catalog.GetFavoritePaths();
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            Assets.AddRange(batch.Select(asset => new AssetViewModel(asset, favorites.Contains(asset.FullPath)) { Tags = tagIndex.GetValueOrDefault(asset.FullPath, []), FolderKey = FolderKeyOf(asset) }));
            AfterBatchAdded();
        }, System.Windows.Threading.DispatcherPriority.Background, token);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        MediaFilter = "All media";
        FavoritesOnly = false;
        TagFilter = "";
        SelectedKnownTag = null;
        if (HasFolderFilter) SelectFolder("");
    }

    [RelayCommand]
    private void ToggleSources() => ShowSources = !ShowSources;
    [RelayCommand]
    private void ToggleInspector() => ShowInspector = !ShowInspector;
    [RelayCommand]
    private void ShowSettings()
    {
        if (!ShowInspector) { ShowInspector = true; InspectorTab = 3; return; }
        InspectorTab = InspectorTab == 3 ? 0 : 3;
    }
    [RelayCommand]
    private void PreviousAsset() => MoveAsset(-1);
    [RelayCommand]
    private void NextAsset() => MoveAsset(1);

    private void MoveAsset(int delta)
    {
        if (LibraryView is not ListCollectionView view || view.Count == 0) return;
        var index = SelectedAsset is null ? -1 : view.IndexOf(SelectedAsset);
        SelectedAsset = (AssetViewModel)view.GetItemAt(Math.Clamp(index + delta, 0, view.Count - 1));
    }

    [RelayCommand]
    private void ToggleCrop() { if (!IsCropping) IsFraming = false; IsCropping = !IsCropping; }
    [RelayCommand]
    private void ResetCrop() { CropSelection = null; IsCropping = false; }
    [RelayCommand]
    private void CopyPreview() => Guard(() =>
    {
        if (!CanCopy) throw new InvalidOperationException("Wait for a still preview before copying. Pause video to copy its current frame.");
        Clipboard.SetImage(BuildClipboardImage());
        Status = HasCrop ? "Cropped pixels copied to clipboard. No media file saved or changed." : "Image pixels copied to clipboard. No media file saved or changed.";
        Notify(NotificationKind.Success, HasCrop ? $"Copied {CropSelection!.Width} x {CropSelection.Height} px to the clipboard." : "Copied the full image to the clipboard.");
    });

    internal BitmapSource BuildClipboardImage()
    {
        if (PreviewImage is not BitmapSource source) throw new InvalidOperationException("No decoded image is available.");
        if (CropSelection is not { } crop) return source;
        crop.Validate(source.PixelWidth, source.PixelHeight);
        var image = new CroppedBitmap(source, new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
        image.Freeze();
        return image;
    }

    private void ShowMetadata(MediaAsset asset, IReadOnlyDictionary<string, string> values)
    {
        Metadata.Clear();
        // The file name is in the header above the preview and the kind is this tab's heading, so neither is repeated here.
        Metadata.Add(new MetadataRow("Folder", Path.GetDirectoryName(asset.FullPath) ?? "", false));
        Metadata.Add(new MetadataRow("Type", $"{asset.Kind}, {Path.GetExtension(asset.Name).TrimStart('.').ToLowerInvariant()}"));
        Metadata.Add(new MetadataRow("File size", $"{asset.Length / 1048576.0:N2} MB ({asset.Length:N0} bytes)", false));
        Metadata.Add(new MetadataRow("Modified", new DateTime(asset.ModifiedTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        // The header line under the file name carries the frame rate too; the large readout beside the frame counter is the measured one.
        var headerRate = IsVideo && (FrameRateInfo.FromRatio(values.GetValueOrDefault("avg_frame_rate")) ?? FrameRateInfo.FromRatio(values.GetValueOrDefault("r_frame_rate"))) is { } rate ? $", {rate.Number} fps" : "";
        if (PreviewImage is BitmapSource image)
        {
            Metadata.Add(new MetadataRow("Dimensions", $"{image.PixelWidth} × {image.PixelHeight}, {(image.PixelWidth == image.PixelHeight ? "square" : image.PixelWidth > image.PixelHeight ? "landscape" : "portrait")}, {image.PixelWidth * (double)image.PixelHeight / 1000000:0.##} MP"));
            Metadata.Add(new MetadataRow("Aspect ratio", MediaDimensions.DescribeAspect(image.PixelWidth, image.PixelHeight)));
            MediaSummary = $"{image.PixelWidth:N0} × {image.PixelHeight:N0}, {MediaDimensions.DescribeAspect(image.PixelWidth, image.PixelHeight)}{headerRate}, {asset.Length / 1048576.0:N1} MB";
        }
        if (mediaInfo is { Duration: > 0 } info)
        {
            Metadata.Add(new MetadataRow("Duration", info.Duration >= 60 ? $"{WaveformView.FormatTime(info.Duration)} ({info.Duration:0.###} s)" : $"{info.Duration:0.###} s"));
            Metadata.Add(new MetadataRow("Sound", info.AudioTracks.Count == 0 ? "none" : string.Join("; ", info.AudioTracks.Select(track => track.Label))));
            if (IsAudio) MediaSummary = $"{info.Duration:0.###} s, {info.AudioTracks.Count} tracks, {asset.Length / 1048576.0:N1} MB";
            else if (PreviewImage is null && values.TryGetValue("width", out var width) && values.TryGetValue("height", out var height))
                MediaSummary = $"{width} × {height}{headerRate}, {info.Duration:0.###} s, {asset.Length / 1048576.0:N1} MB";
        }
        var names = new Dictionary<string, string>
        {
            ["format_name"] = "Container", ["bit_rate"] = "Bit rate", ["size"] = "Container size (bytes)",
            ["width"] = "Encoded width", ["height"] = "Encoded height", ["codec_name"] = "Video codec",
            ["pix_fmt"] = "Pixel format", ["avg_frame_rate"] = "Frame rate", ["r_frame_rate"] = "Nominal rate", ["display_aspect_ratio"] = "Display aspect",
            ["sample_aspect_ratio"] = "Pixel aspect", ["color_space"] = "Color space", ["color_transfer"] = "Color transfer",
            ["bits_per_raw_sample"] = "Bits per channel"
        };
        // One row for the encoded size, one for the frame rate when both figures agree, and no second copy of the file size.
        var skip = new HashSet<string> { "size", "width", "height" };
        if (values.TryGetValue("width", out var encodedWidth) && values.TryGetValue("height", out var encodedHeight))
            Metadata.Add(new MetadataRow("Encoded size", $"{encodedWidth} × {encodedHeight}"));
        if (values.TryGetValue("avg_frame_rate", out var average) && values.GetValueOrDefault("r_frame_rate") == average)
            skip.Add("r_frame_rate");
        foreach (var pair in values)
        {
            if (skip.Contains(pair.Key))
                continue;
            var value = pair.Value;
            if (pair.Key == "bit_rate" && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var bits))
                value = bits >= 1_000_000 ? $"{bits / 1_000_000:0.##} Mbit/s" : $"{bits / 1000:0.#} kbit/s";
            if (pair.Key is "avg_frame_rate" or "r_frame_rate")
            {
                var parts = value.Split('/');
                if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var numerator)
                    && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var denominator) && denominator > 0)
                    value = $"{numerator / denominator:0.###} fps ({pair.Value})";
            }
            Metadata.Add(new MetadataRow(names.GetValueOrDefault(pair.Key, pair.Key), value));
        }
    }

    private AppSettings WithBrowsingPreferences(AppSettings value) => value with { SortMethod = SortMethod, ThumbnailHeight = ThumbnailHeight, ShowSources = ShowSources, ShowInspector = ShowInspector, FollowFilmstrip = FollowFilmstrip, LayoutVersion = 2 };
}
