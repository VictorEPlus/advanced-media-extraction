using Avalonia;
using Avalonia.Headless.XUnit;
using MediaWorkbench.Avalonia.Services;
using SkiaSharp;

namespace MediaWorkbench.Avalonia.Tests;

/// <summary>Phone photos store pixels sideways with an EXIF note saying how to turn them; the app must show them upright.</summary>
public sealed class PhotoDecoderTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "MediaWorkbench.Avalonia.Tests", Guid.NewGuid().ToString("N"));

    public PhotoDecoderTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
    }

    /// <summary>A 40 x 20 JPEG, left half red and right half blue, marked with the given EXIF orientation.</summary>
    private string MakeJpeg(ushort orientation)
    {
        using var bitmap = new SKBitmap(40, 20);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            canvas.DrawRect(0, 0, 20, 20, new SKPaint { Color = SKColors.Red });
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        var jpeg = data.ToArray();
        // APP1 "Exif": a little-endian TIFF header and one IFD entry, 0x0112 Orientation, SHORT, 1, value.
        byte[] tiff = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        byte[] header = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0];
        var length = 2 + header.Length + tiff.Length;
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. header, .. tiff];
        var path = Path.Combine(folder, $"turned{orientation}.jpg");
        File.WriteAllBytes(path, [.. jpeg[..2], .. app1, .. jpeg[2..]]);
        return path;
    }

    [AvaloniaFact]
    public void APhotoTurnedNinetyDegreesIsShownUprightWithItsUprightSize()
    {
        var photo = PhotoDecoder.Decode(MakeJpeg(6), 4000);
        Assert.NotNull(photo);
        Assert.Equal((20, 40), (photo!.Width, photo.Height));
        Assert.Equal(new PixelSize(20, 40), photo.Bitmap.PixelSize);
        // Turned a quarter clockwise: the red left half ends up on top.
        var top = Pixel(photo.Bitmap, 10, 5);
        var bottom = Pixel(photo.Bitmap, 10, 35);
        Assert.True(top.Red > 180 && top.Blue < 90, $"Top should be red, not {top}.");
        Assert.True(bottom.Blue > 180 && bottom.Red < 90, $"Bottom should be blue, not {bottom}.");
    }

    [AvaloniaFact]
    public void APhotoWithoutATurnKeepsItsShapeAndIsNeverScaledUp()
    {
        var photo = PhotoDecoder.Decode(MakeJpeg(1), 4000);
        Assert.Equal(new PixelSize(40, 20), photo!.Bitmap.PixelSize);
        var small = PhotoDecoder.Decode(MakeJpeg(1), 10);
        Assert.Equal(new PixelSize(10, 5), small!.Bitmap.PixelSize);
        Assert.Equal((40, 20), (small.Width, small.Height));
    }

    private static unsafe (byte Red, byte Green, byte Blue) Pixel(global::Avalonia.Media.Imaging.Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        fixed (byte* pointer = buffer)
            bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), (IntPtr)pointer, buffer.Length, bitmap.PixelSize.Width * 4);
        var index = (y * bitmap.PixelSize.Width + x) * 4;
        return (buffer[index + 2], buffer[index + 1], buffer[index]);
    }
}
