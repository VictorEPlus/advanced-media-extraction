namespace MediaWorkbench.Core;

public sealed class LibraryScanner
{
    private static readonly Dictionary<string, MediaKind> Extensions = BuildExtensions();

    public static MediaAsset? ReadFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!Extensions.TryGetValue(Path.GetExtension(path), out var kind) || !File.Exists(path)) return null;
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
            if (!Extensions.TryGetValue(Path.GetExtension(path), out var kind))
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
