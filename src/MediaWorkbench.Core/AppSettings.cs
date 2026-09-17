using System.Text.Json;

namespace MediaWorkbench.Core;

public sealed record AppSettings
{
    public const int RecentLibraryLimit = 8;

    public string ExportDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MediaWorkbench Exports");
    public string FfmpegDirectory { get; init; } = "";
    public string LastLibrary { get; init; } = "";
    public int CacheMegabytes { get; init; } = 512;
    public string SortMethod { get; init; } = "Name (natural)";
    public double ThumbnailHeight { get; init; } = 84;
    public bool ShowSources { get; init; } = true;
    public bool ShowInspector { get; init; } = true;
    public string[] RecentLibraries { get; init; } = [];

    /// <summary>Returns settings with <paramref name="root"/> moved to the front of the recent list, bounded and de-duplicated.</summary>
    public AppSettings WithRecentLibrary(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var recent = new List<string> { root };
        recent.AddRange(RecentLibraries.Where(item => !string.Equals(item, root, StringComparison.OrdinalIgnoreCase)));
        return this with { LastLibrary = root, RecentLibraries = recent.Take(RecentLibraryLimit).ToArray() };
    }

    public bool Equals(AppSettings? other) => other is not null
        && ExportDirectory == other.ExportDirectory && FfmpegDirectory == other.FfmpegDirectory && LastLibrary == other.LastLibrary
        && CacheMegabytes == other.CacheMegabytes && SortMethod == other.SortMethod && ThumbnailHeight.Equals(other.ThumbnailHeight)
        && ShowSources == other.ShowSources && ShowInspector == other.ShowInspector
        && RecentLibraries.SequenceEqual(other.RecentLibraries, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(ExportDirectory, FfmpegDirectory, LastLibrary, CacheMegabytes, SortMethod, ThumbnailHeight, ShowSources, RecentLibraries.Length);
}

public sealed class SettingsStore(string path)
{
    public AppSettings Load()
    {
        if (!File.Exists(path))
            return new AppSettings();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Settings are empty; rename settings.json to reset them.");
        Validate(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void Validate(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ExportDirectory) || !Path.IsPathFullyQualified(settings.ExportDirectory))
            throw new InvalidDataException("Export directory must be an absolute path.");
        if (settings.CacheMegabytes is < 64 or > 8192)
            throw new InvalidDataException("Cache size must be between 64 and 8192 MB.");
        if (!double.IsFinite(settings.ThumbnailHeight) || settings.ThumbnailHeight is < 56 or > 128)
            throw new InvalidDataException("Thumbnail height must be between 56 and 128 pixels.");
        if (settings.RecentLibraries is null || settings.RecentLibraries.Length > AppSettings.RecentLibraryLimit || settings.RecentLibraries.Any(item => string.IsNullOrWhiteSpace(item) || !Path.IsPathFullyQualified(item)))
            throw new InvalidDataException("Recent libraries must be a short list of absolute folder paths.");
    }
}
