using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

internal static partial class DesktopSmokeTest
{
    public static async Task RunAsync(MainViewModel viewModel, MainWindow window, string dataDirectory)
    {
        CheckDarkTheme(window);
        Render(window, Path.Combine(dataDirectory, "desktop-empty.png"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var tools = ToolPaths.Resolve();
        await tools.CheckAsync(timeout.Token);
        var mediaDirectory = Path.Combine(dataDirectory, "synthetic-media");
        Directory.CreateDirectory(mediaDirectory);
        var video = Path.Combine(mediaDirectory, "Sample video.mkv");
        await new ProcessRunner().RunAsync(tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=6:duration=1", "-f", "lavfi", "-i", "aevalsrc=if(lt(t\\,0.5)\\,0.05*sin(2*PI*90*t)\\,0.6*sin(2*PI*330*t)):s=44100:d=1", "-c:v", "libx264", "-c:a", "pcm_s16le", video], cancellationToken: timeout.Token);
        var photo = Path.Combine(mediaDirectory, "Sample photo.png");
        await new ProcessRunner().RunAsync(tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-i", video, "-frames:v", "1", photo], cancellationToken: timeout.Token);
        viewModel.ExportDirectory = Path.Combine(dataDirectory, "exports");
        await viewModel.SaveSettingsCommand.ExecuteAsync(null);
        await viewModel.OpenLibraryAsync(mediaDirectory);
        Require(viewModel.Assets.Count == 2, "The desktop scan did not discover both generated files.");
        Require(viewModel.IsLibraryTab && viewModel.FolderRows.Count == 1 && viewModel.FolderRows[0].Node.Total == 2, "Opening a folder should show the Library tab with its folder tree.");
        var videoItem = viewModel.Assets.Single(item => item.Asset.Kind == MediaKind.Video);
        var photoItem = viewModel.Assets.Single(item => item.Asset.Kind == MediaKind.Photo);
        viewModel.SelectedAsset = videoItem;
        await WaitUntilAsync(() => !viewModel.IsPreviewBusy, timeout.Token);
        Require(viewModel.HasFrames && viewModel.MaximumFrame == 5, "Video frame indexing failed: " + viewModel.Status);
        Require(viewModel.IsPreviewTab, "Selecting a file should switch the centre to the Preview tab.");
        viewModel.CurrentFrame = 3;
        await WaitUntilAsync(() => !viewModel.IsPreviewBusy, timeout.Token);
        Require(viewModel.CanExportFrame && viewModel.DisplayedFrame == 3 && !viewModel.IsPreviewStale, "Exact frame preview failed: " + viewModel.Status);
        Require(viewModel.PreviewImage is not null, "The previous frame must stay visible while the next one decodes.");
        await CheckVideoSoundAsync(viewModel, window, dataDirectory, timeout.Token);
        await CheckPauseMatchingAsync(viewModel, dataDirectory, tools, timeout.Token);
        Require(Directory.GetFiles(Path.Combine(dataDirectory, "cache"), "*.png").Length >= 6, "A cache miss should decode a window of neighbouring frames in one pass.");
        Require(Directory.GetFiles(Path.Combine(dataDirectory, "cache"), "*.index.json").Length == 1, "The frame index should be persisted for the file identity.");
        viewModel.SearchText = "no such file";
        Require(viewModel.SelectedAsset == videoItem && viewModel.IsSelectionHidden && window.Filmstrip.SelectedItem is null, "A filter that hides the selected file must keep it open, only clearing the filmstrip highlight.");
        Require(viewModel.HasFrames && viewModel.CanExportFrame, "Filtering must not tear down the loaded preview.");
        viewModel.SearchText = "";
        Require(!viewModel.IsSelectionHidden && ReferenceEquals(window.Filmstrip.SelectedItem, videoItem), "Clearing the filter should restore the filmstrip highlight.");
        viewModel.ToggleFavoriteCommand.Execute(null);
        Require(videoItem.IsFavorite, "Video favorite did not toggle.");
        Require(videoItem.FavoriteLabel == "\u2605", "The favorite star is incorrectly encoded.");
        Require(videoItem.Placeholder == "\u25B6" && photoItem.Placeholder == "\u25A7", "Media placeholder symbols are incorrectly encoded.");
        Require(viewModel.LibraryLabel == "2 items \u00B7 1 favorites", "The library separator is incorrectly encoded.");
        viewModel.FavoritesOnly = true;
        Require(viewModel.LibraryView.Cast<AssetViewModel>().Count() == 1, "Favorites filter returned the wrong items.");
        viewModel.FavoritesOnly = false;
        viewModel.InspectorTab = 0;
        viewModel.ExportFrameCommand.Execute(null);
        Require(viewModel.Jobs.Count == 1, "Frame export was not queued.");
        Require(viewModel.InspectorTab == 0 && viewModel.ExportBadge == "Export (1)", "Queueing an export must badge the Export tab instead of switching to it.");
        viewModel.SelectedAsset = photoItem;
        await WaitUntilAsync(() => !viewModel.IsPreviewBusy && viewModel.Jobs.All(job => job.IsFinished), timeout.Token);
        Require(viewModel.Jobs[0].OutputPath is { } path && Path.GetFileName(path).Contains("frame_000003", StringComparison.Ordinal), "Export did not retain the selected video frame when browsing away.");
        Require(viewModel.Notifications.Any(notification => notification.Kind == NotificationKind.Success && notification.HasAction), "A finished export should raise a success notification with an Open output action.");
        Require(viewModel.JobHistory.Count == 1 && viewModel.JobHistory[0].Succeeded && File.Exists(Path.Combine(dataDirectory, "export-history.json")), "Finished exports must be recorded in persistent history.");
        Require(viewModel.RecentLibraries.Any(entry => entry.Path == mediaDirectory), "The opened folder should appear in recent libraries.");
        Require(viewModel.ExportBadge == "Export", "The Export badge should clear when no jobs are active.");
        Require(viewModel.CanExportFrame, "Photo preview failed: " + viewModel.Status);
        viewModel.ToggleFavoriteCommand.Execute(null);
        var stored = new CatalogStore(Path.Combine(dataDirectory, "catalog.db")).GetFavorites(mediaDirectory);
        Require(stored.Count == 2, "Photo/video favorites were not both persisted.");
        viewModel.SelectedAsset = videoItem;
        viewModel.SelectedAsset = photoItem;
        await WaitUntilAsync(() => !viewModel.IsPreviewBusy, timeout.Token);
        Require(!viewModel.HasFrames && viewModel.CanExportFrame, "Rapid selection allowed stale video state to replace a photo.");
        foreach (var item in viewModel.Assets)
            await viewModel.LoadThumbnailAsync(item);
        Render(window, Path.Combine(dataDirectory, "desktop.png"));
        await CheckInstantPreviewAsync(viewModel, window, photoItem, videoItem, dataDirectory, timeout.Token);
        await CheckWorkspaceAsync(viewModel, window, dataDirectory, mediaDirectory, photoItem, videoItem, timeout.Token);
        await CheckAudioFileAsync(viewModel, window, dataDirectory, tools, timeout.Token);
        await CheckVideoCropAsync(viewModel, window, dataDirectory, tools, timeout.Token);
        await CheckCompactLayoutAsync(viewModel, window, dataDirectory, tools, timeout.Token);
        await CheckZoomAndFocusAsync(viewModel, window, dataDirectory, timeout.Token);
        await CheckStitchAsync(viewModel, window, dataDirectory, photoItem, videoItem, timeout.Token);
        Require(viewModel.Notifications.All(notification => notification.Kind != NotificationKind.Error),
            "The scenario raised an unexpected error notification: " + string.Join(" | ", viewModel.Notifications.Where(notification => notification.Kind == NotificationKind.Error).Select(notification => notification.Message)));
        CheckLibraryAndTour(viewModel, window, dataDirectory, photoItem);
        var errorWindow = new StartupErrorWindow("Synthetic startup error for theme verification.");
        CheckDarkTheme(errorWindow);
        Render(errorWindow, Path.Combine(dataDirectory, "startup-dialog.png"));
    }

    /// <summary>Library tab: folder tree, folder filter and graph. Then every tour step must point at a real element.</summary>
    private static void CheckLibraryAndTour(MainViewModel model, MainWindow window, string dataDirectory, AssetViewModel photo)
    {
        (string Folder, int Count, MediaKind Kind)[] layout =
        [
            ("", 3, MediaKind.Photo), (@"shoot A\day 1", 40, MediaKind.Photo), (@"shoot A\day 2", 25, MediaKind.Photo), (@"shoot A\day 2", 5, MediaKind.Video),
            ("shoot B", 12, MediaKind.Video), ("shoot B", 8, MediaKind.Audio), (@"deep\only\chain\here", 6, MediaKind.Photo)
        ];
        var number = 0;
        model.ClearFiltersCommand.Execute(null);
        model.SelectedAsset = null;
        model.Assets.Clear();
        model.Assets.AddRange(layout.SelectMany(entry => Enumerable.Range(0, entry.Count).Select(_ =>
            new AssetViewModel(photo.Asset with { RelativePath = Path.Combine(entry.Folder, $"file{++number}.bin"), Kind = entry.Kind }, false) { FolderKey = FolderTree.Normalize(entry.Folder) })));
        model.RebuildFolderTree();
        model.MainTab = 0;
        var view = (System.Windows.Data.ListCollectionView)model.LibraryView;
        Require(model.IsLibraryTab && model.ShowLibraryMap, "The Library tab should show the folder map once media has been found.");
        Require(model.FolderRows.Count == 4 && model.FolderRows[0].Node.Total == 99, "The folder tree should list the root and its three top-level folders with totals that include subfolders.");
        Require(model.FolderRows.Any(row => row.Name == @"deep\only\chain\here" && row.Node.Total == 6), "Folders that only lead to one subfolder should collapse into a single row.");
        Require(model.ChartNodes.Count == 4 && model.ChartNodes[^1].Total == 3, "The graph should chart each top-level folder plus the files directly in the root.");
        model.SelectFolder("shoot A");
        Require(model.HasFolderFilter && view.Count == 70 && model.ChartNodes.Count == 2, "Selecting a folder must narrow the filmstrip to it and chart its subfolders.");
        model.ToggleFolderRowCommand.Execute(model.SelectedFolderRow);
        Require(model.FolderRows.Count == 6 && model.SelectedFolderRow?.Node.Path == "shoot A", "Opening a folder should reveal its subfolders and keep it selected.");
        model.ChartSelectedPath = @"shoot A\day 2";
        Require(view.Count == 30 && model.SelectedFolderRow?.Node.Path == @"shoot A\day 2" && model.ChartSelectedPath is null, "Clicking a graph bar should go into that folder.");
        model.SelectFolder("shoot A");
        Render(window, Path.Combine(dataDirectory, "workspace-library.png"));
        Require(model.VisibleCount == "70 of 99 files in shoot A" && window.VisibleCountText.ActualWidth > 100, $"The file count should name the folder being shown: {model.VisibleCount}");
        model.ClearFiltersCommand.Execute(null);
        Require(!model.HasFolderFilter && view.Count == 99, "Clear filters should also clear the folder filter.");

        // Flattening a folder promotes its subfolders to the top level; removing one takes a branch out. Both are ways of
        // looking at the same scan, so the file count follows and nothing on disk is touched.
        var shootA = model.FolderRows.Single(row => row.Node.Path == "shoot A");
        model.FlattenFolderCommand.Execute(shootA);
        Require(model.ShowFolderEdits && model.FolderEditsLabel == "Flattened to shoot A", "Flattening should say what it did: " + model.FolderEditsLabel);
        Require(view.Count == 70, $"Flattening to a folder should leave only its files in the filmstrip, not {view.Count}.");
        Require(model.FolderRows.Count == 3 && model.FolderRows.Any(row => row.Name == "day 1") && model.FolderRows.Any(row => row.Name == "day 2"),
            "The flattened folder's own subfolders should now be the top level.");
        model.RemoveFolderCommand.Execute(model.FolderRows.Single(row => row.Name == "day 1"));
        Require(view.Count == 30 && model.FolderEditsLabel == "Flattened to shoot A, 1 folder removed", "Removing a folder should take its files out as well: " + model.FolderEditsLabel);
        model.ShowAllFoldersCommand.Execute(null);
        Require(!model.ShowFolderEdits && view.Count == 99 && model.FolderRows.Count == 4, "Show all folders should put the whole library back.");

        window.StartTour();
        Require(window.IsTourActive && window.TourLayer.Visibility == Visibility.Visible, "The tour overlay should appear.");
        for (var index = 0; index < MainWindow.TourSteps.Count; index++)
            Require(window.ShowTourStep(index), $"Tour step {index + 1} names an element that does not exist: {MainWindow.TourSteps[index].Target}");
        Require(MainWindow.TourSteps.Select(step => step.Target).Distinct().Count() == MainWindow.TourSteps.Count, "Each tour step should explain a different element.");
        window.ShowTourStep(MainWindow.TourSteps.ToList().FindIndex(step => step.Target == "FolderTreePanel"));
        Render(window, Path.Combine(dataDirectory, "tour.png"));
        window.EndTour();
        Require(!window.IsTourActive && window.TourLayer.Visibility == Visibility.Collapsed && model.IsLibraryTab, "Ending the tour should remove the overlay and restore the previous view.");
    }

    private static async Task WaitUntilAsync(Func<bool> ready, CancellationToken cancellationToken)
    {
        while (!ready())
            await Task.Delay(20, cancellationToken);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Render(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var width = window is MainWindow ? 1440 : 780;
        var height = window is MainWindow ? 900 : 540;
        // Two passes: an offscreen tree has no dispatcher-driven layout loop, so auto-sized columns whose content
        // and visibility changed together only settle on the second pass. A shown window does this by itself.
        for (var pass = 0; pass < 2; pass++)
        {
            content.InvalidateMeasure();
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
        }
        CheckControlSurfaces(content);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void CheckDarkTheme(Window window)
    {
        Require(window.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(22, 19, 38), "The main window lost its dark canvas background.");
        Require(window.Foreground is SolidColorBrush foreground && foreground.Color == Color.FromRgb(237, 234, 255), "The main window lost its readable foreground.");
    }

    private static void CheckControlSurfaces(DependencyObject parent)
    {
        foreach (var child in VisualChildren(parent))
        {
            Brush? background = child switch
            {
                Control control => control.Background,
                Border border => border.Background,
                Panel panel => panel.Background,
                _ => null
            };
            if (background is SolidColorBrush solid && solid.Color.A > 225)
                Require(solid.Color.R < 225 || solid.Color.G < 225 || solid.Color.B < 225, $"Unexpected white background {solid.Color} on {child.GetType().Name} ({(child as ContentControl)?.Content}, {(child as FrameworkElement)?.Name}).");
            if (child is ComboBox combo)
            {
                combo.ApplyTemplate();
                if (combo.Template.FindName("PART_Popup", combo) is Popup { Child: { } popupChild })
                {
                    popupChild.Measure(new Size(300, 200));
                    popupChild.Arrange(new Rect(0, 0, 300, 200));
                    popupChild.UpdateLayout();
                    CheckControlSurfaces(popupChild);
                }
            }
            if (child is TextBlock text)
                foreach (var marker in new[] { "\u00C2\u00B7", "\u00E2\u20AC", "\u00E2\u02DC", "\uFFFD" })
                    Require(!text.Text.Contains(marker, StringComparison.Ordinal), "A rendered label contains corrupted Unicode.");
        }
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
    {
        yield return parent;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in VisualChildren(VisualTreeHelper.GetChild(parent, index)))
                yield return child;
    }
}
