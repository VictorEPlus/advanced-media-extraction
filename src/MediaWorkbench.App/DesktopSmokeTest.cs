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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var tools = ToolPaths.Resolve();
        await tools.CheckAsync(timeout.Token);
        var mediaDirectory = Path.Combine(dataDirectory, "synthetic-media");
        Directory.CreateDirectory(mediaDirectory);
        var video = Path.Combine(mediaDirectory, "Sample video.mkv");
        await new ProcessRunner().RunAsync(tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=6:duration=1", "-c:v", "libx264", video], cancellationToken: timeout.Token);
        var photo = Path.Combine(mediaDirectory, "Sample photo.png");
        await new ProcessRunner().RunAsync(tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-i", video, "-frames:v", "1", photo], cancellationToken: timeout.Token);
        viewModel.ExportDirectory = Path.Combine(dataDirectory, "exports");
        await viewModel.SaveSettingsCommand.ExecuteAsync(null);
        await viewModel.OpenLibraryAsync(mediaDirectory);
        Require(viewModel.Assets.Count == 2, "The desktop scan did not discover both generated files.");
        var videoItem = viewModel.Assets.Single(item => item.Asset.Kind == MediaKind.Video);
        var photoItem = viewModel.Assets.Single(item => item.Asset.Kind == MediaKind.Photo);
        viewModel.SelectedAsset = videoItem;
        await WaitUntilAsync(() => !viewModel.IsPreviewBusy, timeout.Token);
        Require(viewModel.HasFrames && viewModel.MaximumFrame == 5, "Video frame indexing failed: " + viewModel.Status);
        viewModel.CurrentFrame = 3;
        await WaitUntilAsync(() => !viewModel.IsPreviewBusy, timeout.Token);
        Require(viewModel.CanExportFrame && viewModel.DisplayedFrame == 3 && !viewModel.IsPreviewStale, "Exact frame preview failed: " + viewModel.Status);
        Require(viewModel.PreviewImage is not null, "The previous frame must stay visible while the next one decodes.");
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
        await CheckWorkspaceAsync(viewModel, window, dataDirectory, mediaDirectory, photoItem, videoItem, timeout.Token);
        CheckPathPickers(dataDirectory, mediaDirectory);
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
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
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
        Require(window.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(16, 19, 27), "The main window lost its dark canvas background.");
        Require(window.Foreground is SolidColorBrush foreground && foreground.Color == Color.FromRgb(237, 241, 247), "The main window lost its readable foreground.");
    }

    private static void CheckPathPickers(string dataDirectory, string mediaDirectory)
    {
        var directoryPicker = new PathPickerWindow(PathPickerMode.Folder, "Choose a media folder", mediaDirectory);
        CheckDarkTheme(directoryPicker);
        Require(directoryPicker.ResolveSelection() == mediaDirectory, "The folder picker did not select its current directory.");
        directoryPicker.Navigate(dataDirectory);
        directoryPicker.Entries.SelectedItem = directoryPicker.Entries.Items.Cast<object>()
            .Single(entry => (string?)entry.GetType().GetProperty("FullPath")?.GetValue(entry) == mediaDirectory);
        Require(directoryPicker.ResolveSelection() == mediaDirectory, "The folder picker ignored the selected child folder.");
        directoryPicker.Address.Text = "not the current folder";
        ExpectPickerError(directoryPicker, "An uncommitted typed address must not select the old folder.");
        directoryPicker.Navigate(mediaDirectory);
        Render(directoryPicker, Path.Combine(dataDirectory, "folder-picker.png"));

        var jsonPath = Path.Combine(mediaDirectory, "favorites.json");
        File.WriteAllText(jsonPath, "{}");
        var openPicker = new PathPickerWindow(PathPickerMode.OpenFavorites, "Import favorites", mediaDirectory);
        CheckDarkTheme(openPicker);
        Require(openPicker.Entries.Items.Count == 1, "The favorites picker should filter out non-JSON media files.");
        ExpectPickerError(openPicker, "Opening favorites without a selection must fail.");
        openPicker.Entries.SelectedIndex = 0;
        Require(openPicker.ResolveSelection() == jsonPath, "The favorites picker did not select its JSON file.");

        var savePicker = new PathPickerWindow(PathPickerMode.SaveFavorites, "Export favorites", mediaDirectory);
        CheckDarkTheme(savePicker);
        ExpectPickerError(savePicker, "Existing favorites must not be overwritten without confirmation.");
        savePicker.Overwrite.IsChecked = true;
        Require(savePicker.ResolveSelection() == jsonPath, "Explicit file replacement should be allowed.");
        savePicker.FileName.Text = "new-favorites";
        Require(savePicker.Overwrite.IsChecked == false, "Changing a filename must clear replacement confirmation.");
        Require(savePicker.ResolveSelection() == Path.Combine(mediaDirectory, "new-favorites.json"), "Saving must add a JSON extension.");
        savePicker.FileName.Text = "../outside.json";
        ExpectPickerError(savePicker, "The filename field must not allow folder traversal.");
        savePicker.FileName.Text = "new-favorites.json";
        Render(savePicker, Path.Combine(dataDirectory, "favorites-picker.png"));
        var errorWindow = new StartupErrorWindow("Synthetic startup error for theme verification.");
        CheckDarkTheme(errorWindow);
        Render(errorWindow, Path.Combine(dataDirectory, "startup-dialog.png"));
    }

    private static void ExpectPickerError(PathPickerWindow picker, string message)
    {
        try { picker.ResolveSelection(); }
        catch (IOException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
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
