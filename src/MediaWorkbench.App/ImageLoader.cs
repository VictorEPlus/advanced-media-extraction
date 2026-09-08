using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaWorkbench.App;

internal sealed record PhotoPreview(BitmapSource Image, IReadOnlyDictionary<string, string> Metadata);

internal static class ImageLoader
{
    public static PhotoPreview Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var metadata = new Dictionary<string, string>
        {
            ["Format"] = decoder.CodecInfo?.FriendlyName ?? Path.GetExtension(path),
            ["Bit depth"] = frame.Format.BitsPerPixel.ToString(),
            ["DPI"] = $"{frame.DpiX:0.##} x {frame.DpiY:0.##}",
            ["Source dimensions"] = $"{frame.PixelWidth} x {frame.PixelHeight}"
        };
        var orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata embedded)
            {
                orientation = GetOrientation(embedded);
                Add(metadata, "Camera model", embedded.CameraModel);
                Add(metadata, "Camera maker", embedded.CameraManufacturer);
                Add(metadata, "Date taken", embedded.DateTaken);
                Add(metadata, "Title", embedded.Title);
                Add(metadata, "Author", embedded.Author is { } authors ? string.Join(", ", authors) : null);
                Add(metadata, "Keywords", embedded.Keywords is { } keywords ? string.Join(", ", keywords) : null);
                Add(metadata, "Copyright", embedded.Copyright);
                foreach (var pair in new Dictionary<string, string> { ["Exposure"] = "/app1/ifd/exif/{ushort=33434}", ["Aperture"] = "/app1/ifd/exif/{ushort=33437}", ["ISO"] = "/app1/ifd/exif/{ushort=34855}", ["Lens"] = "/app1/ifd/exif/{ushort=42036}" })
                    if (embedded.GetQuery(pair.Value) is { } raw)
                        Add(metadata, pair.Key, raw is ulong rational ? $"{(uint)rational}/{(uint)(rational >> 32)}" : raw.ToString());
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException) { }
        BitmapSource image = ApplyOrientation(frame, orientation);
        image = new WriteableBitmap(image);
        image.Freeze();
        return new PhotoPreview(image, metadata);
    }

    private static BitmapSource ApplyOrientation(BitmapSource image, int orientation)
    {
        var transform = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0),
            3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0),
            7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0),
            _ => Matrix.Identity
        };
        if (!transform.IsIdentity) image = new TransformedBitmap(image, new MatrixTransform(transform));
        return image;
    }

    public static BitmapSource Thumbnail(string path, int width)
    {
        var orientation = 1;
        int decodeWidth;
        int decodeHeight;
        {
            using var metadataStream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(metadataStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            var scale = Math.Min(1, (double)width / Math.Max(frame.PixelWidth, frame.PixelHeight));
            decodeWidth = Math.Max(1, (int)Math.Floor(frame.PixelWidth * scale));
            decodeHeight = Math.Max(1, (int)Math.Floor(frame.PixelHeight * scale));
            if (new[] { ".jpg", ".jpeg", ".tif", ".tiff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                && frame.Metadata is BitmapMetadata metadata) orientation = GetOrientation(metadata);
        }
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.DecodePixelHeight = decodeHeight;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        var image = ApplyOrientation(bitmap, orientation);
        image.Freeze();
        return image;
    }

    private static int GetOrientation(BitmapMetadata metadata)
    {
        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try { if (metadata.GetQuery(query) is ushort value) return value; }
            catch (Exception exception) when (exception is NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException) { }
        }
        return 1;
    }

    public static byte[] Encode(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Add(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) metadata[key] = value;
    }
}
