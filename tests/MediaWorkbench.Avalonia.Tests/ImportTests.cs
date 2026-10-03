using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MediaWorkbench.Avalonia.Tests;

/// <summary>
/// The first start copies the WPF app's data. These use made-up WPF data folders in the test's own temp folder; the real
/// %LOCALAPPDATA% folders are never read or written.
/// </summary>
public sealed class ImportTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(), "MediaWorkbench.Avalonia.Tests", "import-" + Guid.NewGuid().ToString("N"));
    private string Classic => Path.Combine(workspace, "wpf");
    private string Data => Path.Combine(workspace, "avalonia");

    public ImportTests() => Directory.CreateDirectory(Classic);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(workspace, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Reads a file the WPF app may have open, the way another program can while SQLite holds it.</summary>
    private static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Count(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM favorites;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>A WPF catalog in WAL mode whose latest rows are still only in the log, as while the WPF app is open.</summary>
    private SqliteConnection CatalogInUse(int rows)
    {
        File.WriteAllText(Path.Combine(Classic, "settings.json"), "{}");
        var connection = Open(Path.Combine(Classic, "catalog.db"));
        Run(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE favorites (path TEXT);");
        for (var row = 0; row < rows; row++)
            Run(connection, $"INSERT INTO favorites VALUES ('file{row}.png');");
        return connection;
    }

    [Fact]
    public void TheCatalogIsCopiedWholeWhileTheWpfAppHasItOpenAndItsFilesAreNotTouched()
    {
        using var wpfApp = CatalogInUse(25);
        Assert.True(new FileInfo(Path.Combine(Classic, "catalog.db-wal")).Length > 0, "The rows should still be in the WAL.");
        // The data files must be byte for byte the same. catalog.db-shm is left out: it is SQLite's shared lock table, which
        // every reader (the WPF app's own included) writes to, and it holds no data.
        var before = Directory.GetFiles(Classic).Where(file => !file.EndsWith("-shm", StringComparison.Ordinal)).ToDictionary(file => Path.GetFileName(file), Hash);

        Assert.True(App.ImportClassicData(Data, Classic));

        Assert.Equal(25, Count(Path.Combine(Data, "catalog.db")));
        Assert.True(File.Exists(Path.Combine(Data, "settings.json")));
        Assert.Contains("Copied from", File.ReadAllText(Path.Combine(Data, "imported-from-wpf-app.txt")));
        foreach (var (name, hash) in before)
            Assert.True(hash.SequenceEqual(Hash(Path.Combine(Classic, name))), name + " of the WPF app changed.");
    }

    [Fact]
    public void ACatalogLeftWithItsLogAfterACrashIsStillCopied()
    {
        // The state a crash leaves: database and log on disk, nobody holding them open.
        var crashed = Path.Combine(workspace, "crashed");
        Directory.CreateDirectory(crashed);
        using (var wpfApp = CatalogInUse(7))
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Copy(Path.Combine(Classic, "catalog.db" + suffix), Path.Combine(crashed, "catalog.db" + suffix));
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(Path.Combine(Classic, "catalog.db" + suffix));
            File.Copy(Path.Combine(crashed, "catalog.db" + suffix), Path.Combine(Classic, "catalog.db" + suffix));
        }

        Assert.True(App.ImportClassicData(Data, Classic));
        Assert.Equal(7, Count(Path.Combine(Data, "catalog.db")));
    }

    [Fact]
    public void AFailedCopyLeavesNoMarkerAndIsTriedAgainOnTheNextStart()
    {
        using (CatalogInUse(3)) { }
        SqliteConnection.ClearAllPools();
        // Something in the way of one file: the copy fails partway.
        Directory.CreateDirectory(Path.Combine(Data, "settings.json"));

        Assert.False(App.ImportClassicData(Data, Classic));
        Assert.False(File.Exists(Path.Combine(Data, "imported-from-wpf-app.txt")), "A failed copy must not be marked as done.");
        Assert.Contains("will be tried again", File.ReadAllText(Path.Combine(Data, "app.log")));

        Directory.Delete(Path.Combine(Data, "settings.json"));
        Assert.True(App.ImportClassicData(Data, Classic));
        Assert.True(File.Exists(Path.Combine(Data, "imported-from-wpf-app.txt")));
        Assert.Equal(3, Count(Path.Combine(Data, "catalog.db")));
    }

    [Fact]
    public void WithNoWpfDataThereIsNothingToCopyAndThatCountsAsDone()
    {
        Assert.True(App.ImportClassicData(Data, Classic));
        Assert.Equal("There was no WPF app data to copy.", File.ReadAllText(Path.Combine(Data, "imported-from-wpf-app.txt")));
        Assert.False(File.Exists(Path.Combine(Data, "catalog.db")));
    }
}
