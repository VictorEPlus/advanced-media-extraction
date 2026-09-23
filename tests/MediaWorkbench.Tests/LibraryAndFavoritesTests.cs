using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

public sealed class LibraryAndFavoritesTests
{
    [Fact]
    public void ScanIncludesNestedMediaAndIgnoresOtherFiles()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(temporary.FilePath("photo.JPG"), "photo");
        File.WriteAllText(temporary.FilePath("nested/clip.Mp4"), "video");
        File.WriteAllText(temporary.FilePath("nested/song.flac"), "audio");
        File.WriteAllText(temporary.FilePath("notes.txt"), "not media");
        var assets = new LibraryScanner().Scan(temporary.Path).ToArray();
        Assert.Equal(3, assets.Length);
        Assert.Contains(assets, asset => asset.Kind == MediaKind.Photo);
        Assert.Contains(assets, asset => asset.Kind == MediaKind.Video);
        Assert.Contains(assets, asset => asset.Kind == MediaKind.Audio);
        Assert.All(assets, asset => Assert.False(System.IO.Path.IsPathRooted(asset.RelativePath)));
    }

    [Fact]
    public void ScanSkipsMacSidecarFilesButKeepsRealPicturesStartingWithADot()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllBytes(temporary.FilePath("photo.png"), [0x89, 0x50, 0x4E, 0x47]);
        // What a Mac leaves next to photo.png on an exFAT or network drive.
        File.WriteAllBytes(temporary.FilePath("._photo.png"), [0x00, 0x05, 0x16, 0x07, 0x00, 0x02, 0x00, 0x00]);
        Directory.CreateDirectory(temporary.FilePath("__MACOSX"));
        File.WriteAllBytes(temporary.FilePath("__MACOSX/._clip.mov"), [0x00, 0x05, 0x16, 0x07]);
        File.WriteAllBytes(temporary.FilePath("._real.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);
        var names = new LibraryScanner().Scan(temporary.Path).Select(asset => asset.Name).Order().ToArray();
        Assert.Equal(["._real.jpg", "photo.png"], names);
        Assert.Null(LibraryScanner.ReadFile(temporary.FilePath("._photo.png")));
    }

    [Fact]
    public void ScanHonorsCancellation()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(temporary.FilePath("image.png"), "photo");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new LibraryScanner().Scan(temporary.Path, cancellation.Token).ToArray());
    }

    [Theory]
    [InlineData(MediaKind.Photo)]
    [InlineData(MediaKind.Video)]
    [InlineData(MediaKind.Audio)]
    public void FavoritesSurviveRestartAndRescan(MediaKind kind)
    {
        using var temporary = new TemporaryDirectory();
        var database = temporary.FilePath("catalog.db");
        var asset = new MediaAsset(temporary.Path, "nested/media.file", kind, 15, 1);
        var first = new CatalogStore(database);
        first.SetFavorite(asset, true);
        var second = new CatalogStore(database);
        second.Index([asset with { Length = 123, ModifiedTicks = 5 }]);
        Assert.Contains(asset.RelativePath, second.GetFavorites(temporary.Path));
        second.SetFavorite(asset, false);
        Assert.Empty(new CatalogStore(database).GetFavorites(temporary.Path));
    }

    [Fact]
    public void FavoritesRemapToAnotherRootAndMergeWithoutAbsolutePaths()
    {
        using var source = new TemporaryDirectory();
        using var destination = new TemporaryDirectory();
        var relative = System.IO.Path.Combine("vacation", "image.png");
        var sourceStore = new CatalogStore(source.FilePath("catalog.db"));
        sourceStore.SetFavorite(new MediaAsset(source.Path, relative, MediaKind.Photo, 5, 1), true);
        var manifest = sourceStore.ExportFavorites(source.Path);
        Assert.DoesNotContain(source.Path, manifest, StringComparison.OrdinalIgnoreCase);
        var destinationStore = new CatalogStore(destination.FilePath("catalog.db"));
        destinationStore.Index([new MediaAsset(destination.Path, relative, MediaKind.Photo, 5, 1)]);
        destinationStore.SetFavorite(new MediaAsset(destination.Path, "existing.png", MediaKind.Photo, 5, 1), true);
        Assert.Equal(1, destinationStore.ImportFavorites(destination.Path, manifest));
        Assert.Equal(2, destinationStore.GetFavorites(destination.Path).Count);
        Assert.Contains(relative, destinationStore.GetFavorites(destination.Path));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("C:\\outside.png")]
    [InlineData("/outside.png")]
    [InlineData(" ")]
    public void ImportRejectsPathsOutsideTheLibraryAtomically(string invalidPath)
    {
        using var temporary = new TemporaryDirectory();
        var store = new CatalogStore(temporary.FilePath("catalog.db"));
        store.Index([new MediaAsset(temporary.Path, "valid.png", MediaKind.Photo, 1, 1)]);
        var manifest = JsonSerializer.Serialize(new { Version = 1, RelativePaths = new[] { "valid.png", invalidPath } });
        Assert.Throws<InvalidDataException>(() => store.ImportFavorites(temporary.Path, manifest));
        Assert.Empty(store.GetFavorites(temporary.Path));
    }

    [Fact]
    public void MissingFavoritesAreSkippedAndUnsupportedVersionsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var store = new CatalogStore(temporary.FilePath("catalog.db"));
        Assert.Equal(0, store.ImportFavorites(temporary.Path, "{\"Version\":1,\"RelativePaths\":[\"missing.png\"]}"));
        Assert.Throws<InvalidDataException>(() => store.ImportFavorites(temporary.Path, "{\"Version\":2,\"RelativePaths\":[]}"));
    }
}
