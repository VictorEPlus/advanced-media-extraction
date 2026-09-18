using System.IO;
using System.Windows;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

internal static partial class DesktopSmokeTest
{
    /// <summary>
    /// Zoom stays on the same spot while stepping frames and resets for another file; focus view puts everything but the preview
    /// away and brings it back exactly as it was. Expects the folder with Tall.mkv and Wide.mkv to be open.
    /// </summary>
    private static async Task CheckZoomAndFocusAsync(MainViewModel model, MainWindow window, string dataDirectory, CancellationToken token)
    {
        var wide = model.Assets.Single(item => item.Name == "Wide.mkv");
        var tall = model.Assets.Single(item => item.Name == "Tall.mkv");
        model.SelectedAsset = wide;
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.HasFrames && model.PreviewImage is not null, token);
        Layout(window);
        var surface = window.PreviewSurface;
        Require(!surface.IsZoomed && surface.Zoom == 1, "A newly opened file is shown fitted.");

        // Zoom in on a spot in the upper left quarter of the picture; that spot must stay under the pointer.
        var anchor = new Point(surface.ActualWidth * 0.4, surface.ActualHeight * 0.35);
        var spot = surface.PicturePointAt(anchor);
        surface.ZoomAt(anchor, 2);
        surface.ZoomAt(anchor, 2);
        var after = surface.PicturePointAt(anchor);
        Require(Math.Abs(surface.Zoom - 4) < 1e-9 && Math.Abs(after.X - spot.X) < 0.002 && Math.Abs(after.Y - spot.Y) < 0.002, $"Zooming should keep the spot under the pointer where it is ({spot} became {after}).");
        var corner = surface.PicturePointAt(new Point(0, 0));
        Require(corner.X > 0.05 && corner.Y > 0.05, "Zoomed in, the view should show a part of the picture, not its corner.");

        model.CurrentFrame = 7;
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.DisplayedFrame == 7, token);
        window.TurnFrameWheel(-120);
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.DisplayedFrame == 8, token);
        var later = surface.PicturePointAt(anchor);
        Require(Math.Abs(surface.Zoom - 4) < 1e-9 && Math.Abs(later.X - spot.X) < 0.002 && Math.Abs(later.Y - spot.Y) < 0.002, "Stepping frames must keep the zoom and the place.");

        // A clipboard crop dragged while zoomed must still come out in real picture pixels.
        model.IsCropping = true;
        Layout(window);
        Render(window, Path.Combine(dataDirectory, "workspace-zoomed.png"));
        model.IsCropping = false;

        surface.ZoomAt(anchor, 1000);
        Require(Math.Abs(surface.Zoom - CropSurface.MaximumZoom) < 1e-9, "Zoom stops at its maximum.");
        surface.ZoomAt(anchor, 0.0001);
        Require(!surface.IsZoomed, "Zooming all the way out ends fitted.");
        var fitted = surface.PicturePointAt(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
        Require(Math.Abs(fitted.X - 0.5) < 0.002 && Math.Abs(fitted.Y - 0.5) < 0.002, "Fitted, the picture is centred again.");

        surface.ZoomAt(anchor, 3);
        model.SelectedAsset = tall;
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.PreviewImage is not null, token);
        Require(!surface.IsZoomed, "Another file starts fitted; the old zoom meant a place in the old picture.");

        // Focus view.
        model.ShowSources = true;
        model.ShowInspector = true;
        Layout(window);
        var root = (UIElement)window.Content;
        var normal = Bounds(window.PreviewMonitor, root);
        model.ToggleFocusViewCommand.Execute(null);
        Layout(window);
        var focused = Bounds(window.PreviewMonitor, root);
        Require(model.IsFocusView && !Shown(window.HeaderBar) && !Shown(window.FilterBar) && !Shown(window.FilmstripPanel) && !Shown(window.SourcesPanel) && !Shown(window.InspectorPanel) && !Shown(window.CenterHeader),
            "Focus view should put away the top bar, filters, tabs, side panels and filmstrip.");
        Require(Shown(window.PreviewMonitor) && Shown(window.Timeline) && Shown(window.ExportFrameButton) && Shown(window.ExitFocusButton) && !Shown(window.FocusViewButton) && Shown(window.PlaybackButton),
            "Focus view keeps the picture, the timeline, export and the way back.");
        Require(focused.Width > normal.Width + 300 && focused.Height > normal.Height + 150, $"Focus view should give the picture much more room ({normal.Size} became {focused.Size}).");
        surface.ZoomAt(new Point(surface.ActualWidth / 2, surface.ActualHeight / 3), 2.5);
        model.CurrentFrame = 3;
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.DisplayedFrame == 3, token);
        Require(surface.IsZoomed && model.CanExportFrame, "In focus view you can zoom, step frames and export.");
        Layout(window);
        Render(window, Path.Combine(dataDirectory, "workspace-focus.png"));
        model.ToggleFocusViewCommand.Execute(null);
        Layout(window);
        var restored = Bounds(window.PreviewMonitor, root);
        Require(!model.IsFocusView && model.ShowSources && model.ShowInspector && Shown(window.HeaderBar) && Shown(window.FilmstripPanel) && Shown(window.InspectorPanel) && Math.Abs(restored.Width - normal.Width) < 1 && Math.Abs(restored.Height - normal.Height) < 1,
            "Leaving focus view should bring everything back as it was.");
        Require(surface.IsZoomed, "Leaving focus view keeps the zoom.");
        Require(Shown(window.FocusViewButton) && !Shown(window.ExitFocusButton), "Outside focus view the way in is beside Favorite and Stage, and the button row has no extra button.");
        surface.ResetView();
    }
}
