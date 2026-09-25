using System.IO;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

internal static partial class DesktopSmokeTest
{
    /// <summary>
    /// Tags in the running app: a resized version of a tagged picture gets the tag suggested and an unrelated picture does not; a
    /// tagged file moved on disk keeps its tags and a copy of it gets them, both without a rescan; a folder tag reaches every file
    /// under the folder, including one added afterwards.
    /// </summary>
    private static async Task CheckTagsAsync(MainViewModel model, MainWindow window, string dataDirectory, ToolPaths tools, CancellationToken token)
    {
        var folder = Path.Combine(dataDirectory, "tagging");
        Directory.CreateDirectory(Path.Combine(folder, "sorted"));
        async Task Make(string source, string name) => await new ProcessRunner().RunAsync(tools.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", source, "-frames:v", "1", Path.Combine(folder, name)], cancellationToken: token);
        await Make("testsrc2=size=320x180", "shot.png");
        await Make("testsrc2=size=160x90", "shot small.png");
        await Make("mandelbrot=size=320x240", "other.png");
        await model.OpenLibraryAsync(folder);
        AssetViewModel Item(string path) => model.Assets.Single(item => string.Equals(item.Asset.FullPath, path, StringComparison.OrdinalIgnoreCase));
        async Task Open(string name)
        {
            model.SelectedAsset = Item(Path.Combine(folder, name));
            await WaitUntilAsync(() => !model.IsPreviewBusy, token);
            await Task.Delay(600, token);
        }
        async Task Eventually(Func<bool> condition, string message)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            try { await WaitUntilAsync(condition, limit.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { Require(false, message); }
        }

        await Open("shot.png");
        model.TagText = "client A";
        model.AddTagsCommand.Execute(null);
        Require(model.SelectedTags.Contains("client A"), "Adding a tag should show it on the file.");

        await Open("shot small.png");
        await Eventually(() => model.Suggestions.Any(suggestion => suggestion.Tag == "client A"), "A resized version of a tagged picture should get its tag suggested.");
        Require(model.Suggestions.First(suggestion => suggestion.Tag == "client A").Reason == "looks the same", "The suggestion should say why: " + model.Suggestions[0].Reason);
        await Open("other.png");
        Require(model.Suggestions.All(suggestion => suggestion.Tag != "client A"), "An unrelated picture in the same folder must not get the tag suggested.");
        Render(window, Path.Combine(dataDirectory, "workspace-tags-suggested.png"));

        // Moved on disk: found again by its file ID when the folder watcher reads the change.
        var moved = Path.Combine(folder, "sorted", "shot renamed.png");
        await Task.Delay(1500, token);
        File.Move(Path.Combine(folder, "shot.png"), moved);
        await Eventually(() => model.Assets.Any(item => string.Equals(item.Asset.FullPath, moved, StringComparison.OrdinalIgnoreCase) && item.Tags.Contains("client A")),
            "A tagged file moved on disk should keep its tags.");
        var copy = Path.Combine(folder, "copy of shot.png");
        File.Copy(moved, copy);
        await Eventually(() => model.Assets.Any(item => string.Equals(item.Asset.FullPath, copy, StringComparison.OrdinalIgnoreCase) && item.Tags.Contains("client A")),
            "An exact copy of a tagged file should get its tags.");

        // A live folder tag.
        model.SelectedFolderRow = model.FolderRows.Single(row => row.Name == "sorted");
        Require(model.CanTagFolder && model.FolderTagTarget == Path.Combine(folder, "sorted"), "The folder tag box should target the folder selected in the tree: " + model.FolderTagTarget);
        model.FolderTagText = "job 7";
        model.AddFolderTagsCommand.Execute(null);
        Require(Item(moved).Tags.Contains("job 7") && !Item(copy).Tags.Contains("job 7"), "A folder tag reaches the files in that folder only.");
        Require(model.FolderRows.Single(row => row.Name == "sorted").HasFolderTags, "A tagged folder should be marked in the tree.");
        var later = Path.Combine(folder, "sorted", "added later.png");
        File.Copy(Path.Combine(folder, "other.png"), later);
        await Eventually(() => model.Assets.Any(item => string.Equals(item.Asset.FullPath, later, StringComparison.OrdinalIgnoreCase) && item.Tags.Contains("job 7")),
            "A file added to a tagged folder later should carry the folder's tag.");
        model.SelectedAsset = Item(moved);
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        Require(model.InheritedTags.Any(tag => tag.Tag == "job 7") && model.SelectedTags.Contains("client A") && !model.SelectedTags.Contains("job 7"),
            "The Tags tab should list a file's own tags apart from the ones it carries from its folder.");
        model.InspectorTab = 1;
        Render(window, Path.Combine(dataDirectory, "workspace-tags.png"));

        // The overview lists the tags of the files in the folder shown; a click filters the filmstrip to exactly that tag.
        model.SelectFolder("tagging");
        var strip = (System.Windows.Data.ListCollectionView)model.LibraryView;
        Require(model.OverviewTags.Any(tag => tag.Tag == "client A" && tag.Files == 2) && model.OverviewTags.Any(tag => tag.Tag == "job 7" && tag.Files == 2),
            "The overview should count the tags in the folder: " + string.Join(", ", model.OverviewTags.Select(tag => $"{tag.Tag} {tag.Files}")));
        model.ToggleOverviewTagCommand.Execute(model.OverviewTags.Single(tag => tag.Tag == "client A"));
        Require(strip.Count == 2 && model.OverviewTags.Single(tag => tag.Tag == "client A").IsActive, $"A tag chip should filter the filmstrip to its files, not {strip.Count}.");
        model.MainTab = 0;
        Render(window, Path.Combine(dataDirectory, "workspace-overview-tags.png"));
        model.ToggleOverviewTagCommand.Execute(model.OverviewTags.Single(tag => tag.Tag == "client A"));
        Require(model.TagFilter.Length == 0 && strip.Count > 2, "Clicking the chip again should show every file again.");
    }
}
