using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MediaWorkbench.App;

internal static partial class DesktopSmokeTest
{
    /// <summary>Peeking shows a file's thumbnail in the preview at once without opening it; committing opens it and the real picture takes over.</summary>
    private static async Task CheckInstantPreviewAsync(MainViewModel model, MainWindow window, AssetViewModel photo, AssetViewModel video, string dataDirectory, CancellationToken token)
    {
        Require(MainWindow.FollowFocusX(0, 1000, 400) == 0 && MainWindow.FollowFocusX(100, 1000, 400) == 100 && MainWindow.FollowFocusX(500, 1000, 400) == 200
            && MainWindow.FollowFocusX(900, 1000, 400) == 300 && MainWindow.FollowFocusX(1000, 1000, 400) == 400 && MainWindow.FollowFocusX(0, 0, 400) == 200,
            "The filmstrip marker should rest in the middle and slide to the edges near either end.");
        var last = -1.0;
        for (var offset = 0.0; offset <= 120; offset += 1)
        {
            var content = offset + MainWindow.FollowFocusX(offset, 120, 400);
            Require(content > last, "Scrolling forward must always move the marker forward through the files, also when there is little to scroll.");
            last = content;
        }
        Require(Math.Abs(last - 520) < 1e-9, "At the far end the marker should reach the last file.");

        model.SelectedAsset = photo;
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.PreviewImage is not null, token);
        Require(photo.Thumbnail is not null && video.Thumbnail is not null, "Both thumbnails should be loaded for this check.");
        Require(model.InstantPreview is null && !model.ShowInstantLayer, "Once the real picture is ready the stand-in must be gone.");
        var opened = model.PreviewImage;

        model.PeekAsset(video);
        Require(model.IsPeeking && ReferenceEquals(model.SelectedAsset, photo) && ReferenceEquals(model.PreviewImage, opened), "Peeking must not open the file or disturb the one that is open.");
        Require(ReferenceEquals(model.InstantPreview, video.Thumbnail) && model.ShowInstantLayer && model.SelectionLabel == video.Name && model.HeaderSummary == video.Details && !model.ShowEmptyState,
            "A peek should show the thumbnail and the name of the file under the marker straight away.");
        Layout(window);
        Require(Shown(window.InstantLayer) && window.InstantLayer.ActualWidth > 400, "The stand-in picture should cover the preview while peeking.");
        Require(window.VideoTimeline.Visibility == Visibility.Collapsed, "Peeking must not rearrange the tools under the preview; that waits until the file is opened.");
        Render(window, Path.Combine(dataDirectory, "workspace-peek.png"));

        model.PeekAsset(photo);
        Require(!model.IsPeeking && model.InstantPreview is null && !model.ShowInstantLayer && model.SelectionLabel == photo.Name, "Scrolling back onto the open file should show its real picture again.");

        model.PeekAsset(video);
        model.CommitPeek();
        Require(ReferenceEquals(model.SelectedAsset, video) && !model.IsPeeking && model.ShowInstantLayer && ReferenceEquals(model.InstantPreview, video.Thumbnail), "Opening the peeked file should keep its thumbnail up until the real frame is ready.");
        await WaitUntilAsync(() => !model.IsPreviewBusy && model.PreviewImage is not null, token);
        Require(model.InstantPreview is null && !model.ShowInstantLayer && model.DisplayedFrame >= 0, "The real frame should replace the stand-in.");

        Require(window.FollowMarker.Visibility == Visibility.Collapsed, "The marker only shows while Preview follows scroll is on.");
        model.FollowFilmstrip = true;
        Layout(window);
        Require(window.FollowMarker.Visibility == Visibility.Visible && window.FollowScrollBox.IsChecked == true, "Turning the toggle on should show the filmstrip marker.");
        model.FollowFilmstrip = false;
        model.SelectedAsset = photo;
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
    }

    /// <summary>In a long filmstrip the file under the marker is the one that gets previewed.</summary>
    private static void CheckFollowMarker(MainViewModel model, MainWindow window, string dataDirectory)
    {
        model.FollowFilmstrip = true;
        Layout(window);
        window.FollowMarkerNow();
        Require(model.PeekedAsset is { } peeked && model.SelectionLabel == peeked.Name && model.ShowInstantLayer, "With follow on, the file under the marker should be previewed: " + window.FollowDiagnostics);
        var container = (FrameworkElement)window.Filmstrip.ItemContainerGenerator.ContainerFromItem(model.PeekedAsset);
        var centre = container.TranslatePoint(new Point(container.ActualWidth / 2, 0), window.Filmstrip).X;
        var marker = Canvas.GetLeft(window.FollowMarkerShape);
        Require(Math.Abs(centre - marker) <= container.ActualWidth / 2 + 6, $"The previewed file should be the one under the marker (thumbnail centre {centre:0}, marker {marker:0}).");
        Require(Math.Abs(marker - window.Filmstrip.ActualWidth / 2) < 12, "In the middle of a long filmstrip the marker should sit in the middle.");
        Render(window, Path.Combine(dataDirectory, "workspace-follow.png"));
        model.PeekedAsset = null;
        model.InstantPreview = null;
        model.FollowFilmstrip = false;
    }
}
