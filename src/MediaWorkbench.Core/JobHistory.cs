using System.Text.Json;

namespace MediaWorkbench.Core;

/// <summary>One finished export, retained across application restarts so results are never silently lost.</summary>
public sealed record ExportJobRecord(string Title, string Status, string? OutputPath, DateTimeOffset FinishedAt)
{
    public bool Succeeded => Status == "Complete";
}

public sealed class JobHistoryStore(string path)
{
    public const int Capacity = 200;

    public IReadOnlyList<ExportJobRecord> Load()
    {
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<ExportJobRecord[]>(File.ReadAllText(path))?.Where(record => record is { Title: not null, Status: not null }).ToArray() ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    /// <summary>Persists the newest <see cref="Capacity"/> records, newest first, with an atomic replace.</summary>
    public void Save(IEnumerable<ExportJobRecord> records)
    {
        var kept = records.OrderByDescending(record => record.FinishedAt).Take(Capacity).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(kept, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
