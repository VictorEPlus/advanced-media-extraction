using Avalonia.Media.Imaging;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.FileType;
using MetadataExtractor.Formats.Jpeg;
using MediaWorkbench.Avalonia.Services;
using SkiaSharp;

namespace MediaWorkbench.Avalonia;

internal sealed record PhotoPreview(Bitmap Image, IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Opens photos: the picture upright at full size (Skia, EXIF orientation applied), with the same detail rows the WPF app showed
/// (format, bit depth, DPI, size, camera, date taken, exposure, aperture, ISO, lens, title, author, keywords, copyright).
/// </summary>
internal static class ImageLoader
{
    public static PhotoPreview Load(string path)
    {
        var photo = PhotoDecoder.Decode(path, int.MaxValue) ?? throw new NotSupportedException("This picture format cannot be read directly.");
        var metadata = new Dictionary<string, string>
        {
            ["Format"] = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            ["Source dimensions"] = $"{photo.Width} x {photo.Height}"
        };
        try { ReadDetails(path, metadata); }
        catch (Exception exception) when (exception is IOException or ImageProcessingException or MetadataException or ArgumentException or NotSupportedException) { }
        return new PhotoPreview(photo.Bitmap, metadata);
    }

    private static void ReadDetails(string path, Dictionary<string, string> metadata)
    {
        var directories = ImageMetadataReader.ReadMetadata(path);
        if (directories.OfType<FileTypeDirectory>().FirstOrDefault()?.GetDescription(FileTypeDirectory.TagDetectedFileTypeLongName) is { Length: > 0 } format)
            metadata["Format"] = format;
        using (var stream = File.OpenRead(path))
        using (var codec = SKCodec.Create(stream))
            if (codec is not null)
                metadata["Bit depth"] = (codec.Info.BytesPerPixel * 8).ToString();
        var main = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var exif = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (main is not null && main.TryGetDouble(ExifDirectoryBase.TagXResolution, out var dpiX) && main.TryGetDouble(ExifDirectoryBase.TagYResolution, out var dpiY))
            metadata["DPI"] = $"{dpiX:0.##} x {dpiY:0.##}";
        else
            metadata["DPI"] = "96 x 96";
        Add(metadata, "Camera model", main?.GetDescription(ExifDirectoryBase.TagModel));
        Add(metadata, "Camera maker", main?.GetDescription(ExifDirectoryBase.TagMake));
        if (exif is not null && exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken))
            metadata["Date taken"] = taken.ToString("G");
        else if (main is not null && main.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var stamped))
            metadata["Date taken"] = stamped.ToString("G");
        Add(metadata, "Title", main?.GetDescription(ExifDirectoryBase.TagImageDescription));
        Add(metadata, "Author", main?.GetDescription(ExifDirectoryBase.TagArtist));
        Add(metadata, "Keywords", main?.GetDescription(ExifDirectoryBase.TagWinKeywords));
        Add(metadata, "Copyright", main?.GetDescription(ExifDirectoryBase.TagCopyright));
        Add(metadata, "Exposure", exif?.GetDescription(ExifDirectoryBase.TagExposureTime));
        Add(metadata, "Aperture", exif?.GetDescription(ExifDirectoryBase.TagFNumber));
        Add(metadata, "ISO", exif?.GetDescription(ExifDirectoryBase.TagIsoEquivalent));
        Add(metadata, "Lens", exif?.GetDescription(ExifDirectoryBase.TagLensModel));
        if (directories.OfType<JpegDirectory>().FirstOrDefault() is { } jpeg && jpeg.TryGetInt32(JpegDirectory.TagDataPrecision, out var precision)
            && jpeg.TryGetInt32(JpegDirectory.TagNumberOfComponents, out var components))
            metadata["Bit depth"] = (precision * components).ToString();
    }

    /// <summary>A small upright picture for the filmstrip and the folder covers, no wider or taller than <paramref name="size"/>.</summary>
    public static Bitmap Thumbnail(string path, int size) =>
        PhotoDecoder.Decode(path, size)?.Bitmap ?? throw new NotSupportedException("This picture format cannot be read directly.");

    public static byte[] Encode(Bitmap image) => Pixels.EncodePng(image);

    private static void Add(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) metadata[key] = value.Trim();
    }
}
