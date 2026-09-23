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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(240));
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
        Require(viewModel.IsLibraryTab && viewModel.FolderRows.Count == 2 && viewModel.FolderRows[1].IsWorkspaceFolder && viewModel.FolderRows[0].Node.Total == 2, "Opening a folder should show the Overview with the folder in the workspace tree.");
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
        Require(viewModel.InspectorTab == 0 && viewModel.ExportBadge == "EXPORT (1)", "Queueing an export must badge the Export tab instead of switching to it.");
        viewModel.SelectedAsset = photoItem;
        await WaitUntilAsync(() => !viewModel.IsPreviewBusy && viewModel.Jobs.All(job => job.IsFinished), timeout.Token);
        Require(viewModel.Jobs[0].OutputPath is { } path && Path.GetFileName(path).Contains("frame_000003", StringComparison.Ordinal), "Export did not retain the selected video frame when browsing away.");
        Require(viewModel.Notifications.Any(notification => notification.Kind == NotificationKind.Success && notification.HasAction), "A finished export should raise a success notification with an Open output action.");
        Require(viewModel.JobHistory.Count == 1 && viewModel.JobHistory[0].Succeeded && File.Exists(Path.Combine(dataDirectory, "export-history.json")), "Finished exports must be recorded in persistent history.");
        Require(viewModel.RecentLibraries.Any(entry => entry.Path == mediaDirectory), "The opened folder should appear in recent libraries.");
        Require(viewModel.ExportBadge == "EXPORT", "The Export badge should clear when no jobs are active.");
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
        await CheckTagsAsync(viewModel, window, dataDirectory, tools, timeout.Token);
        Require(viewModel.Notifications.All(notification => notification.Kind != NotificationKind.Error),
            "The scenario raised an unexpected error notification: " + string.Join(" | ", viewModel.Notifications.Where(notification => notification.Kind == NotificationKind.Error).Select(notification => notification.Message)));
        await CheckWorkspaceAndTourAsync(viewModel, window, dataDirectory, timeout.Token);
        var errorWindow = new StartupErrorWindow("Synthetic startup error for theme verification.");
        CheckDarkTheme(errorWindow);
        Render(errorWindow, Path.Combine(dataDirectory, "startup-dialog.png"));
    }

    /// <summary>
    /// The workspace: two folders open at once, each with its own tree; tabs flip between folder views without rescanning and
    /// without closing the open file; a folder opened before comes back at once from its saved index; changes on disk are picked
    /// up while the app runs. Then every tour step must point at a real element.
    /// </summary>
    private static async Task CheckWorkspaceAndTourAsync(MainViewModel model, MainWindow window, string dataDirectory, CancellationToken token)
    {
        (string Folder, int Count, string Extension)[] layout =
        [
            ("", 3, ".png"), (@"shoot A\day 1", 40, ".png"), (@"shoot A\day 2", 25, ".png"), (@"shoot A\day 2", 5, ".mp4"),
            ("shoot B", 12, ".mp4"), ("shoot B", 8, ".wav"), (@"deep\only\chain\here", 6, ".png")
        ];
        var shoots = Path.Combine(dataDirectory, "workspace", "Shoots");
        var archive = Path.Combine(dataDirectory, "workspace", "Archive");
        var number = 0;
        foreach (var (folder, count, extension) in layout)
        {
            Directory.CreateDirectory(Path.Combine(shoots, folder));
            for (var index = 0; index < count; index++)
                File.WriteAllBytes(Path.Combine(shoots, folder, $"file{++number}{extension}"), [1, 2, 3]);
        }
        Directory.CreateDirectory(Path.Combine(archive, "old"));
        for (var index = 0; index < 10; index++)
            File.WriteAllBytes(Path.Combine(archive, "old", $"scan{index}.png"), [1, 2, 3]);

        model.ClearFiltersCommand.Execute(null);
        await model.OpenLibraryAsync(shoots);
        model.MainTab = 0;
        var view = (System.Windows.Data.ListCollectionView)model.LibraryView;
        Require(model.IsLibraryTab && model.ShowLibraryMap, "The Library tab should show the folder map once media has been found.");
        Require(model.FolderRows.Count == 5 && model.FolderRows[0].IsAllFolders && model.FolderRows[1].IsWorkspaceFolder && model.FolderRows[1].Node.Total == 99,
            $"The tree should show All folders, then the workspace folder open to its three top-level folders, not {model.FolderRows.Count} rows.");
        Require(model.FolderRows.Any(row => row.Name == @"deep\only\chain\here" && row.Node.Total == 6), "Folders that only lead to one subfolder should collapse into a single row.");
        model.SelectFolder(@"Shoots\shoot A");
        Require(model.HasFolderFilter && view.Count == 70 && model.ChartNodes.Count == 2, "Selecting a folder must narrow the filmstrip to it and chart its subfolders.");
        model.ToggleFolderRowCommand.Execute(model.SelectedFolderRow);
        Require(model.FolderRows.Count == 7 && model.SelectedFolderRow?.Node.Path == @"Shoots\shoot A", "Opening a folder should reveal its subfolders and keep it selected.");
        model.ChartSelectedPath = @"Shoots\shoot A\day 2";
        Require(view.Count == 30 && model.SelectedFolderRow?.Node.Path == @"Shoots\shoot A\day 2" && model.ChartSelectedPath is null, "Clicking a graph bar should go into that folder.");
        model.SelectFolder(@"Shoots\shoot A");
        Render(window, Path.Combine(dataDirectory, "workspace-library.png"));
        Require(model.VisibleCount == @"70 of 99 files in Shoots\shoot A", $"The file count should name the folder being shown: {model.VisibleCount}");

        // A second folder joins the workspace in a tab of its own; the first tab stays where it was.
        model.NewTabCommand.Execute(null);
        var archiveFolder = await model.AddFolderAsync(archive);
        await WaitUntilAsync(() => !archiveFolder.IsScanning, token);
        Require(model.Assets.Count == 109 && model.WorkspaceFolders.Count == 2 && model.Tabs.Count == 2, $"Adding a folder must keep the first one open ({model.Assets.Count} files, {model.WorkspaceFolders.Count} folders, {model.Tabs.Count} tabs).");
        Require(view.Count == 10 && model.SelectedTab?.Path == "Archive", "The new tab should show the added folder.");
        model.SelectedAsset = view.Cast<AssetViewModel>().First();
        var working = model.SelectedAsset;
        model.SelectedTab = model.Tabs[0];
        Require(view.Count == 70 && !model.IsScanning && ReferenceEquals(model.SelectedAsset, working), "Going back to the first tab must be instant and keep the file that is open.");
        Require(model.Tabs[0].Title == "shoot A" && model.Tabs[1].Title == "Archive", $"Tabs should be named after their folders: {model.Tabs[0].Title}, {model.Tabs[1].Title}.");

        // Hiding a folder takes it out of view without touching anything on disk.
        model.RemoveFolderCommand.Execute(model.FolderRows.Single(row => row.Name == "day 1"));
        Require(model.ShowFolderEdits && view.Count == 30, $"Hiding a folder should take its files out of the view, leaving 30, not {view.Count} ({model.Status}; {model.FolderEditsLabel}; filter {model.SelectedTab?.Path}; keys {string.Join("|", model.Assets.Where(item => item.FolderKey.Contains("day 1")).Select(item => item.FolderKey).Distinct())}; rows {string.Join("|", model.FolderRows.Select(row => row.Node.Path))}).");
        model.ShowAllFoldersCommand.Execute(null);
        Require(!model.ShowFolderEdits && view.Count == 70, "Show all folders should bring hidden folders back.");

        // Reopening a folder is instant: the saved index is shown before the folder is read again.
        model.RemoveWorkspaceFolderCommand.Execute(model.FolderRows.Single(row => row.Node.Path == "Archive"));
        Require(model.WorkspaceFolders.Count == 1 && model.Assets.Count == 99, "Removing a folder from the workspace takes only its files out.");
        archiveFolder = await model.AddFolderAsync(archive, show: false);
        Require(model.Assets.Count(item => item.Owner == archiveFolder) == 10, "A folder opened before should list its files straight from the saved index.");
        await WaitUntilAsync(() => !archiveFolder.IsScanning, token);

        // A file added on disk appears without a rescan.
        File.WriteAllBytes(Path.Combine(archive, "old", "new arrival.png"), [1, 2, 3]);
        using (var watch = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            watch.CancelAfter(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => model.Assets.Count(item => item.Owner == archiveFolder) == 11, watch.Token);
        }
        var saved = new SettingsStore(Path.Combine(dataDirectory, "settings.json")).Load();
        Require(saved.WorkspaceRoots.Length == 2 && saved.WorkspaceTabs.Length == 2, "The workspace folders and tabs should be saved for next time.");

        model.SelectedTab = model.Tabs[0];
        model.MainTab = 0;
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
        Require(window.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(10, 15, 34), "The main window lost its dark canvas background.");
        Require(window.Foreground is SolidColorBrush foreground && foreground.Color == Color.FromRgb(228, 241, 255), "The main window lost its readable foreground.");
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
