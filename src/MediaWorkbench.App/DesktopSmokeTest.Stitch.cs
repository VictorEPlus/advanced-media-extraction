using System.IO;
using System.Windows.Media.Imaging;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

internal static partial class DesktopSmokeTest
{
    /// <summary>
    /// Stitch: add a few pictures, combine them, and save the result. The preview and the exported file come from the same
    /// plan, so the check is that the combined size follows the arrangement and that a real file lands in the export folder.
    /// </summary>
    private static async Task CheckStitchAsync(MainViewModel model, MainWindow window, string dataDirectory, AssetViewModel photo, AssetViewModel video, CancellationToken token)
    {
        model.SelectedAsset = photo;
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        model.IsStitchTab = true;
        Require(model.ShowStitchEmpty && !model.CanStitch, "The Stitch tab should start empty.");
        Require(Shown(window.StitchTab) && !Shown(window.PreviewTab), "Choosing the Stitch tab should show it instead of the preview.");

        await model.AddToStitchCommand.ExecuteAsync(null);
        Require(model.StitchItems.Count == 1 && !model.CanStitch, "Adding the photo should put one picture in the stitch, which is not yet enough to combine.");
        Require(model.IsStitchTab, "Adding a picture must not throw the tab back to the preview.");
        await model.AddToStitchCommand.ExecuteAsync(video);
        Require(model.StitchItems.Count == 2 && model.CanStitch && model.StitchPreview is not null, "Two pictures should combine into a preview: " + model.Status);

        var first = model.StitchItems[0].Picture;
        var second = model.StitchItems[1].Picture;
        model.StitchArrangement = "Side by side";
        model.StitchGap = 0;
        model.StitchMatchSizes = true;
        var height = Math.Min(first.PixelHeight, second.PixelHeight);
        Require(model.StitchPreview is BitmapSource row && row.PixelWidth > row.PixelHeight, "Side by side should come out wider than it is tall for two landscape pictures.");
        Require(model.StitchSizeLabel.Contains('×', StringComparison.Ordinal), "The combined size should be shown: " + model.StitchSizeLabel);

        model.StitchArrangement = "Stacked";
        var stacked = StitchLayout.Compute([.. model.StitchItems.Select(entry => entry.Item)], StitchDirection.Column, 0, 3, true);
        Require(stacked.Height > height, "Stacked pictures should be taller than either picture on its own.");

        model.StitchArrangement = "Grid";
        Require(model.IsGridStitch, "The column control only belongs to the grid arrangement.");
        model.StitchColumns = 1;
        var oneColumn = model.StitchSizeLabel;
        model.StitchColumns = 2;
        Require(oneColumn != model.StitchSizeLabel, "Changing the number of columns should change the combined size.");

        model.MoveStitchItemCommand.Execute("back");
        Require(model.StitchItems[0] == model.SelectedStitchItem, "Moving a picture earlier should keep it selected.");
        model.StitchArrangement = "Side by side";
        model.StitchGap = 12;
        Render(window, Path.Combine(dataDirectory, "workspace-stitch.png"));

        model.ExportStitchCommand.Execute(null);
        await WaitUntilAsync(() => model.Jobs.All(job => job.IsFinished), token);
        var saved = model.Jobs.FirstOrDefault(job => job.Title.StartsWith("Stitch", StringComparison.Ordinal))?.OutputPath;
        Require(saved is not null && File.Exists(saved) && new FileInfo(saved).Length > 0, "The stitch should have been saved as a real file.");
        Require(MainViewModel.DecodeImage(await File.ReadAllBytesAsync(saved!, token)) is { PixelWidth: > 0 }, "The saved stitch should be a readable picture.");

        model.RemoveFromStitchCommand.Execute(null);
        Require(model.StitchItems.Count == 1, "Remove should take out exactly one picture.");
        model.ClearStitchCommand.Execute(null);
        Require(model.ShowStitchEmpty && model.StitchPreview is null, "Clearing the stitch should empty the preview too.");
        model.IsPreviewTab = true;
    }
}
