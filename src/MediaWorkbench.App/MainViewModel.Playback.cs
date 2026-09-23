using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

// The hand-over between the playing video (VLC's own surface) and exact still frames (decoded separately).
// Pausing must land on the very frame the player stopped on, and neither direction may flash a wrong picture.
public sealed partial class MainViewModel
{
    /// <summary>The longest the paused video picture is kept waiting for its still; after that the still area shows whatever it has, with its usual "decoding" note.</summary>
    private static readonly TimeSpan VideoSurfaceHoldLimit = TimeSpan.FromMilliseconds(2500);
    /// <summary>Frames compared on either side of the estimated pause position.</summary>
    private const int PauseSearchBefore = 3;
    private const int PauseSearchAfter = 4;

    private int pausedAtFrame = -1;
    private int liveBaseline;
    private int pauseVersion;
    private bool refiningPause;
    private long lastPlayerTimeMs;
    private long lastPlayerStamp;

    /// <summary>
    /// The player's position now. It only reports a new time about every quarter of a second, so the last report is carried
    /// forward by the time that has passed since, at the playing rate. Without this a pause could land several frames early.
    /// </summary>
    private long EstimatedPlayerTimeMs()
    {
        var stamp = lastPlayerStamp;
        if (stamp == 0 || !Player.IsPlaying)
            return Player.Time;
        return InterpolatePlayerTime(lastPlayerTimeMs, Stopwatch.GetElapsedTime(stamp).TotalMilliseconds, Player.Rate);
    }

    /// <summary>Carries a reported time forward; never by more than 600 ms, so a stalled player cannot run the estimate away.</summary>
    internal static long InterpolatePlayerTime(long reportedMs, double elapsedMs, float rate) =>
        reportedMs + (long)Math.Round(Math.Clamp(elapsedMs, 0, 600) * (rate > 0 ? rate : 1));

    /// <summary>
    /// Watches for the out marker between the player's own time reports. Those are a quarter of a second apart, so waiting for
    /// one always ran past the marker: a marked range would stop several frames late, and a short one could play past its end.
    /// </summary>
    private DispatcherTimer? rangeWatch;

