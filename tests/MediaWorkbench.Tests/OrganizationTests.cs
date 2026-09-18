using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

public sealed class OrganizationTests
{
    [Fact]
    public void BatchedDiscoveryNotifiesTheSortedViewOnce()
    {
        var items = new BatchCollection<int>();
        var notifications = 0;
        items.CollectionChanged += (_, _) => notifications++;
        items.AddRange(Enumerable.Range(1, 5000));
        Assert.Equal(5000, items.Count);
        Assert.Equal(1, notifications);
        items.AddRange([]);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void CollectionRoundTripsDeduplicatesAndPreservesMissingPaths()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.FilePath("collection.json");
        var media = temporary.FilePath("missing image.png");
        var other = temporary.FilePath("other.mp4");
        var store = new CollectionStore();
        store.Save(path, CollectionStore.Add(new StagingCollection(1, "Review", []), [media, media.ToUpperInvariant(), other]));
        var restored = store.Load(path);
        Assert.Equal(2, restored.Paths.Length);
        Assert.Equal("Review", restored.Name);
        Assert.False(File.Exists(media));
        store.Save(path, CollectionStore.Remove(restored, media.ToUpperInvariant()));
        Assert.Equal(other, Assert.Single(store.Load(path).Paths));
    }

    [Theory]
    [InlineData(2, "Name", "relative.png")]
    [InlineData(1, "Name", "relative.png")]
    [InlineData(1, "", "relative.png")]
    public void InvalidCollectionsDoNotReplaceExistingJson(int version, string name, string entry)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.FilePath("collection.json");
        var store = new CollectionStore();
        store.Save(path, new StagingCollection(1, "Keep", []));
        Assert.Throws<InvalidDataException>(() => store.Save(path, new StagingCollection(version, name, [entry])));
        Assert.Equal("Keep", store.Load(path).Name);
    }

    [Theory]
    [InlineData("frame2.png", "frame10.png")]
    [InlineData("frame002.png", "frame10.png")]
    [InlineData("clip9_frame2.png", "clip9_frame20.png")]
    [InlineData("frame99999999999999999999.png", "frame100000000000000000000.png")]
    [InlineData("a.png", "b.png")]
    public void NaturalSortOrdersFrameSequencesWithoutIntegerOverflow(string first, string second)
    {
        Assert.True(NaturalOrder.Compare(first, second) < 0);
        Assert.True(NaturalOrder.Compare(second, first) > 0);
        Assert.Equal(0, NaturalOrder.Compare(first, first.ToUpperInvariant()));
    }

    [Fact]
    public void CropCoordinatesAccountForLetterboxingAndReverseDragging()
    {
        var crop = PixelCrop.FromDrag(300, 175, 100, 75, 400, 300, 800, 400);
        Assert.Equal(new PixelCrop(200, 50, 400, 200), crop);
        Assert.Equal(new PixelCrop(0, 0, 800, 400), PixelCrop.FromDrag(-50, -50, 500, 400, 400, 300, 800, 400));
        Assert.Null(PixelCrop.FromDrag(10, 10, 50, 30, 400, 300, 800, 400));
        Assert.Null(PixelCrop.FromDrag(1, 1, 1, 1, 400, 300, 800, 400));
        Assert.Null(PixelCrop.FromDrag(0, 0, 1, 1, 0, 0, 800, 400));
        Assert.Throws<ArgumentException>(() => new PixelCrop(700, 0, 101, 50).Validate(800, 400));
    }

    [Theory]
    [InlineData(1920, 1080, "16:9")]
    [InlineData(1200, 1600, "3:4")]
    [InlineData(500, 500, "1:1")]
    [InlineData(0, 500, "Unknown")]
    public void AspectRatiosAreReduced(int width, int height, string expected) => Assert.Equal(expected, MediaDimensions.AspectRatio(width, height));

    [Theory]
    [InlineData(1920, 1080, "16:9")]
    [InlineData(1080, 1920, "9:16")]
    [InlineData(1280, 800, "16:10")]
    [InlineData(2560, 1080, "64:27 (about 21:9)")]
    [InlineData(1366, 768, "683:384 (about 16:9)")]
    [InlineData(352, 460, "88:115 (about 3:4)")]
    [InlineData(1080, 1350, "4:5")]
    [InlineData(1000, 1490, "100:149 (about 2:3)")]
    [InlineData(1001, 1000, "1001:1000 (about 1:1)")]
    [InlineData(0, 10, "Unknown")]
    public void TheClosestEverydayRatioIsNamedBesideTheExactOne(int width, int height, string expected) => Assert.Equal(expected, MediaDimensions.DescribeAspect(width, height));

    [Fact]
    public void TagsPersistAcrossRootsAndExportProvenanceWithoutTouchingMedia()
    {
        using var temporary = new TemporaryDirectory();
        var database = temporary.FilePath("catalog.db");
        var first = Path.GetFullPath(temporary.FilePath("folder1/a.png"));
        var second = Path.GetFullPath(temporary.FilePath("folder2/b.mp4"));
        File.WriteAllText(first, "unchanged");
        var store = new TagStore(database);
        store.Add(first, [" Sunset ", "sunset"]);
        store.Add(second, ["sunset", "aspect ratio:16:9"], "confirmed metadata");
        store = new TagStore(database);
        var tags = store.ReadAll();
        Assert.Single(tags[first]);
        Assert.Equal(2, tags[second].Length);
        using var json = JsonDocument.Parse(store.ExportJson());
        Assert.Equal(3, json.RootElement.GetProperty("Relationships").GetArrayLength());
        Assert.Contains("confirmed metadata", store.ExportJson());
        store.Remove(first, "SUNSET");
        Assert.False(store.ReadAll().ContainsKey(first));
        Assert.Equal("unchanged", File.ReadAllText(first));
    }

    [Fact]
    public void InvalidTagBatchesAreAtomic()
    {
        using var temporary = new TemporaryDirectory();
        var store = new TagStore(temporary.FilePath("catalog.db"));
        Assert.Throws<ArgumentException>(() => store.Add(temporary.FilePath("image.png"), ["valid", "bad\nvalue"]));
        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public void FavoritesRemainTheSameWhenOpenedThroughACollectionOrNestedRoot()
    {
        using var temporary = new TemporaryDirectory();
        var store = new CatalogStore(temporary.FilePath("catalog.db"));
        var relative = Path.Combine("nested", "photo.png");
        var original = new MediaAsset(temporary.Path, relative, MediaKind.Photo, 1, 1);
        var nested = new MediaAsset(Path.Combine(temporary.Path, "nested"), "photo.png", MediaKind.Photo, 1, 1);
        store.SetFavorite(original, true);
        store.Index([nested]);
        Assert.Contains("photo.png", store.GetFavorites(nested.Root));
        store.SetFavorite(nested, false);
        Assert.Empty(store.GetFavorites(temporary.Path));
        store.SetFavorite(nested, true);
        Assert.Contains(relative, store.GetFavorites(temporary.Path));
    }
}
