using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MediaWorkbench.Core;

public sealed class TagStore
{
    private readonly string connectionString;

    public TagStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS media_tags (
                path TEXT COLLATE NOCASE NOT NULL, tag TEXT COLLATE NOCASE NOT NULL,
                source TEXT NOT NULL, PRIMARY KEY(path, tag));
            CREATE INDEX IF NOT EXISTS ix_media_tags_tag ON media_tags(tag);
            """;
        command.ExecuteNonQuery();
    }

    public Dictionary<string, string[]> ReadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,tag FROM media_tags ORDER BY tag COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (!result.TryGetValue(path, out var tags)) result[path] = tags = [];
            tags.Add(reader.GetString(1));
        }
        return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public void Add(string path, IEnumerable<string> tags, string source = "manual")
    {
        path = Path.GetFullPath(path);
        var normalized = tags.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var tag in normalized)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO media_tags(path,tag,source) VALUES($path,$tag,$source) ON CONFLICT DO NOTHING;";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$tag", tag);
            command.Parameters.AddWithValue("$source", source);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Remove(string path, string tag)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM media_tags WHERE path=$path AND tag=$tag;";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$tag", Normalize(tag));
        command.ExecuteNonQuery();
    }

    public string ExportJson()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,tag,source FROM media_tags ORDER BY path,tag;";
        using var reader = command.ExecuteReader();
        var relationships = new List<object>();
        while (reader.Read()) relationships.Add(new { Path = reader.GetString(0), Tag = reader.GetString(1), Source = reader.GetString(2) });
        return JsonSerializer.Serialize(new { Version = 1, Relationships = relationships }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Normalize(string tag)
    {
        tag = tag.Trim();
        if (tag.Length is 0 or > 180 || tag.Any(char.IsControl))
            throw new ArgumentException("Tags must have 1-180 characters without control characters.");
        return tag;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
