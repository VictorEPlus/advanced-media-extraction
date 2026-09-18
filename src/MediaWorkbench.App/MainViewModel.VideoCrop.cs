using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

// Crop and rotate for a whole video. Unlike the clipboard crop this one is exported: it applies to
// "Export whole video" and to "Trim selection to MP4" for as long as the video stays selected.
public sealed partial class MainViewModel
{
    /// <summary>The edge-adjusting mode is on: the preview shows grabbers on the four edges and the picture turned as it will be exported.</summary>
    [ObservableProperty] private bool isFraming;
    /// <summary>Crop in source pixels, before rotation. Null means the whole frame.</summary>
    [ObservableProperty] private PixelCrop? videoCrop;
    /// <summary>Clockwise quarter turns, in degrees: 0, 90, 180 or 270.</summary>
    [ObservableProperty] private int videoRotation;
    [ObservableProperty] private CropEdge selectedCropEdge;
    [ObservableProperty] private bool isAutoFitting;

    private (int Width, int Height) FrameSize => IsVideo && PreviewImage is BitmapSource image ? (image.PixelWidth, image.PixelHeight) : (0, 0);
    private VideoTransform CurrentTransform => new(VideoCrop, VideoRotation);

    public bool CanFrame => IsVideo && FrameSize.Width > 0;
    public bool HasVideoTransform => CanFrame && !CurrentTransform.IsIdentity(FrameSize.Width, FrameSize.Height);
    public bool ShowFramingTools => IsFraming && IsVideo;
    public bool CanAutoFit => !IsAutoFitting;
    /// <summary>Shown under the preview when a crop or rotation is set but the tools are closed, so it is never applied by surprise.</summary>
    public bool ShowTransformReminder => HasVideoTransform && !IsFraming;
    public string TrimLabel => HasVideoTransform ? "Trim selection to MP4, cropped/rotated" : "Trim selection to MP4";
    public string FramingEdgeHint => SelectedCropEdge == CropEdge.None
        ? "Drag a grabber, or click one and use the arrow keys: 1 pixel, Shift 10. Tab goes to the next edge."
        : $"{SelectedCropEdge} edge selected: arrow keys move it 1 pixel, Shift 10. Tab next edge, Esc lets go.";

    public string FramingSummary
    {
        get
        {
            var (width, height) = FrameSize;
            if (width <= 0)
                return "";
            var transform = CurrentTransform;
            if (transform.IsIdentity(width, height))
                return $"Whole frame, {width:N0} × {height:N0}. Nothing to export yet.";
            var crop = transform.EffectiveCrop(width, height);
            var output = transform.OutputSize(width, height);
            var parts = new List<string>();
            if (crop != new PixelCrop(0, 0, width, height))
                parts.Add($"cut left {crop.X:N0}, top {crop.Y:N0}, right {width - crop.X - crop.Width:N0}, bottom {height - crop.Y - crop.Height:N0}");
            if (VideoTransform.NormalizeRotation(VideoRotation) is var turn and not 0)
                parts.Add($"turned {turn}° clockwise");
            return $"Exports {output.Width:N0} × {output.Height:N0} ({MediaDimensions.DescribeAspect(output.Width, output.Height)}): {string.Join(", ", parts)}";
        }
    }

    partial void OnVideoCropChanged(PixelCrop? value) => NotifyFraming();
    partial void OnVideoRotationChanged(int value) { SelectedCropEdge = CropEdge.None; NotifyFraming(); }
    partial void OnIsAutoFittingChanged(bool value) => OnPropertyChanged(nameof(CanAutoFit));
    partial void OnSelectedCropEdgeChanged(CropEdge value) => OnPropertyChanged(nameof(FramingEdgeHint));
    partial void OnIsFramingChanged(bool value)
    {
        if (value)
        {
            // One way of marking the picture at a time, and a still frame to mark it on.
            IsCropping = false;
            CropSelection = null;
            if (ShowPlayback) { PauseAtPlaybackPosition(); _ = SeekFrameAsync(); }
            Status = "Crop and rotate: drag the edge grabbers or press Auto-fit edges. The original video is never changed.";
        }
        else SelectedCropEdge = CropEdge.None;
        NotifyFraming();
    }

    private void NotifyFraming()
    {
        UpdatePortraitLayout();
        foreach (var property in new[] { nameof(CanFrame), nameof(HasVideoTransform), nameof(ShowFramingTools), nameof(ShowTransformReminder), nameof(TrimLabel), nameof(FramingSummary), nameof(FramingEdgeHint) })
            OnPropertyChanged(property);
    }

    private void ResetFraming()
    {
        IsFraming = false;
        VideoCrop = null;
        VideoRotation = 0;
        IsAutoFitting = false;
    }

    [RelayCommand]
    private void ToggleFraming() => Guard(() =>
    {
        if (!IsFraming && !CanFrame)
            throw new InvalidOperationException("Select a video and wait for its first frame.");
        IsFraming = !IsFraming;
    });

    [RelayCommand]
    private void RotateVideoRight() => VideoRotation = VideoTransform.NormalizeRotation(VideoRotation + 90);

    [RelayCommand]
    private void RotateVideoLeft() => VideoRotation = VideoTransform.NormalizeRotation(VideoRotation - 90);

    [RelayCommand]
    private void ResetVideoTransform()
    {
        VideoCrop = null;
        VideoRotation = 0;
        SelectedCropEdge = CropEdge.None;
        Status = "Crop and rotation cleared.";
    }

    /// <summary>Finds the picture inside black bars and puts the four edges on it.</summary>
    // Re-entry is guarded by IsAutoFitting, which also drives the button, so the command itself never disables.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AutoFitCropAsync()
    {
        if (IsAutoFitting || SelectedAsset is not { } item || mediaInfo is not { } info || FrameSize is not { Width: > 0 } size)
            return;
        IsAutoFitting = true;
        Status = "Looking for black borders at several moments of the video…";
        try
        {
            var selectedEngine = engine;
            var found = await Task.Run(() => selectedEngine.DetectContentBoundsAsync(item.Asset, info, size.Width, size.Height, lifetime.Token), lifetime.Token);
            if (!ReferenceEquals(SelectedAsset, item))
                return;
            if (found is null)
                Status = "No picture area could be detected. Adjust the edges by hand.";
            else if (found == new PixelCrop(0, 0, size.Width, size.Height))
            {
                VideoCrop = null;
                Status = "No black borders found: the picture already fills the frame.";
            }
            else
            {
                VideoCrop = found;
                Status = $"Edges fitted to the picture: {found.Width:N0} × {found.Height:N0}. Step through the video to check, then adjust any edge.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError(exception); }
        finally { IsAutoFitting = false; }
    }

    [RelayCommand]
    private void ExportWholeVideo() => Guard(() =>
    {
        var asset = SelectedAsset?.Asset ?? throw new InvalidOperationException("Select a video first.");
        var info = mediaInfo ?? throw new InvalidOperationException("Wait for the media to load.");
        var (width, height) = FrameSize;
        if (!HasVideoTransform)
            throw new InvalidOperationException("Set a crop or a rotation first; there is nothing to change.");
        var transform = CurrentTransform;
        var selectedEngine = engine;
        var directory = settings.ExportDirectory;
        var output = transform.OutputSize(width, height);
        QueueExport($"Whole video {output.Width} × {output.Height} from {asset.Name}", (progress, token) => selectedEngine.ExportTransformedVideoAsync(asset, info, transform, width, height, directory, progress, token));
    });
}
