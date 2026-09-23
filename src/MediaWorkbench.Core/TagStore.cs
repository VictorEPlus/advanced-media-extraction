using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MediaWorkbench.Core;

/// <summary>A tagged file as the store knows it: where it was last seen, who it is (file ID), what is in it (fingerprint) and its details for suggestions.</summary>
public sealed record TaggedFile(long Id, string Path, string? Identity, long Size, long Modified, string? Fingerprint, string? Traits, IReadOnlyList<string> Tags);

/// <summary>
/// Tags live in the app's own database, never in the media. A file's tags are attached to a record that remembers the file's path,
/// its NTFS file ID and its content fingerprint, so they can follow the file when it is renamed or moved, and be copied to exact
/// copies of it. Folder tags are live: every file under the folder carries them, including files added later.
/// </summary>
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
            CREATE TABLE IF NOT EXISTS tagged_files (
                id INTEGER PRIMARY KEY, path TEXT COLLATE NOCASE NOT NULL UNIQUE, identity TEXT,
                size INTEGER NOT NULL DEFAULT 0, modified INTEGER NOT NULL DEFAULT 0, fingerprint TEXT, traits TEXT);
            CREATE INDEX IF NOT EXISTS ix_tagged_identity ON tagged_files(identity);
            CREATE INDEX IF NOT EXISTS ix_tagged_content ON tagged_files(size, fingerprint);
            CREATE TABLE IF NOT EXISTS file_tags (
                file INTEGER NOT NULL, tag TEXT COLLATE NOCASE NOT NULL, source TEXT NOT NULL, PRIMARY KEY(file, tag));
            CREATE INDEX IF NOT EXISTS ix_file_tags_tag ON file_tags(tag);
            CREATE TABLE IF NOT EXISTS folder_tags (
                folder TEXT COLLATE NOCASE NOT NULL, tag TEXT COLLATE NOCASE NOT NULL, identity TEXT, PRIMARY KEY(folder, tag));
            CREATE TABLE IF NOT EXISTS fingerprints (
                path TEXT COLLATE NOCASE PRIMARY KEY, size INTEGER NOT NULL, modified INTEGER NOT NULL, fingerprint TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        MigrateFromPaths(connection);
    }

    /// <summary>
    /// The first version kept tags by path only. Its rows become records, stamped with the file's ID where the file still exists;
    /// the old table is kept, renamed, as a backup.
    /// </summary>
    private static void MigrateFromPaths(SqliteConnection connection)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='media_tags';";
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return;
        }
        var rows = new List<(string Path, string Tag, string Source)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT path,tag,source FROM media_tags;";
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        using var transaction = connection.BeginTransaction();
        foreach (var group in rows.GroupBy(row => row.Path, StringComparer.OrdinalIgnoreCase))
        {
            var id = EnsureFile(connection, transaction, group.Key);
            foreach (var (_, tag, source) in group)
                Execute(connection, transaction, "INSERT INTO file_tags(file,tag,source) VALUES($file,$tag,$source) ON CONFLICT DO NOTHING;", ("$file", id), ("$tag", tag), ("$source", source));
        }
        Execute(connection, transaction, "ALTER TABLE media_tags RENAME TO media_tags_v1_backup;");
        transaction.Commit();
    }

    /// <summary>Every tagged file's own tags, by path.</summary>
    public Dictionary<string, string[]> ReadAll() =>
        ReadFiles().ToDictionary(file => file.Path, file => file.Tags.ToArray(), StringComparer.OrdinalIgnoreCase);

    public List<TaggedFile> ReadFiles()
    {
        using var connection = Open();
        var tags = new Dictionary<long, List<string>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT file,tag FROM file_tags ORDER BY tag COLLATE NOCASE;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!tags.TryGetValue(reader.GetInt64(0), out var list)) tags[reader.GetInt64(0)] = list = [];
                list.Add(reader.GetString(1));
            }
        }
        var files = new List<TaggedFile>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,path,identity,size,modified,fingerprint,traits FROM tagged_files;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!tags.TryGetValue(id, out var list) || list.Count == 0) continue;
                files.Add(new TaggedFile(id, reader.GetString(1), Text(reader, 2), reader.GetInt64(3), reader.GetInt64(4), Text(reader, 5), Text(reader, 6), list));
            }
        }
        return files;
    }

    /// <summary>Folder tags by folder path. A file carries the tags of every folder above it.</summary>
    public Dictionary<string, string[]> ReadFolderTags()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT folder,tag FROM folder_tags ORDER BY tag COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (!result.TryGetValue(reader.GetString(0), out var list)) result[reader.GetString(0)] = list = [];
            list.Add(reader.GetString(1));
        }
        return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public void Add(string path, IEnumerable<string> tags, string source = "manual")
    {
        path = Path.GetFullPath(path);
        var normalized = tags.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var id = EnsureFile(connection, transaction, path);
        foreach (var tag in normalized)
            Execute(connection, transaction, "INSERT INTO file_tags(file,tag,source) VALUES($file,$tag,$source) ON CONFLICT DO NOTHING;", ("$file", id), ("$tag", tag), ("$source", source));
        transaction.Commit();
    }

    public void Remove(string path, string tag)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM file_tags WHERE tag=$tag AND file=(SELECT id FROM tagged_files WHERE path=$path);", ("$path", Path.GetFullPath(path)), ("$tag", Normalize(tag)));
        Execute(connection, transaction, "DELETE FROM tagged_files WHERE path=$path AND NOT EXISTS (SELECT 1 FROM file_tags WHERE file=tagged_files.id);", ("$path", Path.GetFullPath(path)));
        transaction.Commit();
    }

    public void AddFolderTags(string folder, IEnumerable<string> tags)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var identity = FileIdentity.Read(folder);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var tag in tags.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
            Execute(connection, transaction, "INSERT INTO folder_tags(folder,tag,identity) VALUES($folder,$tag,$identity) ON CONFLICT DO NOTHING;", ("$folder", folder), ("$tag", tag), ("$identity", (object?)identity ?? DBNull.Value));
        transaction.Commit();
    }

    public void RemoveFolderTag(string folder, string tag)
    {
        using var connection = Open();
        Execute(connection, null, "DELETE FROM folder_tags WHERE folder=$folder AND tag=$tag;", ("$folder", Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))), ("$tag", Normalize(tag)));
    }

    /// <summary>Folders with tags that no longer exist where they were, with the ID they had, so a moved folder can be found again.</summary>
    public List<(string Folder, string? Identity)> ReadFolderRecords()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT folder, identity FROM folder_tags;";
        using var reader = command.ExecuteReader();
        var result = new List<(string, string?)>();
        while (reader.Read()) result.Add((reader.GetString(0), Text(reader, 1)));
        return result;
    }

    public void MoveFolder(string from, string to)
    {
        to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        using var connection = Open();
        Execute(connection, null, "UPDATE OR IGNORE folder_tags SET folder=$to, identity=$identity WHERE folder=$from;", ("$from", from), ("$to", to), ("$identity", (object?)FileIdentity.Read(to) ?? DBNull.Value));
    }

    /// <summary>A tagged file was found somewhere else: the record now points there, with its new ID, size and date.</summary>
    public void MoveFile(long id, string path)
    {
        var (identity, size, modified) = Stamp(path);
        using var connection = Open();
        Execute(connection, null, "UPDATE OR IGNORE tagged_files SET path=$path, identity=$identity, size=$size, modified=$modified WHERE id=$id;",
            ("$id", id), ("$path", Path.GetFullPath(path)), ("$identity", (object?)identity ?? DBNull.Value), ("$size", size), ("$modified", modified));
    }

    /// <summary>An exact copy of a tagged file gets the same tags, marked as copied, and the same fingerprint.</summary>
    public void CopyTags(TaggedFile from, string toPath)
    {
        Add(toPath, from.Tags, "copied");
        if (from.Fingerprint is { } fingerprint)
        {
            using var connection = Open();
            Execute(connection, null, "UPDATE tagged_files SET fingerprint=$fingerprint WHERE path=$path;", ("$path", Path.GetFullPath(toPath)), ("$fingerprint", fingerprint));
        }
    }

    public void SetFingerprint(long id, long size, long modified, string fingerprint)
    {
        using var connection = Open();
        Execute(connection, null, "UPDATE tagged_files SET fingerprint=$fingerprint, size=$size, modified=$modified WHERE id=$id;", ("$id", id), ("$size", size), ("$modified", modified), ("$fingerprint", fingerprint));
    }

    /// <summary>Saves a tagged file's details for suggestions. Does nothing for a file that has no tags of its own.</summary>
    public void SetTraits(string path, string traits)
    {
        using var connection = Open();
        Execute(connection, null, "UPDATE tagged_files SET traits=$traits WHERE path=$path;", ("$path", Path.GetFullPath(path)), ("$traits", traits));
    }

    public string? CachedFingerprint(string path, long size, long modified)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprint FROM fingerprints WHERE path=$path AND size=$size AND modified=$modified;";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$modified", modified);
        return command.ExecuteScalar() as string;
    }

    public void CacheFingerprint(string path, long size, long modified, string fingerprint)
    {
        using var connection = Open();
        Execute(connection, null, "INSERT INTO fingerprints(path,size,modified,fingerprint) VALUES($path,$size,$modified,$fingerprint) ON CONFLICT(path) DO UPDATE SET size=excluded.size, modified=excluded.modified, fingerprint=excluded.fingerprint;",
            ("$path", Path.GetFullPath(path)), ("$size", size), ("$modified", modified), ("$fingerprint", fingerprint));
    }

    public string ExportJson()
    {
        var files = ReadFiles();
        var sources = new Dictionary<(long, string), string>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT file,tag,source FROM file_tags;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) sources[(reader.GetInt64(0), reader.GetString(1).ToLowerInvariant())] = reader.GetString(2);
        }
        var relationships = files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .SelectMany(file => file.Tags.Select(tag => (object)new { Path = file.Path, Tag = tag, Source = sources.GetValueOrDefault((file.Id, tag.ToLowerInvariant()), "manual") }))
            .ToList();
        var folders = ReadFolderTags().SelectMany(pair => pair.Value.Select(tag => (object)new { Folder = pair.Key, Tag = tag })).ToList();
        return JsonSerializer.Serialize(new { Version = 2, Relationships = relationships, FolderTags = folders }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The file's ID, size and date now; ID null on drives without lasting IDs, all zero when the file is gone.</summary>
    public static (string? Identity, long Size, long Modified) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (FileIdentity.Read(path), info.Length, info.LastWriteTimeUtc.Ticks) : (null, 0, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return (null, 0, 0); }
    }

    private static long EnsureFile(SqliteConnection connection, SqliteTransaction? transaction, string path)
    {
        path = Path.GetFullPath(path);
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT id FROM tagged_files WHERE path=$path;";
            find.Parameters.AddWithValue("$path", path);
            if (find.ExecuteScalar() is long existing) return existing;
        }
        var (identity, size, modified) = Stamp(path);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO tagged_files(path,identity,size,modified) VALUES($path,$identity,$size,$modified) RETURNING id;";
        insert.Parameters.AddWithValue("$path", path);
        insert.Parameters.AddWithValue("$identity", (object?)identity ?? DBNull.Value);
        insert.Parameters.AddWithValue("$size", size);
        insert.Parameters.AddWithValue("$modified", modified);
        return (long)insert.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

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
