using Avalonia.Media;
using Avalonia.Media.Imaging;
using MediaWorkbench.Core;
using SkiaSharp;

namespace MediaWorkbench.Avalonia;

/// <summary>Draws a stitch plan. The preview and the exported file go through here, so what is saved is what was shown.</summary>
internal static class StitchRenderer
{
    /// <summary>Widest preview drawn on screen; the export is always at full size.</summary>
    public const int PreviewWidth = 1400;

    public static Bitmap? Render(StitchPlan plan, IReadOnlyList<Bitmap?> pictures, Color background, double scale = 1)
    {
        if (plan.IsEmpty)
            return null;
        var width = Math.Max(1, (int)Math.Round(plan.Width * scale));
        var height = Math.Max(1, (int)Math.Round(plan.Height * scale));
        using var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(new SKColor(background.R, background.G, background.B, background.A));
            using var paint = new SKPaint { IsAntialias = true };
            foreach (var placement in plan.Placements)
            {
                if (placement.Index >= pictures.Count || pictures[placement.Index] is not { } picture)
                    continue;
                using var source = Pixels.ToSkia(picture);
                using var image = SKImage.FromBitmap(source);
                var area = new SKRect((float)(placement.X * scale), (float)(placement.Y * scale), (float)((placement.X + placement.Width) * scale), (float)((placement.Y + placement.Height) * scale));
                canvas.DrawImage(image, area, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            }
        }
        return Pixels.FromSkia(target);
    }

    /// <summary>The scale a preview is drawn at: never enlarged, and never wider than the preview area.</summary>
    public static double PreviewScale(StitchPlan plan) => plan.IsEmpty ? 1 : Math.Min(1, PreviewWidth / (double)plan.Width);

    public static byte[] Encode(Bitmap picture, bool asJpeg) =>
        Pixels.Encode(picture, asJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, asJpeg ? 92 : 100);
}
