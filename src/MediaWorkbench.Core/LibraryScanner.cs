namespace MediaWorkbench.Core;

public sealed class LibraryScanner
{
    private static readonly Dictionary<string, MediaKind> Extensions = BuildExtensions();

    public static MediaAsset? ReadFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!Extensions.TryGetValue(Path.GetExtension(path), out var kind) || !File.Exists(path) || IsMacSidecar(path)) return null;
        var file = new FileInfo(path);
        return new MediaAsset(Path.GetDirectoryName(path)!, file.Name, kind, file.Length, file.LastWriteTimeUtc.Ticks);
    }

    public IEnumerable<MediaAsset> Scan(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Media folder not found: {root}");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            ReturnSpecialDirectories = false
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Extensions.TryGetValue(Path.GetExtension(path), out var kind) || IsMacSidecar(path))
                continue;
            MediaAsset? asset = null;
            try
            {
                var info = new FileInfo(path);
                asset = new MediaAsset(root, Path.GetRelativePath(root, path), kind, info.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (asset is not null)
                yield return asset;
        }
    }

    /// <summary>
    /// A macOS "._name" AppleDouble file: the Finder metadata a Mac writes next to each file it copies to a non-Mac drive. It
    /// carries the picture's extension but holds no picture, so it is not media. Only "._" names are opened to check.
    /// </summary>
    public static bool IsMacSidecar(string path)
    {
        if (!Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal))
            return false;
        try
        {
            Span<byte> header = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // 00 05 16 07 is AppleDouble, 00 05 16 00 AppleSingle.
            return stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) == 4 && header[0] == 0 && header[1] == 5 && header[2] == 0x16 && header[3] is 7 or 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static Dictionary<string, MediaKind> BuildExtensions()
    {
        var result = new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".avif", ".heic", ".heif" })
            result[extension] = MediaKind.Photo;
        foreach (var extension in new[] { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".mts", ".m2ts", ".mpg", ".mpeg", ".ts" })
            result[extension] = MediaKind.Video;
        foreach (var extension in new[] { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".aif" })
            result[extension] = MediaKind.Audio;
        return result;
    }
}
