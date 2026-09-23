using System.IO;
using System.Windows;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

internal static partial class DesktopSmokeTest
{
    /// <summary>
    /// Nothing moves when another file is selected: the picture area, the dock under it and the button row keep exactly the same
    /// place and size for a wide video, a tall video, a photo and a sound file. The space under the picture stays compact, and the
    /// mouse wheel over the timeline steps frames.
    /// </summary>
    private static async Task CheckCompactLayoutAsync(MainViewModel model, MainWindow window, string dataDirectory, ToolPaths tools, CancellationToken token)
    {
        var directory = Path.Combine(dataDirectory, "synthetic-shapes");
        Directory.CreateDirectory(directory);
        foreach (var (name, size) in new[] { ("Tall.mkv", "270x480"), ("Wide.mkv", "480x270") })
            await new ProcessRunner().RunAsync(tools.Ffmpeg,
                ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=10:duration=2", "-f", "lavfi", "-i", "sine=frequency=330:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "pcm_s16le", Path.Combine(directory, name)],
                cancellationToken: token);
        await new ProcessRunner().RunAsync(tools.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=300x500", "-frames:v", "1", Path.Combine(directory, "Tall photo.png")], cancellationToken: token);
        await new ProcessRunner().RunAsync(tools.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=220:duration=1", Path.Combine(directory, "Tone.wav")], cancellationToken: token);
        await model.OpenLibraryAsync(directory);
        var root = (UIElement)window.Content;

        Rect? monitorPlace = null, dockPlace = null, buttonsPlace = null;
        foreach (var name in new[] { "Wide.mkv", "Tall.mkv", "Tall photo.png", "Tone.wav", "Wide.mkv" })
        {
            model.SelectedAsset = model.Assets.Single(item => item.Name == name);
            await WaitUntilAsync(() => !model.IsPreviewBusy && !model.IsWaveformLoading && (model.IsAudio || model.PreviewImage is not null), token);
            Layout(window);
            var monitor = Bounds(window.PreviewMonitor, root);
            var dock = Bounds(window.PreviewDock, root);
            var buttons = Bounds(window.ActionRow, root);
            monitorPlace ??= monitor;
            dockPlace ??= dock;
            buttonsPlace ??= buttons;
            Require(Same(monitor, monitorPlace.Value) && Same(dock, dockPlace.Value) && Same(buttons, buttonsPlace.Value),
                $"Selecting {name} moved the layout: picture {monitorPlace} became {monitor}, dock {dockPlace} became {dock}, buttons {buttonsPlace} became {buttons}.");
            if (name == "Wide.mkv")
            {
                var used = buttons.Bottom - monitor.Bottom;
                Require(used <= 175, $"The frame counter, timeline, sound and buttons should take at most 175 px under the picture, not {used:0}.");
                Require(window.VideoWaveform.ActualHeight <= 40 && window.Timeline.ActualHeight <= 56, "The sound and the timeline should stay slim.");
                Require(Shown(window.PlaybackButton) && Shown(window.RestartButton) && Shown(window.ExportFrameButton) && Shown(window.MoreActionsButton), "A video shows its play controls and its picture actions.");
                Render(window, Path.Combine(dataDirectory, "workspace-landscape.png"));
            }
            if (name == "Tall.mkv")
                Render(window, Path.Combine(dataDirectory, "workspace-portrait.png"));
        }

        model.SelectedAsset = model.Assets.Single(item => item.Name == "Tall.mkv");
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.HasFrames, token);
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
    }

    private static bool Same(Rect left, Rect right) =>
        Math.Abs(left.X - right.X) < 0.5 && Math.Abs(left.Y - right.Y) < 0.5 && Math.Abs(left.Width - right.Width) < 0.5 && Math.Abs(left.Height - right.Height) < 0.5;

    private static Rect Bounds(FrameworkElement element, UIElement root) =>
        new(element.TranslatePoint(new Point(0, 0), root), new Size(element.ActualWidth, element.ActualHeight));
}
