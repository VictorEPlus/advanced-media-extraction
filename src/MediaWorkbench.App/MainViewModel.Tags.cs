using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

/// <summary>A tag a file carries because a folder above it is tagged.</summary>
public sealed record InheritedTag(string Tag, string Folder)
{
    public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)) is { Length: > 0 } name ? name : Folder;
    public string ToolTipText => $"From the folder {Folder}. Every file in that folder and its subfolders carries it; remove it there.";
}

// Tags: a file's own tags, the live tags of the folders above it, suggestions from tagged files that resemble it, and keeping
// all of that with the files when they are renamed, moved or copied.
public sealed partial class MainViewModel
{
    private Dictionary<string, string[]> folderTagIndex = new(StringComparer.OrdinalIgnoreCase);
    private List<TaggedFile> taggedFiles = [];
    private readonly SemaphoreSlim tagWork = new(1);
    /// <summary>The selected file's details as suggestions compare them, once the file has been read.</summary>
    private (AssetViewModel Item, FileTraits Traits)? currentTraits;

    public ObservableCollection<InheritedTag> InheritedTags { get; } = [];
    public ObservableCollection<TagSuggestion> Suggestions { get; } = [];
    public ObservableCollection<string> FolderTagsOfTarget { get; } = [];
    public bool HasInheritedTags => InheritedTags.Count > 0;
    public bool HasSuggestions => Suggestions.Count > 0;
    [ObservableProperty] private string folderTagText = "";

    /// <summary>Raised to put the cursor in the folder tag box, after "Tag this folder" in the tree.</summary>
    public event EventHandler? FolderTagRequested;

