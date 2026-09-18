using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MediaWorkbench.App;

/// <summary>One stop on the tour: the named element to outline, what to call it, what it does, and how to bring it on screen.</summary>
internal sealed record TourStep(string Target, string Title, string Body, Action<MainWindow>? Prepare = null);

/// <summary>
/// "Tour the UI": dims the window, outlines one panel or button at a time and explains it in a callout.
/// Steps that point at controls which only exist for a selected video or photo still explain the control and say how to make it appear.
/// </summary>
public partial class MainWindow
{
    private int tourIndex = -1;
    private (bool Sources, bool Inspector, int InspectorTab, int MainTab)? tourRestore;

    internal bool IsTourActive => tourIndex >= 0;

    private static void Library(MainWindow window) => window.viewModel.MainTab = 0;
    private static void Preview(MainWindow window) => window.viewModel.MainTab = 1;
    private static void Sources(MainWindow window) => window.viewModel.ShowSources = true;
    private static Action<MainWindow> Inspector(int tab) => window => { window.viewModel.ShowInspector = true; window.viewModel.InspectorTab = tab; };

    internal static readonly IReadOnlyList<TourStep> TourSteps =
    [
        new("HeaderBar", "The top bar", "Everything that affects the whole window lives here: showing or hiding the side panels on the left, and opening folders, rescanning, exports and settings on the right."),
        new("SourcesButton", "Sources", "Shows or hides the left panel with your folders, collections and tag search. Hide it when you want more room for the picture."),
        new("InspectorButton", "Inspector", "Shows or hides the right panel with Details, Tags, Export and Settings."),
        new("TourButton", "Tour the UI", "Starts this tour again at any time."),
        new("OpenFolderButton", "Open folder", "Opens the Windows folder picker. Every photo, video and audio file in that folder and all of its subfolders is listed. You can also drop a folder or a single file onto the window."),
        new("RescanButton", "Rescan", "Reads the current folder or collection again to pick up files you added, renamed or removed since it was opened."),
        new("OpenExportsButton", "Open exports", "Opens the folder where every export is saved, in Windows Explorer. Hover it to see the path."),
        new("SettingsButton", "Settings", "Jumps to the Settings tab in the Inspector: export folder, FFmpeg location and cache size."),

        new("FilterBar", "The filter bar", "Narrows which files appear in the filmstrip at the bottom. Filters combine, and nothing is ever deleted or moved."),
        new("SearchBox", "Search", "Type part of a file name or subfolder name. The filmstrip updates as you type."),
        new("MediaFilterBox", "Media type", "Show all media, or only photos, only videos, or only audio."),
        new("SortBox", "Sort order", "Order the filmstrip by name, date, size, type or path. Natural name order puts frame2 before frame10."),
        new("FavoritesOnlyBox", "Favorites only", "Shows only files you have starred with the Favorite button or the F key."),
        new("VisibleCountText", "How many files match", "The number of files that pass the current filters, out of all files found. A Clear filters button appears next to it whenever a filter is active."),

        new("SourcesPanel", "The Sources panel", "Where your media comes from: the current folder, recent folders, collections you have built, and files you have tagged.", Sources),
        new("CurrentSourceSection", "Current source", "The folder, collection or tag search that is open right now. While a folder is being read, a Cancel scan button appears here.", Sources),
        new("FoldersSection", "Folders", "Open a folder, or click a recent folder to reopen it. The last eight folders are remembered.", Sources),
        new("CollectionsSection", "Collections", "A collection is a list of references to files from anywhere on your PC. Files are never copied or moved. Click a collection name to browse it like a folder.", Sources),
        new("StageToBox", "Stage to", "Chooses which collection the Stage buttons add files to.", Sources),
        new("StageCurrentButton", "Stage current file", "Adds the selected file to the chosen collection. The S key does the same.", Sources),
        new("StageFilteredButton", "Stage filtered files", "Adds every file that matches the current filters, not just the thumbnails you can see. The label shows how many that is.", Sources),
        new("ManageCollectionsExpander", "Manage collections", "Create a new collection by name, or load and save a collection as a JSON file to move it between PCs.", window => { Sources(window); window.ManageCollectionsExpander.IsExpanded = true; }),
        new("TaggedFilesSection", "Tagged files", "Filter the current source by a tag, pick from tags you have used, or browse every tagged file across all folders and drives.", Sources),
        new("FavoritesTransferExpander", "Favorites transfer", "Export your favorites for this folder to a file and import them on another PC where the same folder structure exists.", window => { Sources(window); window.FavoritesTransferExpander.IsExpanded = true; }),

        new("MainTabs", "Library and Preview tabs", "The centre of the window has two views, shown as folder tabs. The open tab is taller, has an amber top edge and joins the page below it. Library shows where your media lives. Preview shows the selected file. Selecting a file switches to Preview; with nothing selected you see Library. You can switch by hand at any time.", Library),
        new("LibrarySummaryBlock", "Library totals", "How many files were found, in how many folders, their total size, and the split between photos, videos and audio.", Library),
        new("FolderTreePanel", "Folder tree", "Every subfolder that contains media, with file counts that include its subfolders, the split between photos, videos and audio, and the share of its parent folder as a percentage. Click a folder to show only its files in the filmstrip. Double-click, or press Right and Left, to open and close it.", Library),
        new("ExpandFoldersButton", "Expand all and Collapse all", "Open every level of the tree at once, or fold it back to the top-level folders.", Library),
        new("FolderChartPanel", "Folder graph", "A bar for each subfolder of the folder you clicked in the tree. Length is the number of files and the percentage is that folder's share; colours split photos, videos and audio. Hover a bar for exact numbers and size. Click a bar to go into that folder.", Library),

        new("SelectionHeader", "Selected file", "On the same row as the tabs: the name of the selected file with its dimensions, frame rate and size. If a filter hides the file it stays open and a Hidden by filters note appears here.", Preview),
        new("FavoriteButton", "Favorite", "Stars or unstars the selected file. Favorites are remembered between sessions and marked with a star on the thumbnail. Shortcut: F.", Preview),
        new("StageButton", "Stage", "Adds the selected file to the collection chosen under Stage to. Shortcut: S.", Preview),
        new("PreviewMonitor", "Preview", "Shows the photo, the exact video frame, or video playback. Click it so the Left and Right keys step frames. With Crop on, drag here to choose pixels. Messages such as Saved or an error appear at the bottom right of this area.", Preview),
        new("FrameReadout", "Frame counter", "The current frame number (counting from zero), the last frame number, and the time of this frame in seconds. While a new video is still being indexed, a row of dots fills up here with a percentage; you can already play the video and see its first frame. On the right: your in and out points and how many frames they cover.", Preview),
        new("FrameRateReadout", "Frames per second", "How many frames this video shows each second, in large figures beside the frame counter. It is measured from the real frame times once indexing finishes. A ~ and the word variable mean the time between frames changes during the video. Shown when a video is selected.", Preview),
        new("Timeline", "Frame timeline", "Drag the white playhead to any frame. As you move the pointer along it, or drag the playhead or a handle, a small picture above the timeline shows what the video looks like there, so you can find a moment before you let go. Drag the amber handles to set the in and out points; the shaded part is your selection. The ruler underneath counts frames. Left and Right step one frame, Shift steps ten.", Preview),
        new("VideoWaveform", "Sound under the timeline", "A picture of the video's sound, lined up with the frame timeline above it: tall where it is loud, flat where it is quiet. Drag across it to mark a section; the section snaps to whole frames and moves the in and out markers. Click to jump to that moment. Shown when the selected video has sound. For an audio file the same picture fills the preview area.", Preview),
        new("MarkInButton", "Mark in", "Sets the start of your selection at the current frame. Shortcut: I.", Preview),
        new("MarkOutButton", "Mark out", "Sets the end of your selection at the current frame, inclusive. Shortcut: O.", Preview),
        new("AudioTools", "Snip selection to WAV", "The line shows which part of the sound is selected and how long it is. Snip selection to WAV saves just that part as a WAV file in your export folder; the original file is never changed. Track and channel are chosen in the Export tab. Shown when the selected file has sound.", Preview),
        new("PlaybackButton", "Play / pause", "Plays video or audio from the current position. Pausing returns to an exact still frame. Shortcut: Space.", Preview),
        new("PlayRangeButton", "Play marked range", "Plays from your in point and pauses at your out point, so you can check a selection before exporting it. For an audio file it is called Play selection and plays the section you dragged on the sound.", Preview),
        new("PreviousFrameButton", "Previous frame", "Steps back exactly one frame. Shortcuts: comma, or Left while the preview has focus.", Preview),
        new("NextFrameButton", "Next frame", "Steps forward exactly one frame. Shortcuts: period, or Right while the preview has focus.", Preview),
        new("CopyButton", "Copy", "Copies the full-resolution image, frame or crop to the clipboard. Nothing is saved to disk. Shortcut: Ctrl+C.", Preview),
        new("CropButton", "Crop", "Turns crop selection on or off. Drag on the preview to choose an area; Copy then copies only that area. Esc clears it. Exports are never cropped.", Preview),
        new("ExportFrameButton", "Export frame or PNG", "Saves the displayed frame or photo as a full-resolution PNG in your export folder, with no save dialog and no overwriting. Shortcut: E.", Preview),

        new("InspectorPanel", "The Inspector", "Four tabs about the selected file and the app: Details, Tags, Export and Settings.", Inspector(0)),
        new("DetailsTabContent", "Details tab", "A one-line summary, then everything known about the file: dimensions, size, dates, camera data for photos, codec and frame rate for videos.", Inspector(0)),
        new("RevealFileButton", "Reveal file", "Opens Windows Explorer with the selected file highlighted.", Inspector(0)),
        new("MetadataTagModeButton", "Create tags from metadata", "Shows a checkbox beside each detail. Tick the ones you want, then confirm, to turn them into tags such as aspect ratio 16:9. Nothing is tagged automatically.", Inspector(0)),
        new("TagsTabContent", "Tags tab", "Your own labels for the selected file. Tags are stored by the app and never written into your media.", Inspector(1)),
        new("TagBox", "Add tags", "Type one or more tags separated by commas, then press Add tags. Each tag gets a Remove button below.", Inspector(1)),
        new("FindRelatedButton", "Find files with shared tags", "Lists every other file, in any folder, that shares at least one tag with this one.", Inspector(1)),
        new("TagsMoreExpander", "More", "Exports all tags and the files they belong to as JSON, for use in other tools. The file contains full paths.", window => { Inspector(1)(window); window.TagsMoreExpander.IsExpanded = true; }),
        new("DestinationSection", "Export destination", "Every export is saved in this folder automatically. Change opens Settings.", Inspector(2)),
        new("VideoExportPanel", "Video selection", "The in and out frame numbers, how many frames that is and roughly how large an export would be.", Inspector(2)),
        new("TrimButton", "Trim selection to MP4", "Saves the frames between your in and out points as a new MP4, accurate to the frame. The original video is untouched.", Inspector(2)),
        new("FrameExportButtons", "Export frames as PNGs", "Saves one PNG per frame, either for your selection or for the whole video. The buttons show the count; above 500 files you click twice to confirm.", Inspector(2)),
        new("AudioExportPanel", "Audio selection", "Pick an audio track, all channels or one channel, and a start and end time, then export that as a WAV file. Works for videos with sound and for audio files.", Inspector(2)),
        new("ExportQueueSection", "Export queue", "Exports run one at a time while you keep working. Each shows progress, a Cancel button, and Open output when finished. The Export tab shows a count while jobs are running.", Inspector(2)),
        new("JobHistoryExpander", "Previous exports", "A record of finished exports that survives restarts, each with an Open button.", window => { Inspector(2)(window); window.JobHistoryExpander.IsExpanded = true; }),
        new("ExportDirectoryBox", "Export folder", "Where exports are saved. Type a full path or use Browse folder, then save.", Inspector(3)),
        new("FfmpegSetting", "FFmpeg folder", "Leave blank if FFmpeg is installed normally. Otherwise point this at the folder that contains ffmpeg.exe and ffprobe.exe.", Inspector(3)),
        new("CacheSetting", "Disk cache limit", "How much disk space decoded frames and thumbnails may use. A larger cache makes revisiting long videos faster.", Inspector(3)),
        new("SaveSettingsButton", "Check tools and save", "Confirms that FFmpeg works and saves these settings.", Inspector(3)),
        new("KeyboardHelp", "Keyboard shortcuts", "The full list of shortcuts is always here in Settings.", Inspector(3)),

        new("FilmstripPanel", "The filmstrip", "Every file that passes the filters, as thumbnails. Click one to select it, or use Page Up and Page Down to move one file at a time. The mouse wheel scrolls sideways. A star marks favorites."),
        new("FollowScrollBox", "Preview follows scroll", "Tick this and an amber marker appears on the filmstrip. As you scroll the filmstrip with the mouse wheel or its scrollbar, the preview shows whatever file is under the marker straight away, and opens it fully when you stop. Untick it to go back to clicking thumbnails."),
        new("ThumbnailSizeControl", "Thumbnail size", "At the right of the status line. Makes thumbnails larger or smaller; the filmstrip grows and shrinks with them."),
        new("StatusText", "Status line", "A one-line description of what the app just did or is doing. Important results and errors also appear as messages over the centre panel."),
    ];

