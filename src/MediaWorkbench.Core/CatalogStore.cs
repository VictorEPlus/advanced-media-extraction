using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MediaWorkbench.Core;

public sealed class CatalogStore
{
    private readonly string connectionString;

    public CatalogStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS assets (
                root TEXT COLLATE NOCASE NOT NULL,
                relative_path TEXT COLLATE NOCASE NOT NULL,
                kind TEXT NOT NULL,
                size INTEGER NOT NULL,
                modified INTEGER NOT NULL,
                favorite INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (root, relative_path)
            );
            """;
        command.ExecuteNonQuery();
    }

    public void Index(IEnumerable<MediaAsset> assets, CancellationToken cancellationToken = default)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO assets(root, relative_path, kind, size, modified)
                VALUES($root, $path, $kind, $size, $modified)
                ON CONFLICT(root, relative_path) DO UPDATE SET
                    kind=excluded.kind, size=excluded.size, modified=excluded.modified;
                """;
            command.Parameters.AddWithValue("$root", NormalizeRoot(asset.Root));
            command.Parameters.AddWithValue("$path", asset.RelativePath);
            command.Parameters.AddWithValue("$kind", asset.Kind.ToString());
            command.Parameters.AddWithValue("$size", asset.Length);
            command.Parameters.AddWithValue("$modified", asset.ModifiedTicks);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Everything indexed under <paramref name="root"/> the last time it was scanned, with favorites marked. Read before scanning, so a
    /// folder opened before shows its files at once; the scan that follows only adds, updates and removes what changed.
    /// </summary>
    public List<(MediaAsset Asset, bool Favorite)> ReadIndex(string root)
    {
        root = NormalizeRoot(root);
        var result = new List<(MediaAsset, bool)>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path, kind, size, modified, favorite FROM assets WHERE root=$root;";
        command.Parameters.AddWithValue("$root", root);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Enum.TryParse<MediaKind>(reader.GetString(1), out var kind))
                continue;
            result.Add((new MediaAsset(root, reader.GetString(0), kind, reader.GetInt64(2), reader.GetInt64(3)), reader.GetInt64(4) == 1));
        }
        return result;
    }

    /// <summary>Forgets files under <paramref name="root"/> that a scan no longer found, except favorites, which are kept in case the file comes back.</summary>
    public int Prune(string root, IReadOnlyCollection<string> relativePaths)
    {
        root = NormalizeRoot(root);
        var present = new HashSet<string>(relativePaths, StringComparer.OrdinalIgnoreCase);
        var gone = new List<string>();
        using var connection = Open();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT relative_path FROM assets WHERE root=$root AND favorite=0;";
            select.Parameters.AddWithValue("$root", root);
            using var reader = select.ExecuteReader();
            while (reader.Read())
                if (!present.Contains(reader.GetString(0)))
                    gone.Add(reader.GetString(0));
        }
        using var transaction = connection.BeginTransaction();
        foreach (var path in gone)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM assets WHERE root=$root AND relative_path=$path;";
            delete.Parameters.AddWithValue("$root", root);
            delete.Parameters.AddWithValue("$path", path);
            delete.ExecuteNonQuery();
        }
        transaction.Commit();
        return gone.Count;
    }

    public HashSet<string> GetFavorites(string root)
    {
        root = NormalizeRoot(root);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var allFavorites = GetFavoritePaths();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path FROM assets WHERE root=$root;";
        command.Parameters.AddWithValue("$root", root);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relative = reader.GetString(0);
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!allFavorites.Contains(full)) continue;
            result.Add(relative);
            represented.Add(full);
        }
        foreach (var path in allFavorites.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !represented.Contains(path)))
            result.Add(Path.GetRelativePath(root, path));
        return result;
    }

    public HashSet<string> GetFavoritePaths()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT root,relative_path FROM assets WHERE favorite=1;";
        using var reader = command.ExecuteReader();
        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            favorites.Add(Path.GetFullPath(Path.Combine(reader.GetString(0), reader.GetString(1))));
        return favorites;
    }

    public void SetFavorite(MediaAsset asset, bool favorite)
    {
        Index([asset]);
        using var connection = Open();
        using var command = connection.CreateCommand();
        connection.CreateFunction<string, string, string>("full_path", (root, relative) => Path.GetFullPath(Path.Combine(root, relative)));
        command.CommandText = "UPDATE assets SET favorite=$favorite WHERE full_path(root,relative_path)=$full COLLATE NOCASE;";
        command.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
        command.Parameters.AddWithValue("$full", Path.GetFullPath(asset.FullPath));
        command.ExecuteNonQuery();
    }

    public string ExportFavorites(string root) => JsonSerializer.Serialize(
        new FavoritesManifest(1, GetFavorites(root).Order(StringComparer.OrdinalIgnoreCase).ToArray()),
        new JsonSerializerOptions { WriteIndented = true });

    public int ImportFavorites(string root, string json)
    {
        var manifest = JsonSerializer.Deserialize<FavoritesManifest>(json)
            ?? throw new InvalidDataException("The favorites file is empty.");
        if (manifest.Version != 1 || manifest.RelativePaths is null)
            throw new InvalidDataException("Unsupported favorites file version.");
        root = NormalizeRoot(root);
        var paths = manifest.RelativePaths.Select(path => ValidateRelativePath(root, path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var imported = 0;
        foreach (var path in paths)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE assets SET favorite=1 WHERE root=$root AND relative_path=$path;";
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$path", path);
            imported += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return imported;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static string ValidateRelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':'))
            throw new InvalidDataException("Favorites must contain relative paths within the selected folder.");
        var fullPath = Path.GetFullPath(Path.Combine(root, path));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A favorite path escapes the selected folder.");
        return Path.GetRelativePath(root, fullPath);
    }

    private sealed record FavoritesManifest(int Version, string[] RelativePaths);
}