    /// <summary>The folder on disk that the folder tag box tags: the one selected in the workspace tree.</summary>
    public string? FolderTagTarget => SelectedFolderRow is { } row ? RealFolder(row) : null;
    public string FolderTagTargetLabel => FolderTagTarget is { } folder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) : "Select a folder in the workspace tree";
    public bool CanTagFolder => FolderTagTarget is not null;

    /// <summary>A tree row's folder on disk, or null for All folders and for collections and tag searches.</summary>
    private string? RealFolder(FolderRowViewModel row)
    {
        if (row.IsAllFolders || FolderOf(row.Node.Path) is not { IsVirtual: false } folder)
            return null;
        var relative = row.Node.Path.Length > folder.Label.Length ? row.Node.Path[(folder.Label.Length + 1)..] : "";
        return Path.TrimEndingDirectorySeparator(Path.Combine(folder.Path, relative));
    }

    /// <summary>Everything a file carries: its own tags and those of every folder above it.</summary>
    private string[] TagsFor(MediaAsset asset)
    {
        var own = tagIndex.GetValueOrDefault(asset.FullPath, []);
        if (folderTagIndex.Count == 0)
            return own;
        var inherited = FolderTags.Inherited(Path.GetDirectoryName(asset.FullPath) ?? "", folderTagIndex).Select(entry => entry.Tag);
        return own.Concat(inherited).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void ReloadTags()
    {
        tagIndex = tagStore.ReadAll();
        folderTagIndex = tagStore.ReadFolderTags();
        taggedFiles = tagStore.ReadFiles();
        var known = tagIndex.Values.Concat(folderTagIndex.Values).SelectMany(tags => tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!known.SequenceEqual(KnownTags, StringComparer.OrdinalIgnoreCase))
        {
            KnownTags.Clear();
            foreach (var tag in known) KnownTags.Add(tag);
        }
        foreach (var item in Assets) item.Tags = TagsFor(item.Asset);
        ShowTagsOf(SelectedAsset);
        UpdateOverviewTags();
        ShowFolderTags();
        UpdateSuggestions();
    }

    /// <summary>The Tags tab for a file: its own tags, and the ones it carries from its folders.</summary>
    private void ShowTagsOf(AssetViewModel? item)
    {
        SelectedTags.Clear();
        InheritedTags.Clear();
        if (item is not null)
        {
            foreach (var tag in tagIndex.GetValueOrDefault(item.Asset.FullPath, [])) SelectedTags.Add(tag);
            foreach (var (tag, folder) in FolderTags.Inherited(Path.GetDirectoryName(item.Asset.FullPath) ?? "", folderTagIndex))
                InheritedTags.Add(new InheritedTag(tag, folder));
        }
        OnPropertyChanged(nameof(HasInheritedTags));
        ShowCollectionsOf(item);
    }

    private void ShowFolderTags()
    {
        FolderTagsOfTarget.Clear();
        if (FolderTagTarget is { } folder)
            foreach (var tag in folderTagIndex.GetValueOrDefault(folder, [])) FolderTagsOfTarget.Add(tag);
        OnPropertyChanged(nameof(FolderTagTarget));
        OnPropertyChanged(nameof(FolderTagTargetLabel));
        OnPropertyChanged(nameof(CanTagFolder));
    }

    private static string[] SplitTags(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [RelayCommand]
    private void AddTags() => Guard(() =>
    {
        if (SelectedAsset is not { } item) return;
        var tags = SplitTags(TagText);
        if (tags.Length == 0) throw new ArgumentException("Type one or more comma-separated tags.");
        TagFile(item, tags, "manual");
        TagText = "";
        Status = "Tags saved in the app. The file itself was not changed.";
    });

    /// <summary>Tags a file, saves its details for suggestions, and fingerprints it in the background so its tags can follow it.</summary>
    private void TagFile(AssetViewModel item, IEnumerable<string> tags, string source)
    {
        tagStore.Add(item.Asset.FullPath, tags, source);
        if (currentTraits is { } traits && ReferenceEquals(traits.Item, item))
            tagStore.SetTraits(item.Asset.FullPath, traits.Traits.ToJson());
        ReloadTags();
        RefreshView();
        _ = FingerprintTaggedFilesAsync();
    }

    [RelayCommand]
    private void RemoveTag(string? tag) => Guard(() =>
    {
        if (SelectedAsset is null || tag is null) return;
        tagStore.Remove(SelectedAsset.Asset.FullPath, tag);
        ReloadTags();
        RefreshView();
    });

    [RelayCommand]
    private void ApplySuggestion(TagSuggestion? suggestion) => Guard(() =>
    {
        if (SelectedAsset is not { } item || suggestion is null) return;
        TagFile(item, [suggestion.Tag], "suggested");
        Status = $"Added {suggestion.Tag}: {suggestion.Files:N0} tagged files, {suggestion.Reason}.";
    });

    [RelayCommand]
    private void ConfirmMetadataTags() => Guard(() =>
    {
        if (SelectedAsset is not { } item) return;
        var selected = Metadata.Where(row => row.CanTag && row.IsSelected).Select(row => row.Tag).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Tick the details you want, then confirm.");
        TagFile(item, selected, "confirmed metadata");
        foreach (var row in Metadata) row.IsSelected = false;
        IsMetadataTagMode = false;
        Status = $"Added {selected.Length} tags from the details. Nothing is tagged automatically.";
        Notify(NotificationKind.Success, $"Added {selected.Length} tags to {item.Name}.");
    });

    /// <summary>From the tree's right-click menu: select the folder and put the cursor in the folder tag box.</summary>
    [RelayCommand]
    private void TagFolder(FolderRowViewModel? row)
    {
        if (row is null) return;
        SelectedFolderRow = row;
        ShowInspector = true;
        InspectorTab = 1;
        ShowFolderTags();
        FolderTagRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddFolderTags() => Guard(() =>
    {
        if (FolderTagTarget is not { } folder) throw new InvalidOperationException("Select a folder of the workspace in the tree first.");
        var tags = SplitTags(FolderTagText);
        if (tags.Length == 0) throw new ArgumentException("Type one or more comma-separated tags.");
        tagStore.AddFolderTags(folder, tags);
        FolderTagText = "";
        ReloadTags();
        RefreshView();
        RebuildFolderRows();
        Status = $"Every file in {Path.GetFileName(folder)} and its subfolders now carries {string.Join(", ", tags)}, including files added later.";
    });

    [RelayCommand]
    private void RemoveFolderTag(string? tag) => Guard(() =>
    {
        if (FolderTagTarget is not { } folder || tag is null) return;
        tagStore.RemoveFolderTag(folder, tag);
        ReloadTags();
        RefreshView();
        RebuildFolderRows();
    });

    /// <summary>Folders on disk that carry tags, for the marker on their tree rows.</summary>
    private bool FolderHasTags(FolderNode node)
    {
        if (folderTagIndex.Count == 0 || FolderOf(node.Path) is not { IsVirtual: false } folder) return false;
        var relative = node.Path.Length > folder.Label.Length ? node.Path[(folder.Label.Length + 1)..] : "";
        return folderTagIndex.ContainsKey(Path.TrimEndingDirectorySeparator(Path.Combine(folder.Path, relative)));
    }

    [RelayCommand]
    private async Task BrowseTaggedAsync()
    {
        var query = TagFilter.Trim();
        bool Matches(IEnumerable<string> tags) => tags.Any(tag => query.Length == 0 || tag.Contains(query, StringComparison.OrdinalIgnoreCase));
        // Files tagged directly, wherever they are, and files in the workspace that carry a matching folder tag.
        var paths = tagIndex.Where(pair => Matches(pair.Value)).Select(pair => pair.Key)
            .Concat(Assets.Where(item => item.Owner is { IsVirtual: false } && Matches(item.Tags)).Select(item => item.Asset.FullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await ShowVirtualFolderAsync(query.Length == 0 ? "Tagged" : $"Tag {query.Replace('\\', ' ')}", paths, null);
    }

    [RelayCommand]
    private async Task FindRelatedAsync()
    {
        if (SelectedAsset is not { Tags.Length: > 0 } selected) { Status = "Add a tag before finding related files."; return; }
        var sourcePath = selected.Asset.FullPath;
        var tags = selected.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = tagIndex.Where(pair => pair.Value.Any(tags.Contains)).Select(pair => pair.Key)
            .Concat(Assets.Where(item => item.Owner is { IsVirtual: false } && item.Tags.Any(tags.Contains)).Select(item => item.Asset.FullPath))
            .Where(path => !string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        SearchText = "";
        TagFilter = "";
        MediaFilter = "All media";
        FavoritesOnly = false;
        await ShowVirtualFolderAsync($"Shares tags with {selected.Name}", paths, null);
    }

    /// <summary>
    /// Reads the selected file's details for suggestions once it has been opened: camera, day, folder, numbered name, size, motion
    /// and a look-alike fingerprint of its thumbnail (the same picture a tagged file's fingerprint was made from).
    /// </summary>
    private async Task ReadTraitsAsync(AssetViewModel item, IReadOnlyDictionary<string, string> details, int width, int height, double duration)
    {
        ulong? visual = null;
        try { visual = await LookAlikeAsync(item.Asset); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or OperationCanceledException) { }
        if (!ReferenceEquals(SelectedAsset, item))
            return;
        var traits = FileTraits.From(item.Asset, details, width, height, duration, visual);
        currentTraits = (item, traits);
        // A tagged file that is looked at again brings its saved details up to date.
        if (tagIndex.ContainsKey(item.Asset.FullPath))
            Guard(() => tagStore.SetTraits(item.Asset.FullPath, traits.ToJson()));
        UpdateSuggestions();
    }

    private void UpdateSuggestions()
    {
        Suggestions.Clear();
        if (currentTraits is { } current && ReferenceEquals(current.Item, SelectedAsset))
        {
            var tagged = taggedFiles
                .Where(file => !string.Equals(file.Path, current.Item.Asset.FullPath, StringComparison.OrdinalIgnoreCase))
                .Select(file => (Traits: FileTraits.FromJson(file.Traits), Tags: (IReadOnlyCollection<string>)file.Tags))
                .Where(entry => entry.Traits is not null)
                .Select(entry => (entry.Traits!, entry.Tags));
            foreach (var suggestion in TagSuggester.Suggest(current.Traits, tagged, current.Item.Tags))
                Suggestions.Add(suggestion);
        }
        OnPropertyChanged(nameof(HasSuggestions));
    }

    /// <summary>The look-alike fingerprint of a file's thumbnail, reduced to 9 × 8 grey.</summary>
    private async Task<ulong?> LookAlikeAsync(MediaAsset asset)
    {
        if (asset.Kind == MediaKind.Audio)
            return null;
        var image = await thumbnails.LoadAsync(asset, engine, lifetime.Token);
        if (image is null)
            return null;
        return await Task.Run(() => LookAlikeOf(image));
    }

    internal static ulong LookAlikeOf(BitmapSource image)
    {
        var scaled = new TransformedBitmap(image, new ScaleTransform(VisualHash.Width / (double)image.PixelWidth, VisualHash.Height / (double)image.PixelHeight));
        var grey = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        var pixels = new byte[grey.PixelWidth * grey.PixelHeight];
        grey.CopyPixels(pixels, grey.PixelWidth, 0);
        // Rounding can leave the scaled picture a pixel off; sample it on the exact 9 × 8 grid.
        var cells = new byte[VisualHash.Width * VisualHash.Height];
        for (var row = 0; row < VisualHash.Height; row++)
            for (var column = 0; column < VisualHash.Width; column++)
                cells[row * VisualHash.Width + column] = pixels[Math.Min(row, grey.PixelHeight - 1) * grey.PixelWidth + Math.Min(column, grey.PixelWidth - 1)];
        return VisualHash.FromGrey(cells);
    }

    /// <summary>
    /// After a folder is read: tagged files that moved keep their tags, exact copies get them, and tagged folders that moved keep
    /// theirs. Runs in the background, one at a time.
    /// </summary>
    private async Task ReconcileTagsAsync(WorkspaceFolder folder)
    {
        if (folder.IsVirtual || taggedFiles.Count == 0 && folderTagIndex.Count == 0)
            return;
        var scanned = Assets.Where(item => item.Owner == folder).Select(item => item.Asset).ToList();
        await tagWork.WaitAsync(lifetime.Token);
        try
        {
            var result = await Task.Run(() =>
            {
                TagReconciler.FillFingerprints(tagStore, lifetime.Token);
                return TagReconciler.Reconcile(tagStore, folder.Path, scanned, lifetime.Token);
            }, lifetime.Token);
            if (!result.Changed)
                return;
            ReloadTags();
            RefreshView();
            var parts = new List<string>();
            if (result.Moved > 0) parts.Add($"{result.Moved:N0} moved or renamed {(result.Moved == 1 ? "file kept its" : "files kept their")} tags");
            if (result.Copied > 0) parts.Add($"{result.Copied:N0} {(result.Copied == 1 ? "copy" : "copies")} got the original's tags");
            if (result.FoldersMoved > 0) parts.Add($"{result.FoldersMoved:N0} moved {(result.FoldersMoved == 1 ? "folder kept its" : "folders kept their")} tags");
            Notify(NotificationKind.Info, $"{folder.Label}: {string.Join("; ", parts)}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { ReportError(exception); }
        finally { tagWork.Release(); }
    }

    private async Task FingerprintTaggedFilesAsync()
    {
        try
        {
            await tagWork.WaitAsync(lifetime.Token);
            try { await Task.Run(() => TagReconciler.FillFingerprints(tagStore, lifetime.Token), lifetime.Token); }
            finally { tagWork.Release(); }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Tagged files whose details were never saved (tagged before suggestions existed, or copies) are read in the background, a few
    /// at a time, so their tags can be suggested for files that resemble them.
    /// </summary>
    private async Task LearnTaggedDetailsAsync()
    {
        var missing = taggedFiles.Where(file => file.Traits is null && File.Exists(file.Path)).Take(200).ToList();
        var learned = 0;
        foreach (var file in missing)
        {
            if (lifetime.IsCancellationRequested) return;
            try
            {
                var asset = LibraryScanner.ReadFile(file.Path);
                if (asset is null) continue;
                IReadOnlyDictionary<string, string> details;
                int width = 0, height = 0;
                double duration = 0;
                if (asset.Kind == MediaKind.Photo)
                {
                    var photo = await Task.Run(() => ImageLoader.Load(asset.FullPath), lifetime.Token);
                    (details, width, height) = (photo.Metadata, photo.Image.PixelWidth, photo.Image.PixelHeight);
                }
                else
                {
                    var info = await engine.ProbeAsync(asset.FullPath, lifetime.Token);
                    details = info.Metadata;
                    duration = info.Duration;
                    int.TryParse(info.Metadata.GetValueOrDefault("width"), out width);
                    int.TryParse(info.Metadata.GetValueOrDefault("height"), out height);
                }
                var traits = FileTraits.From(asset, details, width, height, duration, await LookAlikeAsync(asset));
                tagStore.SetTraits(file.Path, traits.ToJson());
                learned++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException) { }
        }
        if (learned > 0)
        {
            taggedFiles = tagStore.ReadFiles();
            UpdateSuggestions();
        }
    }
}
