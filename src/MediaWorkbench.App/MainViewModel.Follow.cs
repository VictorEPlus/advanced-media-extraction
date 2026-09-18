using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaWorkbench.App;

// Instant preview: the thumbnail that is already in memory is shown at once, full size, while the real file loads.
// With "Preview follows scroll" on, the filmstrip peeks at whatever thumbnail is under its marker as it scrolls;
// a peek only swaps that picture and the name, so the layout stays still, and the file is opened once scrolling settles.
public sealed partial class MainViewModel
{
    private AssetViewModel? instantSource;

    /// <summary>When on, the preview follows the thumbnail under the filmstrip marker while scrolling.</summary>
    [ObservableProperty] private bool followFilmstrip;
    /// <summary>The file under the filmstrip marker while scrolling; it becomes the selection when scrolling settles.</summary>
    [ObservableProperty] private AssetViewModel? peekedAsset;
    [ObservableProperty] private ImageSource? instantPreview;

    public bool IsPeeking => PeekedAsset is not null;
    /// <summary>The stand-in picture covers the preview while peeking, or until the selected file's own picture is ready.</summary>
    public bool ShowInstantLayer => !ShowVideoSurface && (IsPeeking || InstantPreview is not null && PreviewImage is null && HasSelection && !IsAudio);
    public string InstantGlyph => (PeekedAsset ?? SelectedAsset)?.Placeholder ?? "";
    public string HeaderSummary => PeekedAsset is { } peeked ? peeked.Details : MediaSummary;

    partial void OnPeekedAssetChanged(AssetViewModel? value) => NotifyInstant();
    partial void OnInstantPreviewChanged(ImageSource? value) => NotifyInstant();
    partial void OnMediaSummaryChanged(string value) => OnPropertyChanged(nameof(HeaderSummary));
    partial void OnFollowFilmstripChanged(bool value)
    {
        if (!value) CommitPeek();
        Status = value
            ? "Preview follows scroll: the file under the filmstrip marker is previewed as you scroll."
            : "Preview follows scroll is off. Click a thumbnail to preview it.";
    }

    private void NotifyInstant()
    {
        foreach (var property in new[] { nameof(IsPeeking), nameof(ShowInstantLayer), nameof(InstantGlyph), nameof(HeaderSummary), nameof(SelectionLabel), nameof(ShowEmptyState) })
            OnPropertyChanged(property);
    }

    /// <summary>Shows <paramref name="item"/>'s thumbnail in the preview at once without opening the file. Cheap enough to call on every scroll step.</summary>
    public void PeekAsset(AssetViewModel? item)
    {
        if (item is null || ReferenceEquals(item, PeekedAsset))
            return;
        if (ReferenceEquals(item, SelectedAsset))
        {
            // Scrolled back onto the open file: drop the stand-in and show its real picture again.
            if (PeekedAsset is not null)
            {
                PeekedAsset = null;
                WatchInstantSource(item);
                InstantPreview = PreviewImage is null ? item.Thumbnail : null;
            }
            return;
        }
        if (ShowPlayback)
            PauseAtPlaybackPosition();
        MainTab = 1;
        WatchInstantSource(item);
        InstantPreview = item.Thumbnail;
        PeekedAsset = item;
    }

    /// <summary>Opens the peeked file for real. Called when scrolling has settled.</summary>
    public void CommitPeek()
    {
        if (PeekedAsset is not { } item)
            return;
        if (ReferenceEquals(item, SelectedAsset))
        {
            PeekedAsset = null;
            WatchInstantSource(null);
            if (PreviewImage is not null)
                InstantPreview = null;
            return;
        }
        SelectedAsset = item;
    }

    /// <summary>Called when the selection changes: the new file's thumbnail stands in until its own picture is ready.</summary>
    private void BeginInstantPreview(AssetViewModel? item)
    {
        WatchInstantSource(item);
        InstantPreview = item?.Thumbnail;
        PeekedAsset = null;
    }

    private void EndInstantPreview()
    {
        if (IsPeeking)
            return;
        WatchInstantSource(null);
        InstantPreview = null;
    }

    /// <summary>A thumbnail that finishes loading a moment later is picked up; one that is released as it scrolls away is kept.</summary>
    private void WatchInstantSource(AssetViewModel? item)
    {
        if (ReferenceEquals(instantSource, item))
            return;
        if (instantSource is not null) instantSource.PropertyChanged -= OnInstantSourceChanged;
        instantSource = item;
        if (instantSource is not null) instantSource.PropertyChanged += OnInstantSourceChanged;
    }

    private void OnInstantSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AssetViewModel.Thumbnail) && sender is AssetViewModel { Thumbnail: { } thumbnail } item
            && ReferenceEquals(item, instantSource) && (IsPeeking || PreviewImage is null))
            InstantPreview = thumbnail;
    }
}