    internal void StartTour()
    {
        if (viewModel.ShowPlayback && viewModel.TogglePlaybackCommand.CanExecute(null))
            viewModel.TogglePlaybackCommand.Execute(null);
        tourRestore ??= (viewModel.ShowSources, viewModel.ShowInspector, viewModel.InspectorTab, viewModel.MainTab);
        TourLayer.Visibility = Visibility.Visible;
        ShowTourStep(0);
    }

    internal void EndTour()
    {
        tourIndex = -1;
        TourLayer.Visibility = Visibility.Collapsed;
        if (tourRestore is { } restore)
        {
            viewModel.ShowSources = restore.Sources;
            viewModel.ShowInspector = restore.Inspector;
            viewModel.InspectorTab = restore.InspectorTab;
            viewModel.MainTab = restore.MainTab;
            tourRestore = null;
        }
    }

    /// <summary>Shows a step. Returns false only if the step names an element that does not exist, which is a bug in the step list.</summary>
    internal bool ShowTourStep(int index)
    {
        index = Math.Clamp(index, 0, TourSteps.Count - 1);
        tourIndex = index;
        var step = TourSteps[index];
        step.Prepare?.Invoke(this);
        RootGrid.UpdateLayout();
        var target = FindName(step.Target) as FrameworkElement;
        if (target is not null && IsShown(target))
        {
            target.BringIntoView();
            RootGrid.UpdateLayout();
        }
        TourStepCount.Text = $"Step {index + 1} of {TourSteps.Count}";
        TourTitle.Text = step.Title;
        TourBody.Text = step.Body;
        TourBackButton.IsEnabled = index > 0;
        TourNextButton.Content = index == TourSteps.Count - 1 ? "Finish" : "Next";
        PositionTour(target);
        return target is not null;
    }

