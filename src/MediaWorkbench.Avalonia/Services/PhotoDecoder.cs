using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace MediaWorkbench.Avalonia.Services;

/// <summary>
/// Decodes photos the way a camera meant them: turned upright by the EXIF orientation phones write instead of rotating the pixels,
/// and no wider (or taller) than asked for. JPEGs are scaled down while they are decoded, which is far quicker than decoding them whole and
/// shrinking afterwards. Returns null for a file Skia cannot read, so the caller can hand it to FFmpeg.
/// </summary>
public static class PhotoDecoder
{
    /// <summary>A decoded photo, and the photo's own upright size (the bitmap may be smaller).</summary>
    public sealed record Photo(Bitmap Bitmap, int Width, int Height);

    public static Photo? Decode(string path, int maximumWidth, int maximumHeight = int.MaxValue)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;
        var origin = codec.EncodedOrigin;
        var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var source = codec.Info;
        // The width that matters is the upright one: for a turned photo that is the stored height.
        var uprightWidth = turned ? source.Height : source.Width;
        var uprightHeight = turned ? source.Width : source.Height;
        var scale = Math.Min(1f, Math.Min(maximumWidth / (float)uprightWidth, maximumHeight / (float)uprightHeight));
        var sampled = codec.GetScaledDimensions(scale);
        var info = new SKImageInfo(sampled.Width, sampled.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return null;
        // The codec only scales in steps (1/2, 1/4, 1/8 for JPEG); the rest is a high-quality resize.
        var targetWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var sized = decoded.Width > targetWidth + 1 || decoded.Height > targetHeight + 1
            ? decoded.Resize(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? decoded.Copy()
            : decoded.Copy();
        using var upright = Orient(sized, origin);
        return new Photo(ToAvalonia(upright), uprightWidth, uprightHeight);
    }

    /// <summary>Applies the EXIF orientation: the eight ways a camera can store a picture, as a turn and possibly a mirror.</summary>
    private static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return bitmap.Copy();
        var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(new SKImageInfo(turned ? bitmap.Height : bitmap.Width, turned ? bitmap.Width : bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        var (width, height) = (bitmap.Width, bitmap.Height);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Scale(-1, 1, width / 2f, 0); break;
            case SKEncodedOrigin.BottomRight: canvas.RotateDegrees(180, width / 2f, height / 2f); break;
            case SKEncodedOrigin.BottomLeft: canvas.Scale(1, -1, 0, height / 2f); break;
            case SKEncodedOrigin.LeftTop: canvas.Translate(height, 0); canvas.RotateDegrees(90); canvas.Scale(1, -1); canvas.Translate(0, -height); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(height, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(0, width); canvas.RotateDegrees(-90); canvas.Scale(1, -1); canvas.Translate(0, -height); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, width); canvas.RotateDegrees(-90); break;
        }
        canvas.DrawBitmap(bitmap, 0, 0);
        canvas.Flush();
        return result;
    }

    private static unsafe Bitmap ToAvalonia(SKBitmap bitmap)
    {
        var target = new WriteableBitmap(new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = target.Lock();
        var rowBytes = bitmap.RowBytes;
        var source = (byte*)bitmap.GetPixels();
        for (var row = 0; row < bitmap.Height; row++)
            Buffer.MemoryCopy(source + (long)row * rowBytes, (byte*)buffer.Address + (long)row * buffer.RowBytes, buffer.RowBytes, Math.Min(rowBytes, buffer.RowBytes));
        return target;
    }
}