    private void WatchForRangeEnd()
    {
        rangeWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Normal, (_, _) => CheckRangeEnd(), Application.Current.Dispatcher);
        rangeWatch.Start();
    }

    private void StopWatchingRangeEnd() => rangeWatch?.Stop();

    private void CheckRangeEnd()
    {
        if (disposed || !ShowPlayback || stopPlaybackAt is not { } stop)
        {
            StopWatchingRangeEnd();
            return;
        }
        if (EstimatedPlayerTimeMs() < stop)
            return;
        StopWatchingRangeEnd();
        FinishAtOutMarker();
    }

    /// <summary>
    /// Keeps the player's last picture on screen after pausing, until the exact still of that frame is ready to take its place.
    /// Both are drawn by the same surface, so the swap is invisible: the two pictures are the same frame.
    /// </summary>
    private void HoldVideoSurface()
    {
        live.Freeze();
        if (!IsVideo || holdingVideoSurface || LiveImage is null)
            return;
        holdingVideoSurface = true;
        var version = ++holdVersion;
        NotifyVideoSurface();
        _ = ReleaseLaterAsync(version);
    }

    /// <summary>Lets the still take over from the paused player picture. While the paused frame is still being identified only a forced release does.</summary>
    private void ReleaseVideoSurface(bool force = false)
    {
        if (!holdingVideoSurface || refiningPause && !force)
            return;
        holdingVideoSurface = false;
        holdVersion++;
        if (!ShowPlayback)
            LiveImage = null;
        NotifyVideoSurface();
    }

    private async Task ReleaseLaterAsync(int version)
    {
        try { await Task.Delay(VideoSurfaceHoldLimit, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (version == holdVersion)
            ReleaseVideoSurface(force: true);
    }

    /// <summary>A new picture from the player: once playback has produced its first picture, the preview shows the player instead of the still.</summary>
    private void OnLivePictureShown(object? sender, EventArgs args)
    {
        if (disposed || !ShowPlayback || live.PictureCount <= liveBaseline || live.Bitmap is not { } bitmap)
            return;
        if (!ReferenceEquals(LiveImage, bitmap))
            LiveImage = bitmap;
    }

    partial void OnLiveImageChanged(System.Windows.Media.Imaging.BitmapSource? value) => NotifyVideoSurface();

    private void NotifyVideoSurface()
    {
        OnPropertyChanged(nameof(ShowVideoSurface));
        OnPropertyChanged(nameof(ShowInstantLayer));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>
    /// Finds the frame the player really stopped on. The time estimate is good to a frame or two; the picture itself settles it:
    /// the paused picture, already in memory, is compared with the decoded frames around the estimate and the closest one wins.
    /// The paused picture stays on screen meanwhile, so what replaces it is the same frame. Any failure keeps the estimate.
    /// </summary>
    private async Task RefinePausedFrameAsync(AssetViewModel item, int estimate, Action<int>? settled = null)
    {
        var version = ++pauseVersion;
        refiningPause = true;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            limit.CancelAfter(VideoSurfaceHoldLimit - TimeSpan.FromMilliseconds(300));
            // Let a picture that was already on its way to the screen land, so the one compared is the one showing.
            await Task.Delay(40, limit.Token);
            var snapshot = live.Capture();
            if (snapshot is null || !StillPausedThere() || mediaInfo is not { } info)
                return;
            var selectedEngine = engine;
            var asset = item.Asset;
            var index = frames;
            var first = Math.Max(0, estimate - PauseSearchBefore);
            var last = Math.Min(index.Count - 1, estimate + PauseSearchAfter);
            var best = await Task.Run(async () =>
            {
                var candidates = new List<(int Frame, BitmapSource Image)>();
                for (var frame = first; frame <= last; frame++)
                {
                    var bytes = await selectedEngine.GetFrameAsync(asset, frame, index, info, limit.Token);
                    candidates.Add((frame, DecodeImage(bytes, FrameMatcher.CompareWidth * 2)));
                }
                return FrameMatcher.Best(snapshot, candidates, estimate);
            }, limit.Token);
            if (!StillPausedThere())
                return;
            pausedAtFrame = best;
            if (best != estimate)
                settled?.Invoke(best);
            if (best != CurrentFrame)
                CurrentFrame = best;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or ArgumentException) { }
        finally
        {
            if (version == pauseVersion)
            {
                refiningPause = false;
                if (frameCancellation is null && DisplayedFrame == CurrentFrame)
                    ReleaseVideoSurface();
            }
        }

        bool StillPausedThere() => version == pauseVersion && loadedAsset == item && !ShowPlayback && CurrentFrame == estimate;
    }
}

/// <summary>Tells which of a few decoded frames a picture shows, by comparing small grey versions of them.</summary>
internal static class FrameMatcher
{
    public const int CompareWidth = 128;

    /// <summary>
    /// The candidate closest to <paramref name="picture"/>. Overall brightness is taken out first, because the player and the decoder
    /// do not render levels identically. When several candidates are as good as each other (a still scene), the one nearest
    /// <paramref name="estimate"/> wins, which is then indistinguishable anyway.
    /// </summary>
    public static int Best(BitmapSource picture, IReadOnlyList<(int Frame, BitmapSource Image)> candidates, int estimate)
    {
        if (candidates.Count == 0)
            return estimate;
        var height = Math.Max(8, (int)Math.Round(CompareWidth * (double)picture.PixelHeight / picture.PixelWidth));
        var target = Grey(picture, CompareWidth, height);
        var scores = candidates.Select(candidate => (candidate.Frame, Score: Distance(target, Grey(candidate.Image, CompareWidth, height)))).ToList();
        var best = scores.Min(score => score.Score);
        return scores.Where(score => score.Score <= best * 1.03 + 0.15).MinBy(score => Math.Abs(score.Frame - estimate)).Frame;
    }

    private static double[] Grey(BitmapSource image, int width, int height)
    {
        var scaled = new TransformedBitmap(image, new ScaleTransform(width / (double)image.PixelWidth, height / (double)image.PixelHeight));
        var grey = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        var stride = grey.PixelWidth;
        var pixels = new byte[stride * grey.PixelHeight];
        grey.CopyPixels(pixels, stride, 0);
        // Every picture is sampled on the same grid, whatever rounding did to its scaled size, so values line up one to one.
        var columns = width - 2;
        var rows = height - 2;
        var values = new double[columns * rows];
        double sum = 0;
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
                sum += values[y * columns + x] = pixels[Math.Min(y, grey.PixelHeight - 1) * stride + Math.Min(x, grey.PixelWidth - 1)];
        var mean = values.Length == 0 ? 0 : sum / values.Length;
        for (var index = 0; index < values.Length; index++)
            values[index] -= mean;
        return values;
    }

    private static double Distance(double[] left, double[] right)
    {
        var count = Math.Min(left.Length, right.Length);
        if (count == 0)
            return double.MaxValue;
        double total = 0;
        for (var index = 0; index < count; index++)
            total += Math.Abs(left[index] - right[index]);
        return total / count;
    }
}
