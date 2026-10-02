using Avalonia.Media.Imaging;
using MediaWorkbench.Core;
using SkiaSharp;

namespace MediaWorkbench.Avalonia;

/// <summary>The picture sizes by the names the shared logic uses (WPF's), so it reads the same in both apps.</summary>
internal static class BitmapSizes
{
    extension(Bitmap bitmap)
    {
        public int PixelWidth => bitmap.PixelSize.Width;
        public int PixelHeight => bitmap.PixelSize.Height;
    }
}

/// <summary>Small image operations the logic needs: a grey copy at a small size for comparisons, and a crop.</summary>
internal static class PictureOps
{
    /// <summary>The picture scaled to exactly <paramref name="width"/> × <paramref name="height"/> and turned grey, one byte per pixel, row by row.</summary>
    public static byte[] Grey(Bitmap image, int width, int height)
    {
        using var source = Pixels.ToSkia(image);
        using var scaled = source.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidOperationException("The picture could not be scaled.");
        var grey = new byte[width * height];
        var bytes = scaled.Bytes;
        var rowBytes = scaled.RowBytes;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = y * rowBytes + x * 4;
                grey[y * width + x] = (byte)Math.Clamp((int)Math.Round(0.114 * bytes[at] + 0.587 * bytes[at + 1] + 0.299 * bytes[at + 2]), 0, 255);
            }
        return grey;
    }

    /// <summary>The pixels inside <paramref name="crop"/>, as a new picture of that size.</summary>
    public static Bitmap Crop(Bitmap image, PixelCrop crop)
    {
        using var source = Pixels.ToSkia(image);
        using var cut = new SKBitmap(new SKImageInfo(crop.Width, crop.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!source.ExtractSubset(cut, new SKRectI(crop.X, crop.Y, crop.X + crop.Width, crop.Y + crop.Height)))
            throw new InvalidOperationException("The crop area is outside the picture.");
        using var copy = cut.Copy();
        return Pixels.FromSkia(copy);
    }
}
