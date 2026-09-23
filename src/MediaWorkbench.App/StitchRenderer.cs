using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

/// <summary>Draws a stitch plan. The preview and the exported file go through here, so what is saved is what was shown.</summary>
internal static class StitchRenderer
{
    /// <summary>Widest preview drawn on screen; the export is always at full size.</summary>
    public const int PreviewWidth = 1400;

    public static BitmapSource? Render(StitchPlan plan, IReadOnlyList<BitmapSource?> pictures, Color background, double scale = 1)
    {
        if (plan.IsEmpty)
            return null;
        var width = Math.Max(1, (int)Math.Round(plan.Width * scale));
        var height = Math.Max(1, (int)Math.Round(plan.Height * scale));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
            foreach (var placement in plan.Placements)
            {
                if (placement.Index >= pictures.Count || pictures[placement.Index] is not { } picture)
                    continue;
                var area = new Rect(placement.X * scale, placement.Y * scale, placement.Width * scale, placement.Height * scale);
                context.DrawImage(picture, area);
            }
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>The scale a preview is drawn at: never enlarged, and never wider than the preview area.</summary>
    public static double PreviewScale(StitchPlan plan) => plan.IsEmpty ? 1 : Math.Min(1, PreviewWidth / (double)plan.Width);

    public static byte[] Encode(BitmapSource picture, bool asJpeg)
    {
        BitmapEncoder encoder = asJpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
