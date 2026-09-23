using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public void LabelsAreFolderNamesAndStayUniqueWhenNamesRepeat()
    {
        var labels = Workspace.Labels([@"C:\Users\me\Videos", @"D:\Work\Images", @"E:\Home\Images", @"D:\"]);
        // The first "Images" keeps its plain name; only the one added later is told apart.
        Assert.Equal(["Videos", "Images", "Images (Home)", "D"], labels);
        var same = Workspace.Labels([@"C:\A\Shots", @"D:\A\Shots"]);
        Assert.Equal(2, same.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(labels, label => Assert.DoesNotContain('\\', label));
    }

    [Fact]
    public void FolderKeysStartWithTheLabel()
    {
        Assert.Equal("Videos", Workspace.FolderKey("Videos", ""));
        Assert.Equal(@"Videos\2026\trip", Workspace.FolderKey("Videos", @"2026/trip"));
        Assert.True(FolderTree.Contains("Videos", Workspace.FolderKey("Videos", "2026")));
        Assert.False(FolderTree.Contains("Videos", Workspace.FolderKey("Videos 2", "2026")));
    }

    [Fact]
    public void DiffFindsNewChangedAndRemovedFiles()
    {
        MediaAsset Asset(string path, long size, long modified = 1) => new(@"C:\root", path, MediaKind.Photo, size, modified);
        var (added, changed, removed) = Workspace.Diff(
            [Asset("a.png", 10), Asset("b.png", 10), Asset("c.png", 10)],
            [Asset("A.PNG", 10), Asset("b.png", 11), Asset("d.png", 5)]);
        Assert.Equal(["d.png"], added.Select(asset => asset.RelativePath));
        Assert.Equal(["b.png"], changed.Select(asset => asset.RelativePath));
        Assert.Equal(["c.png"], removed);
    }

    [Fact]
    public void TheIndexIsReadBackAndPrunedWithoutLosingFavorites()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.FilePath("library");
        Directory.CreateDirectory(root);
        var catalog = new CatalogStore(temporary.FilePath("catalog.db"));
        MediaAsset Asset(string path) => new(root, path, MediaKind.Photo, 3, 7);
        catalog.Index([Asset("keep.png"), Asset("gone.png"), Asset(@"sub\starred.png")]);
        catalog.SetFavorite(Asset(@"sub\starred.png"), true);
        var read = catalog.ReadIndex(root);
        Assert.Equal(3, read.Count);
        Assert.True(read.Single(entry => entry.Asset.RelativePath == @"sub\starred.png").Favorite);
        Assert.Equal(1, catalog.Prune(root, ["keep.png"]));
        Assert.Equal(["keep.png", @"sub\starred.png"], catalog.ReadIndex(root).Select(entry => entry.Asset.RelativePath).Order());
    }

    [Fact]
    public void WorkspaceFoldersAndTabsAreSaved()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SettingsStore(temporary.FilePath("settings.json"));
        var settings = new AppSettings { WorkspaceRoots = [@"C:\Videos", @"D:\Pictures"], WorkspaceTabs = ["Videos", @"Pictures\2026"], SelectedTab = 1 };
        store.Save(settings);
        Assert.Equal(settings, store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(settings with { WorkspaceRoots = ["relative"] }));
    }
}
