using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia.Tests;

internal static partial class DesktopSmokeTest
{
    /// <summary>
    /// Pausing has to land on the frame the player stopped on. A hidden window cannot play video, so the two parts that decide
    /// the frame are checked on their own: carrying the player's coarse clock forward, and recognising a frame from a small,
    /// differently rendered picture of it. Expects a video to be selected.
    /// </summary>
    private static async Task CheckPauseMatchingAsync(MainViewModel model, string dataDirectory, ToolPaths tools, CancellationToken token)
    {
        Require(MainViewModel.InterpolatePlayerTime(1000, 180, 1) == 1180, "The player's last reported time should be carried forward by the time since.");
        Require(MainViewModel.InterpolatePlayerTime(1000, 180, 0.5f) == 1090, "Carrying the clock forward must respect the playing rate.");
        Require(MainViewModel.InterpolatePlayerTime(1000, 5000, 1) == 1600 && MainViewModel.InterpolatePlayerTime(1000, -20, 1) == 1000, "A stalled player must not run the estimate away.");

        var asset = model.SelectedAsset!.Asset;
        var engine = new MediaEngine(tools, Path.Combine(dataDirectory, "cache"));
        var info = await engine.ProbeAsync(asset.FullPath, token);
        var frames = await engine.IndexFramesAsync(asset, info, token);
        var candidates = new List<(int Frame, Bitmap Image)>();
        for (var frame = 0; frame < frames.Count; frame++)
            candidates.Add((frame, MainViewModel.DecodeImage(await engine.GetFrameAsync(asset, frame, frames, info, token), FrameMatcher.CompareWidth * 2)));

        foreach (var shown in new[] { 0, 2, 4, 5 })
        {
            // What a player snapshot is like: small, and with its levels rendered a little differently from the decoder's.
            var full = MainViewModel.DecodeImage(await engine.GetFrameAsync(asset, shown, frames, info, token));
            var picture = Relevel(full, 320, 0.92, 14);
            var estimate = Math.Clamp(shown + (shown % 2 == 0 ? 2 : -2), 0, frames.Count - 1);
            var found = FrameMatcher.Best(picture, candidates, estimate);
            Require(found == shown, $"A paused picture of frame {shown} should be recognised as frame {shown}, not {found}, even when the clock estimate says {estimate}.");
        }

        // A scene that does not change: every frame matches equally, so the clock estimate decides.
        var still = candidates.Select(candidate => (candidate.Frame, candidates[0].Image)).ToList();
        Require(FrameMatcher.Best(candidates[0].Image, still, 3) == 3, "When the frames look the same, the estimate from the clock should stand.");
    }

    /// <summary>A copy at <paramref name="width"/> pixels wide with its levels changed, as the player renders them slightly differently from the decoder.</summary>
    private static Bitmap Relevel(Bitmap source, int width, double gain, int lift)
    {
        using var full = Pixels.ToSkia(source);
        var height = (int)Math.Round(width * (double)source.PixelSize.Height / source.PixelSize.Width);
        using var scaled = full.Resize(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear))!;
        var pixels = scaled.Bytes;
        for (var index = 0; index < pixels.Length; index++)
            if (index % 4 != 3)
                pixels[index] = (byte)Math.Clamp(pixels[index] * gain + lift, 0, 255);
        using var result = new SkiaSharp.SKBitmap(scaled.Info);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, result.GetPixels(), pixels.Length);
        return Pixels.FromSkia(result);
    }
}
