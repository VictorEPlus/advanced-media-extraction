using System.IO;
using System.Windows;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

internal static partial class DesktopSmokeTest
{
    /// <summary>Crop and rotate for a whole video: edges adjusted by keyboard, turned, auto-fitted to the picture and exported.</summary>
    private static async Task CheckVideoCropAsync(MainViewModel model, MainWindow window, string dataDirectory, ToolPaths tools, CancellationToken token)
    {
        var directory = Path.Combine(dataDirectory, "synthetic-letterbox");
        Directory.CreateDirectory(directory);
        // A 320 x 180 picture inside a 480 x 360 black frame.
        await new ProcessRunner().RunAsync(tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=3", "-vf", "pad=480:360:80:90:black", "-c:v", "libx264", "-pix_fmt", "yuv420p", Path.Combine(directory, "Letterboxed.mkv")],
            cancellationToken: token);
        await model.OpenLibraryAsync(directory);
        model.SelectedAsset = model.Assets.Single();
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.HasFrames && model.PreviewImage is not null, token);
        Require(model.CanFrame && !model.HasVideoTransform && !model.ShowFramingTools && !model.ShowTransformReminder, "A newly opened video starts uncropped and unturned.");

        model.IsCropping = true;
        model.ToggleFramingCommand.Execute(null);
        Layout(window);
        Require(model.IsFraming && !model.IsCropping && Shown(window.FramingTools) && window.PreviewSurface.IsEdgeEditing, "Opening the video crop tools should show them and switch the clipboard crop off.");
        Require(!window.ExportWholeVideoButton.IsEnabled, "There is nothing to export until an edge or the rotation changes.");

        var surface = window.PreviewSurface;
        Require(!surface.Nudge(5) && model.VideoCrop is null, "Arrow keys must not move anything until an edge is chosen.");
        surface.CycleEdge(1);
        Require(model.SelectedCropEdge == CropEdge.Left && model.FramingEdgeHint.StartsWith("Left edge selected", StringComparison.Ordinal), "Tab should choose the left edge first.");
        surface.Nudge(10);
        surface.Nudge(1);
        Require(model.VideoCrop == new PixelCrop(11, 0, 469, 360), $"Nudging the left edge by 10 and 1 should cut 11 pixels: {model.VideoCrop}");
        surface.Nudge(-500);
        Require(model.VideoCrop == new PixelCrop(0, 0, 480, 360) && !model.HasVideoTransform, "An edge stops at the frame.");
        surface.CycleEdge(1);
        surface.Nudge(7);
        Require(model.SelectedCropEdge == CropEdge.Top && model.VideoCrop == new PixelCrop(0, 7, 480, 353), "The next edge is the top one, and down is positive.");

        model.RotateVideoRightCommand.Execute(null);
        Require(model.VideoRotation == 90 && model.SelectedCropEdge == CropEdge.None, "Turning the video lets go of the chosen edge, because the edges change places.");
        surface.CycleEdge(-1);
        surface.Nudge(-20);
        // Turned a quarter clockwise, the bottom edge on screen is the right edge of the source.
        Require(model.SelectedCropEdge == CropEdge.Bottom && model.VideoCrop == new PixelCrop(0, 7, 460, 353), $"On a turned video the chosen edge must still be the one seen on screen: {model.VideoCrop}");
        Require(model.FramingSummary.StartsWith("Exports 352 × 460", StringComparison.Ordinal) && model.FramingSummary.Contains("turned 90° clockwise", StringComparison.Ordinal), "The summary should give the exported size, even and turned: " + model.FramingSummary);
        Layout(window);
        Render(window, Path.Combine(dataDirectory, "workspace-video-crop-turned.png"));

        model.ResetVideoTransformCommand.Execute(null);
        await model.AutoFitCropCommand.ExecuteAsync(null);
        Require(model.VideoCrop is { } fitted && Math.Abs(fitted.X - 80) <= 2 && Math.Abs(fitted.Y - 90) <= 2 && Math.Abs(fitted.Width - 320) <= 4 && Math.Abs(fitted.Height - 180) <= 4,
            $"Auto-fit should put the edges on the picture inside the black bars: {model.VideoCrop} ({model.Status})");
        await WaitUntilAsync(() => model.AutoFitCropCommand.CanExecute(null), token);
        Layout(window);
        Require(window.AutoFitButton.IsEnabled && (string)window.AutoFitButton.Content == "Auto-fit edges", $"Auto-fit should be available again once it has finished (can execute {model.AutoFitCropCommand.CanExecute(null)}, running {model.AutoFitCropCommand.IsRunning}, enabled {window.AutoFitButton.IsEnabled}, content {window.AutoFitButton.Content}).");
        model.CurrentFrame = 12;
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        Require(model.VideoCrop is not null && model.IsFraming && model.DisplayedFrame == 12, "Stepping through the video keeps the crop so it can be checked at other moments.");
        Layout(window);
        Render(window, Path.Combine(dataDirectory, "workspace-video-crop.png"));

        model.ToggleFramingCommand.Execute(null);
        Layout(window);
        Require(!model.IsFraming && model.HasVideoTransform && model.ShowTransformReminder && Shown(window.TransformReminder) && !Shown(window.FramingTools) && !window.PreviewSurface.IsEdgeEditing,
            "Closing the tools keeps the crop and leaves a reminder that exports will use it.");
        Require(model.TrimLabel.Contains("cropped", StringComparison.Ordinal), "The trim button should say that it will crop.");

        var before = model.JobHistory.Count;
        model.VideoRotation = 270;
        model.ExportWholeVideoCommand.Execute(null);
        await WaitUntilAsync(() => model.JobHistory.Count > before && model.ActiveJobCount == 0, token);
        var record = model.JobHistory[0];
        Require(record.Succeeded && File.Exists(record.OutputPath) && Path.GetFileName(record.OutputPath)!.StartsWith("Letterboxed_edit", StringComparison.Ordinal), "Export whole video should write a new MP4: " + record.Status);
        var exported = await new MediaEngine(tools, Path.Combine(dataDirectory, "cache")).ProbeAsync(record.OutputPath!, token);
        var expected = new VideoTransform(model.VideoCrop, 270).OutputSize(480, 360);
        Require(exported.Metadata["width"] == expected.Width.ToString() && exported.Metadata["height"] == expected.Height.ToString() && expected.Width < expected.Height,
            $"The exported video should be the cropped picture turned on its side, not {exported.Metadata["width"]} x {exported.Metadata["height"]}.");

        var other = model.Assets.Single();
        model.SelectedAsset = null;
        model.SelectedAsset = other;
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        Require(model.VideoCrop is null && model.VideoRotation == 0 && !model.IsFraming, "Selecting a file starts with no crop and no rotation.");
    }
}
