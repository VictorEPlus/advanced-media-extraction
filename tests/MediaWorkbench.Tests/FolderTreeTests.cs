using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

public sealed class FolderTreeTests
{
    private static (string, MediaAsset) Item(string folder, string name, MediaKind kind, long length = 100) =>
        (folder, new MediaAsset(@"C:\library", System.IO.Path.Combine(folder, name), kind, length, 1));

    [Fact]
    public void CountsIncludeDescendantsAndChildrenSortNaturally()
    {
        var root = FolderTree.Build(
        [
            Item("", "cover.png", MediaKind.Photo),
            Item(@"shoot10", "a.mp4", MediaKind.Video),
            Item(@"shoot2", "a.png", MediaKind.Photo),
            Item(@"shoot2\day 1", "b.png", MediaKind.Photo, 400),
            Item(@"shoot2/day 1", "c.wav", MediaKind.Audio),
            Item(@"SHOOT2\day 2", "d.mp4", MediaKind.Video),
        ], "library");

        Assert.Equal("library", root.Name);
        Assert.Equal(6, root.Total);
        Assert.Equal(1, root.DirectTotal);
        Assert.Equal(4, root.FolderCount);
        Assert.Equal(["shoot2", "shoot10"], root.Children.Select(child => child.Name));
        var shoot = root.Children[0];
        Assert.Equal((2, 1, 1), (shoot.Photos, shoot.Videos, shoot.Audio));
        Assert.Equal(1, shoot.DirectTotal);
        Assert.Equal(700, shoot.Bytes);
        Assert.Equal(["day 1", "day 2"], shoot.Children.Select(child => child.Name));
        Assert.Equal(@"shoot2\day 1", shoot.Children[0].Path);
        Assert.Same(shoot.Children[1], FolderTree.Find(root, @"shoot2\DAY 2"));
        Assert.Null(FolderTree.Find(root, "missing"));
    }

    [Fact]
    public void EmptyChainsCollapseIntoOneRowButKeepTheDeepPath()
    {
        var root = FolderTree.Build(
        [
            Item(@"C:\Users\me\Media\clips", "a.mp4", MediaKind.Video),
            Item(@"C:\Users\me\Media\stills", "b.png", MediaKind.Photo),
            Item(@"\\server\share\archive", "c.png", MediaKind.Photo),
        ], "All tagged media");

        Assert.Equal(2, root.Children.Count);
        var local = root.Children.Single(child => child.Name.StartsWith("C:", StringComparison.Ordinal));
        Assert.Equal(@"C:\Users\me\Media", local.Name);
        Assert.Equal(@"C:\Users\me\Media", local.Path);
        Assert.Equal(["clips", "stills"], local.Children.Select(child => child.Name));
        var network = root.Children.Single(child => child.Name.StartsWith("server", StringComparison.Ordinal));
        Assert.Equal(@"server\share\archive", network.Path);
        Assert.Empty(network.Children);
    }

    [Theory]
    [InlineData("", @"any\folder", true)]
    [InlineData("shoot", "shoot", true)]
    [InlineData("shoot", @"SHOOT\day 1", true)]
    [InlineData("shoot", "shoot2", false)]
    [InlineData(@"shoot\day 1", "shoot", false)]
    public void FolderFilterMatchesTheFolderAndItsDescendantsOnly(string filter, string folder, bool expected) =>
        Assert.Equal(expected, FolderTree.Contains(filter, folder));

    [Theory]
    [InlineData(null, "")]
    [InlineData(@"\\server\share\x\", @"server\share\x")]
    [InlineData("a/b\\c", @"a\b\c")]
    public void FolderKeysNormalizeSeparators(string? input, string expected) => Assert.Equal(expected, FolderTree.Normalize(input));
}
