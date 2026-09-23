using MediaWorkbench.Core;
using Microsoft.Data.Sqlite;

namespace MediaWorkbench.Tests;

public sealed class TagTrackingTests
{
    private static MediaAsset Scan(string root, string path)
    {
        var info = new FileInfo(path);
        return new MediaAsset(root, Path.GetRelativePath(root, path), MediaKind.Photo, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static List<MediaAsset> ScanAll(string root) => new LibraryScanner().Scan(root).ToList();

    [Fact]
    public void AFileKeepsItsIdAcrossARenameAndACopyGetsANewOne()
    {
        using var temporary = new TemporaryDirectory();
        var original = Path.GetFullPath(temporary.FilePath("a.png"));
        File.WriteAllBytes(original, [1, 2, 3]);
        var identity = FileIdentity.Read(original);
        Assert.NotNull(identity);
        var renamed = Path.GetFullPath(temporary.FilePath("renamed/b.png"));
        File.Move(original, renamed);
        Assert.Equal(identity, FileIdentity.Read(renamed));
        var copy = Path.GetFullPath(temporary.FilePath("copy.png"));
        File.Copy(renamed, copy);
        Assert.NotEqual(identity, FileIdentity.Read(copy));
    }

    [Fact]
    public void TagsFollowARenamedOrMovedFileAndNotAFileThatOnlyLooksTheSame()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.GetFullPath(temporary.FilePath("library"));
        var store = new TagStore(Path.GetFullPath(temporary.FilePath("catalog.db")));
        var shot = Path.Combine(root, "shot.png");
        File.WriteAllBytes(Path.GetFullPath(temporary.FilePath("library/shot.png")), [1, 2, 3, 4]);
        // Same size and date, different content: must not be taken for the tagged file.
        File.WriteAllBytes(Path.GetFullPath(temporary.FilePath("library/other.png")), [9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(Path.Combine(root, "other.png"), File.GetLastWriteTimeUtc(shot));
        store.Add(shot, ["client A", "keep"]);
        TagReconciler.FillFingerprints(store);

        var moved = Path.Combine(root, "sorted", "renamed shot.png");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(shot, moved);
        var result = TagReconciler.Reconcile(store, root, ScanAll(root));
        Assert.Equal(1, result.Moved);
        var tags = store.ReadAll();
        Assert.Equal(["client A", "keep"], tags[moved]);
        Assert.False(tags.ContainsKey(shot));
        Assert.False(tags.ContainsKey(Path.Combine(root, "other.png")));
    }

    [Fact]
    public void ExactCopiesGetTheTagsAndAFileRewrittenElsewhereIsFoundByItsContent()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.GetFullPath(temporary.FilePath("library"));
        var store = new TagStore(Path.GetFullPath(temporary.FilePath("catalog.db")));
        var shot = Path.GetFullPath(temporary.FilePath("library/shot.png"));
        File.WriteAllBytes(shot, [5, 6, 7, 8, 9]);
        store.Add(shot, ["sunset"]);
        TagReconciler.FillFingerprints(store);

        var copy = Path.GetFullPath(temporary.FilePath("library/copies/shot copy.png"));
        File.Copy(shot, copy);
        var copied = TagReconciler.Reconcile(store, root, ScanAll(root));
        Assert.Equal(1, copied.Copied);
        Assert.Equal(["sunset"], store.ReadAll()[copy]);

        // Written anew somewhere else and the original deleted, as a move to another drive does: a new ID, the same content.
        var elsewhere = Path.GetFullPath(temporary.FilePath("library/elsewhere/shot.png"));
        File.WriteAllBytes(elsewhere, [5, 6, 7, 8, 9]);
        File.Delete(shot);
        var moved = TagReconciler.Reconcile(store, root, ScanAll(root));
        Assert.Equal(1, moved.Moved);
        var tags = store.ReadAll();
        Assert.True(tags.ContainsKey(elsewhere) && !tags.ContainsKey(shot));
    }

    [Fact]
    public void FolderTagsAreLiveAndFollowAMovedFolder()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.GetFullPath(temporary.FilePath("library"));
        var store = new TagStore(Path.GetFullPath(temporary.FilePath("catalog.db")));
        var shoot = Path.GetFullPath(temporary.FilePath("library/shoot A"));
        File.WriteAllBytes(Path.GetFullPath(temporary.FilePath("library/shoot A/day 1/x.png")), [1]);
        store.AddFolderTags(shoot, ["client A"]);
        var folders = store.ReadFolderTags();
        Assert.Equal([("client A", shoot)], FolderTags.Inherited(Path.Combine(shoot, "day 1"), folders).ToList());
        Assert.Empty(FolderTags.Inherited(shoot + " B", folders));

        var renamed = Path.GetFullPath(temporary.FilePath("library/2026 shoot A"));
        Directory.Move(shoot, renamed);
        Assert.Equal(1, TagReconciler.Reconcile(store, root, ScanAll(root)).FoldersMoved);
        Assert.Equal(["client A"], store.ReadFolderTags()[renamed]);
    }

    [Fact]
    public void TagsFromTheFirstVersionAreMigrated()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.GetFullPath(temporary.FilePath("catalog.db"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE media_tags (path TEXT COLLATE NOCASE NOT NULL, tag TEXT COLLATE NOCASE NOT NULL, source TEXT NOT NULL, PRIMARY KEY(path, tag));
                INSERT INTO media_tags VALUES ('C:\old\a.png','review','manual'), ('C:\old\a.png','sunset','manual');
                """;
            command.ExecuteNonQuery();
        }
        var store = new TagStore(database);
        Assert.Equal(["review", "sunset"], store.ReadAll()[@"C:\old\a.png"]);
        _ = new TagStore(database);
        Assert.Single(store.ReadFiles());
    }

    [Fact]
    public void SuggestionsNeedRealResemblanceAndSayWhy()
    {
        FileTraits Shot(string name, string camera = "Sony A7", string day = "2026-09-20", ulong? look = null) => new()
        {
            Kind = MediaKind.Photo, Camera = camera, Day = day, Folder = @"D:\cards\101", NameStem = FileTraits.SplitName(name).Stem,
            NameNumber = FileTraits.SplitName(name).Number, Width = 6000, Height = 4000, Visual = look
        };
        var tagged = new List<(FileTraits, IReadOnlyCollection<string>)>
        {
            (Shot("DSC0401"), ["client A"]), (Shot("DSC0402"), ["client A", "rejects"]), (Shot("DSC0405"), ["client A"]),
            (Shot("IMG9000", camera: "Canon R5", day: "2025-01-01"), ["other job"])
        };
        var suggestions = TagSuggester.Suggest(Shot("DSC0410"), tagged, alreadyOn: []);
        Assert.Equal("client A", suggestions[0].Tag);
        Assert.Equal(3, suggestions[0].Files);
        Assert.Equal("same camera, same day", suggestions[0].Reason);
        Assert.DoesNotContain(suggestions, suggestion => suggestion.Tag == "other job");
        Assert.DoesNotContain(TagSuggester.Suggest(Shot("DSC0410"), tagged, alreadyOn: ["client A"]), suggestion => suggestion.Tag == "client A");

        // One file that looks the same is enough; one that merely shares a camera is not.
        var lookAlike = TagSuggester.Suggest(Shot("edit", camera: "Other", day: "2020-01-01", look: 0xF0F0F0F0F0F0F0F0),
            [(Shot("orig", camera: "X", day: "1999-01-01", look: 0xF0F0F0F0F0F0F0F1), ["hero shot"])], []);
        Assert.Equal("hero shot", Assert.Single(lookAlike).Tag);
        Assert.Empty(TagSuggester.Suggest(Shot("x", day: "2020-02-02") with { Folder = "E:\\" }, [(Shot("y", day: "2021-02-02") with { Folder = "F:\\", Width = 1 }, ["t"])], []));
    }

    [Fact]
    public void TraitsAreReadFromPhotoAndPhoneVideoDetails()
    {
        var photo = FileTraits.From(new MediaAsset(@"D:\cards", "DSC_0412.jpg", MediaKind.Photo, 1, 1),
            new Dictionary<string, string> { ["Camera maker"] = "SONY", ["Camera model"] = "ILCE-7M4", ["Lens"] = "FE 35mm", ["Date taken"] = "2026-09-20 14:03:11" }, 6000, 4000, 0, 42);
        Assert.Equal(("SONY ILCE-7M4", "2026-09-20", "DSC_", 412L), (photo.Camera, photo.Day, photo.NameStem, photo.NameNumber));
        var video = FileTraits.From(new MediaAsset(@"D:\phone", "clip.mov", MediaKind.Video, 1, 1),
            new Dictionary<string, string> { ["Embedded com.apple.quicktime.make"] = "Apple", ["Embedded com.apple.quicktime.model"] = "iPhone 16", ["Embedded creation_time"] = "2026-09-20T12:00:00.000000Z", ["avg_frame_rate"] = "30/1", ["codec_name"] = "hevc" }, 1920, 1080, 12.5, null);
        Assert.Equal(("Apple iPhone 16", "30/1", "hevc"), (video.Camera, video.FrameRate, video.Codec));
        Assert.NotNull(video.Day);
        Assert.Equal(video, FileTraits.FromJson(video.ToJson()));
    }

    [Fact]
    public void TheLookAlikeFingerprintIgnoresBrightnessAndNoticesADifferentPicture()
    {
        var grey = new byte[VisualHash.Width * VisualHash.Height];
        for (var index = 0; index < grey.Length; index++) grey[index] = (byte)(index * 37 % 200);
        var brighter = grey.Select(value => (byte)(value + 40)).ToArray();
        var reversed = grey.Reverse().ToArray();
        Assert.Equal(0, VisualHash.Distance(VisualHash.FromGrey(grey), VisualHash.FromGrey(brighter)));
        Assert.True(VisualHash.Distance(VisualHash.FromGrey(grey), VisualHash.FromGrey(reversed)) > TagSuggester.SimilarLookDistance);
    }
}
