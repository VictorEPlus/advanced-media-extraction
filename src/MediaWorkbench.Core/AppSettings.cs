using System.Text.Json;

namespace MediaWorkbench.Core;

public sealed record AppSettings
{
    public string ExportDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MediaWorkbench Exports");
    public string FfmpegDirectory { get; init; } = "";
    public string LastLibrary { get; init; } = "";
    public int CacheMegabytes { get; init; } = 512;
    public string SortMethod { get; init; } = "Name (natural)";
    public double ThumbnailHeight { get; init; } = 84;
    public bool ShowSources { get; init; } = true;
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
    }
}
