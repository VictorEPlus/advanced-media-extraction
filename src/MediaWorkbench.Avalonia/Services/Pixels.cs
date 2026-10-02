using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace MediaWorkbench.Avalonia;

/// <summary>Moves pixels between Avalonia bitmaps and Skia, which does the decoding, encoding and drawing off the UI thread.</summary>
public static class Pixels
{
    /// <summary>The pixels as premultiplied BGRA, whatever order the bitmap keeps them in (a rendered window is RGBA, for one).</summary>
    public static unsafe SKBitmap ToSkia(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var colorType = bitmap.Format == PixelFormat.Rgba8888 ? SKColorType.Rgba8888 : SKColorType.Bgra8888;
        var alphaType = bitmap.AlphaFormat switch { AlphaFormat.Unpremul => SKAlphaType.Unpremul, AlphaFormat.Opaque => SKAlphaType.Opaque, _ => SKAlphaType.Premul };
        var result = new SKBitmap(new SKImageInfo(size.Width, size.Height, colorType, alphaType));
        bitmap.CopyPixels(new PixelRect(size), result.GetPixels(), result.ByteCount, result.RowBytes);
        if (colorType == SKColorType.Bgra8888 && alphaType == SKAlphaType.Premul)
            return result;
        using (result)
        {
            var converted = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(converted);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(result, 0, 0);
            return converted;
        }
    }

    public static unsafe WriteableBitmap FromSkia(SKBitmap bitmap)
    {
        using var converted = bitmap.ColorType == SKColorType.Bgra8888 && bitmap.AlphaType == SKAlphaType.Premul ? null
            : bitmap.Copy(SKColorType.Bgra8888);
        var source = converted ?? bitmap;
        var target = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = target.Lock();
        var from = (byte*)source.GetPixels();
        for (var row = 0; row < source.Height; row++)
            Buffer.MemoryCopy(from + (long)row * source.RowBytes, (byte*)buffer.Address + (long)row * buffer.RowBytes, buffer.RowBytes, Math.Min(source.RowBytes, buffer.RowBytes));
        return target;
    }

    /// <summary>A copy that does not change when the original does (the live video picture is rewritten in place).</summary>
    public static WriteableBitmap Copy(Bitmap bitmap)
    {
        using var skia = ToSkia(bitmap);
        return FromSkia(skia);
    }

    public static byte[] EncodePng(Bitmap bitmap) => Encode(bitmap, SKEncodedImageFormat.Png, 100);

    public static byte[] Encode(Bitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var skia = ToSkia(bitmap);
        using var data = skia.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>Decodes PNG or JPEG bytes (frames from FFmpeg), optionally no wider than <paramref name="decodeWidth"/>.</summary>
    public static Bitmap Decode(byte[] bytes, int decodeWidth = 0)
    {
        using var memory = new MemoryStream(bytes);
        return decodeWidth > 0 ? Bitmap.DecodeToWidth(memory, decodeWidth, BitmapInterpolationMode.MediumQuality) : new Bitmap(memory);
    }

    /// <summary>Copies the pixels out as BGRA bytes, row by row, for comparisons.</summary>
    public static byte[] Bytes(Bitmap bitmap, out int stride)
    {
        using var skia = ToSkia(bitmap);
        stride = skia.Width * 4;
        var buffer = new byte[stride * skia.Height];
        for (var row = 0; row < skia.Height; row++)
            System.Runtime.InteropServices.Marshal.Copy(skia.GetPixels() + row * skia.RowBytes, buffer, row * stride, stride);
        return buffer;
    }
}
