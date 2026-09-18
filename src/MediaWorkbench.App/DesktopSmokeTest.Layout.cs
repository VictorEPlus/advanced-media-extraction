using System.IO;
using System.Windows;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

internal static partial class DesktopSmokeTest
{
    /// <summary>
    /// The space under the preview stays compact, a portrait video gets its buttons beside the picture instead of under it,
    /// and the mouse wheel over the preview or timeline steps frames.
    /// </summary>
    private static async Task CheckCompactLayoutAsync(MainViewModel model, MainWindow window, string dataDirectory, ToolPaths tools, CancellationToken token)
    {
        var directory = Path.Combine(dataDirectory, "synthetic-shapes");
        Directory.CreateDirectory(directory);
        foreach (var (name, size) in new[] { ("Tall.mkv", "270x480"), ("Wide.mkv", "480x270") })
            await new ProcessRunner().RunAsync(tools.Ffmpeg,
                ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=10:duration=2", "-f", "lavfi", "-i", "sine=frequency=330:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "pcm_s16le", Path.Combine(directory, name)],
                cancellationToken: token);
        await model.OpenLibraryAsync(directory);
        var root = (UIElement)window.Content;

        model.SelectedAsset = model.Assets.Single(item => item.Name == "Wide.mkv");
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.HasFrames && !model.IsWaveformLoading, token);
        Layout(window);
        var monitor = Bounds(window.PreviewMonitor, root);
        var actions = Bounds(window.ActionRow, root);
        Require(!model.IsPortraitLayout && actions.Top >= monitor.Bottom && actions.Left < monitor.Left + 1, "A landscape video keeps its buttons under the preview.");
        var used = actions.Bottom - monitor.Bottom;
        Require(used <= 175, $"The frame counter, timeline, sound and buttons should take at most 175 px under a landscape video, not {used:0}.");
        Require(window.VideoWaveform.ActualHeight <= 40 && window.Timeline.ActualHeight <= 56, "The sound and the timeline should stay slim.");
        Render(window, Path.Combine(dataDirectory, "workspace-landscape.png"));
        var landscapeHeight = monitor.Height;

        model.SelectedAsset = model.Assets.Single(item => item.Name == "Tall.mkv");
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.HasFrames && !model.IsWaveformLoading, token);
        Layout(window);
        monitor = Bounds(window.PreviewMonitor, root);
        actions = Bounds(window.ActionRow, root);
        Require(model.IsPortraitLayout && actions.Left >= monitor.Right && actions.Top < monitor.Top + 1 && actions.Bottom <= monitor.Bottom + 1,
            $"A portrait video should have its buttons beside the preview (monitor {monitor}, buttons {actions}).");
        Require(monitor.Height >= landscapeHeight + 30, $"Moving the buttons aside should give a portrait video more height ({monitor.Height:0} against {landscapeHeight:0}).");
        Require(Shown(window.PlaybackButton) && Shown(window.FramingButton) && Shown(window.ExportFrameButton) && Shown(window.SnipAudioButton) && window.FramingButton.ActualWidth >= 160,
            "Every button should still be there in the side column, full width.");
        Render(window, Path.Combine(dataDirectory, "workspace-portrait.png"));

        model.CurrentFrame = 5;
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        window.TurnFrameWheel(-120);
        Require(model.CurrentFrame == 6, "One notch of the wheel towards you should go to the next frame.");
        window.TurnFrameWheel(-60);
        Require(model.CurrentFrame == 6, "Half a notch is not yet a frame.");
        window.TurnFrameWheel(-60);
        Require(model.CurrentFrame == 7, "Fractions of a notch from smooth wheels add up to whole frames.");
        window.TurnFrameWheel(360);
        Require(model.CurrentFrame == 4, "Three notches away from you should go back three frames.");
        window.TurnFrameWheel(120 * 50);
        Require(model.CurrentFrame == 0, "The wheel stops at the first frame.");
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);

        model.SelectedAsset = model.Assets.Single(item => item.Name == "Wide.mkv");
        Require(model.IsPortraitLayout, "While the next file is loading the layout should not flip back and forth.");
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.PreviewImage is not null, token);
        Require(!model.IsPortraitLayout, "Once a landscape picture is showing the buttons go back under it.");
    }

    private static Rect Bounds(FrameworkElement element, UIElement root) =>
        new(element.TranslatePoint(new Point(0, 0), root), new Size(element.ActualWidth, element.ActualHeight));
}