    private void PositionTour(FrameworkElement? target)
    {
        var size = new Size(RootGrid.ActualWidth, RootGrid.ActualHeight);
        var bounds = Rect.Empty;
        if (target is not null && IsShown(target))
        {
            try
            {
                bounds = target.TransformToAncestor(RootGrid).TransformBounds(new Rect(target.RenderSize));
                bounds.Inflate(4, 4);
                bounds.Intersect(new Rect(size));
            }
            catch (InvalidOperationException) { bounds = Rect.Empty; }
        }
        var visible = !bounds.IsEmpty && bounds.Width > 1 && bounds.Height > 1;
        TourNote.Text = visible ? "" : "This control is not on screen right now. It appears when a matching file is selected, for example a video for the timeline and frame buttons.";
        TourNote.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        TourDim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(size)), visible ? new RectangleGeometry(bounds, 4, 4) : Geometry.Empty);
        TourHighlight.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            Canvas.SetLeft(TourHighlight, bounds.Left);
            Canvas.SetTop(TourHighlight, bounds.Top);
            TourHighlight.Width = bounds.Width;
            TourHighlight.Height = bounds.Height;
        }
        TourCallout.Measure(new Size(TourCallout.Width, double.PositiveInfinity));
        var callout = new Size(TourCallout.Width, TourCallout.DesiredSize.Height);
        var position = new Point((size.Width - callout.Width) / 2, (size.Height - callout.Height) / 2);
        if (visible)
        {
            const double gap = 12;
            Point[] candidates =
            [
                new(bounds.Left, bounds.Bottom + gap),
                new(bounds.Left, bounds.Top - gap - callout.Height),
                new(bounds.Right + gap, bounds.Top),
                new(bounds.Left - gap - callout.Width, bounds.Top),
            ];
            foreach (var candidate in candidates)
            {
                var clamped = new Point(Math.Clamp(candidate.X, 8, Math.Max(8, size.Width - callout.Width - 8)), Math.Clamp(candidate.Y, 8, Math.Max(8, size.Height - callout.Height - 8)));
                if (new Rect(clamped, callout).IntersectsWith(bounds)) continue;
                position = clamped;
                break;
            }
        }
        Canvas.SetLeft(TourCallout, position.X);
        Canvas.SetTop(TourCallout, position.Y);
    }

    /// <summary>Visibility up the tree plus a real size. IsVisible is not used because it is false for a window that is rendered offscreen.</summary>
    private static bool IsShown(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private void TourNextClicked(object sender, RoutedEventArgs args) => AdvanceTour(1);
    private void TourBackClicked(object sender, RoutedEventArgs args) => AdvanceTour(-1);
    private void TourEndClicked(object sender, RoutedEventArgs args) => EndTour();

    private void AdvanceTour(int delta)
    {
        if (!IsTourActive) return;
        if (delta > 0 && tourIndex >= TourSteps.Count - 1) { EndTour(); return; }
        ShowTourStep(tourIndex + delta);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (IsTourActive) ShowTourStep(tourIndex);
    }
}
