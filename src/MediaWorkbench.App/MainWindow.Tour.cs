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
        new("HeaderBar", "The top bar", "Search and filters in the middle; at the ends, the switches for the two side panels, and on the right rescan, the output folder, settings and this tour."),
        new("SourcesButton", "Workspace panel", "Shows or hides the workspace panel on the left, with your folders, collections and tags."),
        new("SearchBox", "Search", "Type part of a file or folder name. The filmstrip narrows as you type, in whichever folder it is showing."),
        new("ClearFiltersButton", "Clear filters", "Appears inside the search box while a search or filter is active, and clears them all. Its place is kept free otherwise, so nothing moves."),
        new("MediaFilterBox", "Media type", "All media, or only photos, only videos, or only sound."),
        new("SortBox", "Sort order", "Order the filmstrip by name, date, size, type or path. Natural name order puts frame2 before frame10."),
        new("FavoritesOnlyBox", "Favorites only", "Lit magenta when on: only files you starred are shown."),
        new("RescanButton", "Rescan", "Checks every workspace folder for files added, changed or removed. Folders are also watched while the app runs, so this is rarely needed."),
        new("OpenExportsButton", "Output folder", "The folder every export goes to, by name. Click for a menu: open the folder, or change it."),
        new("ChangeOutputButton", "Change the output folder", "In the output menu. Pick another folder; it is used straight away, with nothing else to save."),
        new("SettingsButton", "Settings", "Opens the Advanced tab of the inspector: FFmpeg location, cache size, collection files, favorites transfer and the keyboard shortcuts."),
        new("TourButton", "Tour the UI", "Starts this tour again at any time."),
        new("InspectorButton", "Inspector panel", "Shows or hides the panel on the right with Details, Tags, Export and Advanced."),

        new("SourcesPanel", "The workspace", "Every folder you are working with, open side by side. Adding a folder never closes another, and each keeps its own tree.", Sources),
        new("OpenFolderButton", "Add a folder", "Adds a folder to the workspace. A folder opened before appears at once from its saved index and is then checked for changes in the background. You can also drop a folder onto the window.", Sources),
        new("FolderTreePanel", "Folder tree", "All folders at the top, then each workspace folder in cyan with its subfolders. Click any folder to show it in the filmstrip; nothing is rescanned. Right-click for Open in a new tab, Add as a workspace folder of its own, Show in Explorer, or Hide. Double-click or Left and Right open and close a folder.", Sources),
        new("ExpandFoldersButton", "Open and close every folder", "The two small arrows open every level of the tree, or fold it back to the workspace folders.", Sources),
        new("CollectionsSection", "Collections", "Lists of files from anywhere on your PC; nothing is copied or moved. Click one to show it in the workspace beside your folders.", window => { Sources(window); window.CollectionsSection.IsExpanded = true; }),
        new("StageToBox", "Stage to", "The collection that Stage (S) adds files to.", window => { Sources(window); window.CollectionsSection.IsExpanded = true; }),
        new("ManageCollectionsExpander", "New collection", "Type a name and press New.", window => { Sources(window); window.CollectionsSection.IsExpanded = true; }),
        new("TaggedFilesSection", "Tags", "Narrow the filmstrip to a tag, pick from tags you have used, or show every tagged file as one more entry in the workspace.", window => { Sources(window); window.TaggedFilesSection.IsExpanded = true; }),

        new("MainTabs", "Overview, Preview and Stitch", "Overview charts the folder in the filmstrip. Preview shows the selected file with its tools. Stitch combines a few pictures into one. Selecting a file opens Preview.", Library),
        new("LibrarySummaryBlock", "Totals", "How many files are in the folder shown in the filmstrip, in how many subfolders, their size and the split between photos, videos and sound.", Library),
        new("FolderChartPanel", "Folder graph", "A bar for each subfolder: length is the number of files, colours split photos, videos and sound. Hover for exact numbers; click a bar to go into that folder.", Library),

        new("SelectionHeader", "Selected file", "The open file's name, its shape and a one-line summary. It stays open while you browse other folders; Not in view says the filmstrip is showing somewhere else.", Preview),
        new("AspectChip", "Shape", "The picture's shape, such as 16:9. A tilde means it is only close to that everyday shape.", Preview),
        new("FavoriteButton", "Favorite", "Stars the file, or takes the star off. Shortcut: F.", Preview),
        new("StageButton", "Stage", "Adds the file to the collection chosen under Stage to. Shortcut: S.", Preview),
        new("FocusViewButton", "Focus view", "Only the picture, the timeline and the buttons. F11 or Esc brings everything back.", Preview),
        new("PreviewMonitor", "Preview", "The photo, the exact video frame, or the video playing; pausing keeps the frame that was showing. Wheel over the picture zooms at the pointer, drag moves, double-click fits; Ctrl with the wheel steps frames.", Preview),
        new("PreviewDock", "Under the picture", "Frame counter, timeline, sound and buttons. This strip is the same height for every kind of file, so the picture never jumps when you pick another file.", Preview),
        new("FrameReadout", "Frame counter", "The frame number (from zero), the last frame and the time. While a video is first indexed, dots fill with a percentage. On the right, your in and out points.", Preview),
        new("FrameRateReadout", "Frames per second", "Measured from the real frame times once indexing finishes; a tilde and variable mean the time between frames changes.", Preview),
        new("Timeline", "Timeline", "Drag the playhead to any frame, drag the handles to set in and out, or roll the wheel to step frames. Left and Right step one frame, Shift ten.", Preview),
        new("VideoWaveform", "Sound", "The video's sound, lined up with the timeline. Drag to mark a section; it snaps to whole frames. Click to go to that moment.", Preview),
        new("MarkInButton", "In", "Starts the selection at the current frame. Shortcut: I.", Preview),
        new("MarkOutButton", "Out", "Ends the selection at the current frame. Shortcut: O.", Preview),
        new("RestartButton", "Restart", "Back to the first frame and play from there. Shortcut: Home.", Preview),
        new("PreviousFrameButton", "Previous frame", "One frame back. Shortcuts: comma, or Left while the preview has focus.", Preview),
        new("PlaybackButton", "Play and pause", "Plays from exactly the frame you are looking at; pausing keeps the frame that was showing. Shortcut: Space.", Preview),
        new("NextFrameButton", "Next frame", "One frame forward. Shortcuts: period, or Right while the preview has focus.", Preview),
        new("PlayRangeButton", "Play the marked range", "Plays from the in point and stops on the out point, to check a selection before exporting it.", Preview),
        new("CopyButton", "Copy", "Copies the full-resolution picture, or just the crop area, to the clipboard. Shortcut: Ctrl+C.", Preview),
        new("CropButton", "Crop", "Drag on the picture to choose an area for Copy; its size shows at the top of the picture. Esc clears it. Exports are never cropped.", Preview),
        new("ExportFrameButton", "Export", "Saves the frame or photo on screen as a full-resolution PNG in the output folder. Shortcut: E.", Preview),
        new("MoreActionsButton", "More", "Add to stitch, Crop and rotate the whole video, Snip marked sound to WAV, and Clear the crop area.", Preview),
        new("FramingButton", "Crop and rotate the whole video", "In the More menu. Tools appear over the top of the picture and a grabber goes on each edge; export a new MP4 when done.", Preview),
        new("SnipAudioButton", "Snip marked sound", "In the More menu. Saves just the marked sound as a WAV in the output folder.", Preview),
        new("FramingTools", "Crop and rotate tools", "Auto-fit finds black bars. Drag a grabber or use the arrow keys on a chosen edge (Shift for 10 pixels, Tab for the next edge). Turn left and right in quarter turns. The original is never changed.", Preview),

        new("InspectorPanel", "The inspector", "Details, Tags, Export and Advanced, for the selected file and the app.", Inspector(0)),
        new("DetailsTabContent", "Details", "A one-line summary, then everything known about the file: dimensions, size, dates, camera data, codec and frame rate.", Inspector(0)),
        new("RevealFileButton", "Reveal", "Shows the file in Windows Explorer.", Inspector(0)),
        new("TagBox", "Tags", "Type tags separated by commas and press Add. Tags are kept by the app, never written into the file. They follow the file when it is renamed or moved, and exact copies of it get them too.", Inspector(1)),
        new("SuggestedTagsSection", "Suggested tags", "Tags of files that resemble this one: same camera on the same day, the same numbered sequence, the same folder, the same technical profile, or a picture that looks the same. Each says why; click one to add it. Shown when there is something to suggest.", Inspector(1)),
        new("FolderTagsSection", "Folder tags", "Tags for the folder selected in the workspace tree (or right-click a folder and choose Tag this folder). Every file in it and its subfolders carries them, including files added later; they show under From folders on each file.", Inspector(1)),
        new("MetadataTagModeButton", "Tag from details", "Tick values in Details, such as the camera, then confirm to turn them into tags.", Inspector(1)),
        new("FindRelatedButton", "Shared tags", "Every other file that shares a tag with this one, shown as one more entry in the workspace.", Inspector(1)),
        new("DestinationSection", "Output folder", "Where exports go. Change picks another folder at once.", Inspector(2)),
        new("TrimButton", "Trim to MP4", "Saves the frames between in and out as a new MP4, accurate to the frame.", Inspector(2)),
        new("FrameExportButtons", "Frames as PNGs", "One PNG per frame, for the selection or the whole video. Above 500 files you click twice to confirm.", Inspector(2)),
        new("AudioExportPanel", "Sound export", "Pick a track and a channel, then export the marked sound as a WAV.", Inspector(2)),
        new("ExportQueueSection", "Queue", "Exports run one at a time while you keep working, each with progress, Cancel and Open output.", Inspector(2)),
        new("JobHistoryExpander", "Previous exports", "Finished exports, kept between sessions, each with Open.", window => { Inspector(2)(window); window.JobHistoryExpander.IsExpanded = true; }),
        new("FfmpegSetting", "FFmpeg folder", "Leave blank if FFmpeg is installed normally; otherwise the folder with ffmpeg.exe and ffprobe.exe.", Inspector(3)),
        new("CacheSetting", "Cache limit", "Disk space for decoded frames and thumbnails. More makes revisiting long videos faster.", Inspector(3)),
        new("FavoritesTransferExpander", "Favorites transfer", "Export the favorites of the folder shown in the filmstrip, or import them on another PC.", Inspector(3)),
        new("KeyboardHelp", "Keyboard", "Every shortcut in one place.", Inspector(3)),

        new("FilmstripPanel", "The filmstrip", "Every file in the folder you are looking at that passes the filters. Click one to open it; Page Up and Page Down move one file at a time."),
        new("FolderTabsList", "Folder tabs", "Keep several folders a click away. Clicking a folder in the tree moves the selected tab there; right-click a folder for a new tab. The file you are working on stays open when you switch tabs."),
        new("NewTabButton", "New tab", "Opens another tab on the same folder, ready to be pointed somewhere else."),
        new("VisibleCountText", "How many files", "Files shown, out of every file in the workspace, and which folder they are in."),
        new("ThumbnailSizeControl", "Thumbnail size", "Makes the thumbnails bigger or smaller. The only control that resizes the window's parts, and only when you move it."),
        new("FollowScrollBox", "Follow scroll", "While on, the preview shows whatever is under the marker as you scroll the filmstrip, and opens it when you stop."),
        new("StatusText", "Status line", "What the app just did. READING FOLDERS shows on the right while a folder is being read."),
    ];

    internal void StartTour()
    {
        viewModel.IsFocusView = false;
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
