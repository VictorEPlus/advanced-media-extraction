using System.Text.Json;

namespace MediaWorkbench.Core;

public sealed record AppSettings
{
    public const int RecentLibraryLimit = 8;
    public const int WorkspaceRootLimit = 32;
    public const int WorkspaceTabLimit = 16;

    public string ExportDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MediaWorkbench Exports");
    public string FfmpegDirectory { get; init; } = "";
    public string LastLibrary { get; init; } = "";
    public int CacheMegabytes { get; init; } = 512;
    public string SortMethod { get; init; } = "Name (natural)";
    public double ThumbnailHeight { get; init; } = 84;
    public bool ShowSources { get; init; } = true;
    public bool ShowInspector { get; init; } = true;
    /// <summary>The preview follows the thumbnail at the filmstrip marker while scrolling.</summary>
    public bool FollowFilmstrip { get; init; }
    /// <summary>Set once the first-run "Tour the UI" invitation has been shown, so it is not repeated.</summary>
    public bool TourOffered { get; init; }
    /// <summary>Bumped when a layout change needs a one-time adjustment of saved sizes. 2 = the filmstrip header row was removed and its height given to the thumbnails.</summary>
    public int LayoutVersion { get; init; }
    public string[] RecentLibraries { get; init; } = [];
    /// <summary>The folders open side by side in the workspace. Each keeps its own tree; none replaces another.</summary>
    public string[] WorkspaceRoots { get; init; } = [];
    /// <summary>The folder views open as tabs over the filmstrip, as workspace folder keys (the folder's label, then its subfolders).</summary>
    public string[] WorkspaceTabs { get; init; } = [];
    public int SelectedTab { get; init; }

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
        && ShowSources == other.ShowSources && ShowInspector == other.ShowInspector && FollowFilmstrip == other.FollowFilmstrip && TourOffered == other.TourOffered && LayoutVersion == other.LayoutVersion
        && RecentLibraries.SequenceEqual(other.RecentLibraries, StringComparer.Ordinal)
        && WorkspaceRoots.SequenceEqual(other.WorkspaceRoots, StringComparer.Ordinal) && WorkspaceTabs.SequenceEqual(other.WorkspaceTabs, StringComparer.Ordinal) && SelectedTab == other.SelectedTab;

    public override int GetHashCode() => HashCode.Combine(ExportDirectory, FfmpegDirectory, LastLibrary, CacheMegabytes, SortMethod, ThumbnailHeight,
        HashCode.Combine(ShowSources, ShowInspector, TourOffered, FollowFilmstrip, WorkspaceRoots.Length, WorkspaceTabs.Length, SelectedTab), RecentLibraries.Length);
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
        if (!double.IsFinite(settings.ThumbnailHeight) || settings.ThumbnailHeight is < 56 or > 168)
            throw new InvalidDataException("Thumbnail height must be between 56 and 168 pixels.");
        if (settings.RecentLibraries is null || settings.RecentLibraries.Length > AppSettings.RecentLibraryLimit || settings.RecentLibraries.Any(item => string.IsNullOrWhiteSpace(item) || !Path.IsPathFullyQualified(item)))
            throw new InvalidDataException("Recent libraries must be a short list of absolute folder paths.");
        if (settings.WorkspaceRoots is null || settings.WorkspaceRoots.Length > AppSettings.WorkspaceRootLimit || settings.WorkspaceRoots.Any(item => string.IsNullOrWhiteSpace(item) || !Path.IsPathFullyQualified(item)))
            throw new InvalidDataException("Workspace folders must be a short list of absolute folder paths.");
        if (settings.WorkspaceTabs is null || settings.WorkspaceTabs.Length > AppSettings.WorkspaceTabLimit)
            throw new InvalidDataException("Too many workspace tabs.");
    }
}
