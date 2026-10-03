using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly Dictionary<Control, (AssetViewModel Item, CancellationTokenSource Cancellation)> thumbnailRequests = new();
    private GridLength inspectorWidth = new(330);
    private GridLength navigatorWidth = new(270);
    /// <summary>Thumbnails stay with their files until this many others have been shown since, so switching folders or tabs shows them at once.</summary>
    internal const int ThumbnailsKept = 400;
    private readonly LinkedList<AssetViewModel> thumbnailHolders = new();
    private readonly Dictionary<AssetViewModel, LinkedListNode<AssetViewModel>> holderNodes = new();

    /// <summary>For the designer and the XAML loader only.</summary>
    public MainWindow() : this(null!) { }

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        if (viewModel is null) return;
        DataContext = viewModel;
        NativeDialogs.Owner = this;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.TourRequested += OnTourRequested;
        viewModel.FolderTagRequested += OnFolderTagRequested;
        viewModel.CollectionNameRequested += OnCollectionNameRequested;
        ApplyInspectorVisibility();
        ApplySourcesVisibility();
        SyncFilmstripSelection();
        // Only where it was last time if at least its title bar is still on one of the screens.
        if (viewModel.SavedWindow() is var (bounds, maximized) && Screens.All.Any(screen => screen.Bounds.Intersects(new PixelRect((int)bounds.X + 40, (int)bounds.Y, Math.Max(1, (int)bounds.Width - 80), 30))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)bounds.X, (int)bounds.Y);
            Width = bounds.Width;
            Height = bounds.Height;
            if (maximized) WindowState = WindowState.Maximized;
        }
        Closing += (_, _) => viewModel.RememberWindow(new Rect(Position.X, Position.Y, Width, Height), WindowState == WindowState.Maximized);
        Closed += OnClosed;

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        SizeChanged += (_, _) => { if (IsTourActive) ShowTourStep(tourIndex); };

        Filmstrip.SelectionChanged += FilmstripSelectionChanged;
        Filmstrip.ContainerPrepared += (_, args) => RequestThumbnail(args.Container);
        Filmstrip.ContainerClearing += (_, args) => ReleaseThumbnail(args.Container);
        Filmstrip.AddHandler(PointerWheelChangedEvent, FilmstripMouseWheel, RoutingStrategies.Tunnel);
        Filmstrip.AddHandler(PointerPressedEvent, (_, _) => StopGlide(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Filmstrip.SizeChanged += (_, _) => UpdateFollowFocus(false);
        Filmstrip.ContextRequested += FilmstripContextRequested;
        Filmstrip.AddHandler(PointerPressedEvent, FilmstripPickPointerPressed, RoutingStrategies.Tunnel);
        SetUpTagCompletion(TagBox, viewModel.AddTagsCommand);
        SetUpTagCompletion(FolderTagBox, viewModel.AddFolderTagsCommand);
        SetUpTagCompletion(QuickTagBox, viewModel.AddQuickTagsCommand, backToFilmstrip: true);
        SetUpHoverPreview();
        // The tag filter's list is counted afresh each time it opens (files may have been added since), with an empty find box.
        ((Flyout)TagFilterButton.Flyout!).Opening += (_, _) =>
        {
            viewModel.TagFilterSearch = "";
            viewModel.RebuildTagFilterOptions();
        };

        FolderTreeList.ContainerPrepared += (_, args) => { if (args.Container.DataContext is FolderRowViewModel row) _ = viewModel.LoadFolderCoversAsync(row); };
        FolderTreeList.AddHandler(PointerPressedEvent, FolderTreePointerPressed, RoutingStrategies.Tunnel);
        FolderTreeList.AddHandler(KeyDownEvent, FolderTreeKeyDown, RoutingStrategies.Tunnel);
        FolderTreeList.DoubleTapped += FolderTreeDoubleClick;
        FolderTreeList.ContextRequested += FolderTreeContextRequested;
        FolderTreeList.AddHandler(KeyDownEvent, RenameBoxKeyDown, RoutingStrategies.Tunnel);
        FolderTreeList.AddHandler(LostFocusEvent, RenameBoxLostFocus, RoutingStrategies.Bubble);

        FolderTabsList.AddHandler(PointerReleasedEvent, FolderTabMouseUp, RoutingStrategies.Tunnel);
        OverviewScroller.ScrollChanged += OverviewScrollChanged;
        PreviewMonitor.AddHandler(PointerWheelChangedEvent, FrameWheel);
        TimelineArea.AddHandler(PointerWheelChangedEvent, FrameWheel, RoutingStrategies.Tunnel);
        AudioWaveform.SelectionRequested += OnAudioSelectionRequested;
        AudioWaveform.SeekRequested += OnAudioSeekRequested;
        VideoWaveform.SelectionRequested += OnAudioSelectionRequested;
        VideoWaveform.SeekRequested += OnAudioSeekRequested;
        TourNextButton.Click += (_, _) => AdvanceTour(1);
        TourBackButton.Click += (_, _) => AdvanceTour(-1);
        TourEndButton.Click += (_, _) => EndTour();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(MainViewModel.SelectedAsset): SyncFilmstripSelection(); break;
            case nameof(MainViewModel.ShowInspector): ApplyInspectorVisibility(); break;
            case nameof(MainViewModel.ShowSources): ApplySourcesVisibility(); break;
            case nameof(MainViewModel.FollowFilmstrip): OnFollowFilmstripChanged(); break;
            case nameof(MainViewModel.IsFocusView): ApplyFocusView(); break;
        }
    }

    /// <summary>
    /// The filmstrip highlight follows the view model, never the other way round for clears: a collection reset or a
    /// filter that hides the selected item must not tear down the preview, so a null from the list is ignored.
    /// </summary>
    private void SyncFilmstripSelection()
    {
        var selected = viewModel.SelectedAsset;
        var target = selected is not null && viewModel.IsVisible(selected) ? selected : null;
        if (!ReferenceEquals(Filmstrip.SelectedItem, target))
            Filmstrip.SelectedItem = target;
    }

    private void FilmstripSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (Filmstrip.SelectedItem is not AssetViewModel item)
            return;
        if (!ReferenceEquals(viewModel.SelectedAsset, item))
            viewModel.SelectedAsset = item;
        RevealSelection(item);
    }

    private void ApplyInspectorVisibility()
    {
        if (viewModel.ShowInspector)
        {
            InspectorPanel.IsVisible = true;
            InspectorSplitter.IsVisible = true;
            MainGrid.ColumnDefinitions[3].Width = new GridLength(8);
            MainGrid.ColumnDefinitions[4].MinWidth = 300;
            MainGrid.ColumnDefinitions[4].MaxWidth = 460;
            MainGrid.ColumnDefinitions[4].Width = inspectorWidth;
        }
        else
        {
            if (InspectorPanel.Bounds.Width > 0)
                inspectorWidth = new GridLength(InspectorPanel.Bounds.Width);
            InspectorPanel.IsVisible = false;
            InspectorSplitter.IsVisible = false;
            MainGrid.ColumnDefinitions[3].Width = new GridLength(0);
            MainGrid.ColumnDefinitions[4].MinWidth = 0;
            MainGrid.ColumnDefinitions[4].MaxWidth = double.PositiveInfinity;
            MainGrid.ColumnDefinitions[4].Width = new GridLength(0);
        }
    }

    /// <summary>The workspace panel on the left: shown at its last width, or folded away completely.</summary>
    private void ApplySourcesVisibility()
    {
        if (viewModel.ShowSources)
        {
            SourcesPanel.IsVisible = true;
            NavigatorSplitter.IsVisible = true;
            MainGrid.ColumnDefinitions[1].Width = new GridLength(8);
            MainGrid.ColumnDefinitions[0].MinWidth = 220;
            MainGrid.ColumnDefinitions[0].MaxWidth = 420;
            MainGrid.ColumnDefinitions[0].Width = navigatorWidth;
        }
        else
        {
            if (SourcesPanel.Bounds.Width > 0)
                navigatorWidth = new GridLength(SourcesPanel.Bounds.Width);
            SourcesPanel.IsVisible = false;
            NavigatorSplitter.IsVisible = false;
            MainGrid.ColumnDefinitions[1].Width = new GridLength(0);
            MainGrid.ColumnDefinitions[0].MinWidth = 0;
            MainGrid.ColumnDefinitions[0].MaxWidth = double.PositiveInfinity;
            MainGrid.ColumnDefinitions[0].Width = new GridLength(0);
        }
    }

    /// <summary>A menu built when asked for, under the pointer. Menus inside list rows cannot reach the window's commands through bindings.</summary>
    private static void ShowMenu(Control target, IEnumerable<object> items)
    {
        var menu = new ContextMenu { ItemsSource = items.ToList() };
        menu.Open(target);
    }

    private static MenuItem Item(string header, ICommand command, object? parameter, string? tip = null, string? gesture = null)
    {
        var item = new MenuItem { Header = header, Command = command, CommandParameter = parameter };
        if (tip is not null) ToolTip.SetTip(item, tip);
        if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
        return item;
    }

    private void FolderTreeContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if ((args.Source as Control)?.DataContext is not FolderRowViewModel row || args.Source is not Control target) return;
        args.Handled = true;
        var items = new List<object>();
        if (row.IsWorkspaceFolder)
            items.Add(Item("Rename…", viewModel.StartRenameCommand, row, "Give this workspace folder the name you want to see. The folder on disk keeps its name.", "F2"));
        items.Add(Item("Open in a new tab", viewModel.OpenInNewTabCommand, row));
        items.Add(Item("Move out to its own workspace folder", viewModel.AddSubfolderToWorkspaceCommand, row, "Shows this folder as its own entry in the workspace and no longer inside this one. Nothing on disk moves; removing it from the workspace puts it back."));
        items.Add(Item("Show in Explorer", viewModel.RevealFolderCommand, row));
        items.Add(Item("Tag this folder…", viewModel.TagFolderCommand, row, "Tags that every file in this folder and its subfolders carries, including files added later"));
        items.Add(new Separator());
        items.Add(Item("Hide this folder (or remove a workspace folder)", viewModel.RemoveFolderCommand, row, "Hides the folder here without touching anything on disk. A workspace folder is taken out of the workspace; one that was moved out goes back into its parent."));
        items.Add(Item("Show all folders", viewModel.ShowAllFoldersCommand, null));
        ShowMenu(target, items);
    }

    private void FilmstripContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if ((args.Source as Control)?.DataContext is not AssetViewModel item || args.Source is not Control target) return;
        args.Handled = true;
        var items = new List<object>
        {
            Item("Open in Explorer", viewModel.RevealAssetCommand, item, "Opens the folder this file is in, with the file selected.", "Ctrl+Shift+E"),
            Item("Add to stitch", viewModel.AddToStitchCommand, item, "Adds this file's picture to the Stitch view."),
            new Separator(),
            Item(item.IsPicked ? "Unpick" : "Pick", viewModel.TogglePickCommand, item, "Pick several files to tag them together in the Tags tab.", "Ctrl+Click")
        };
        if (viewModel.HasPicks)
            items.Add(Item($"Tag the {viewModel.PickedCount:N0} picked…", viewModel.ShowTagsForPicksCommand, null, "Opens the Tags tab for the picked files."));
        ShowMenu(target, items);
    }

    /// <summary>The keyboard on the open file's thumbnail (or the filmstrip), where the arrow keys change file.</summary>
    private void FocusFilmstrip() => Dispatcher.UIThread.Post(() =>
    {
        if (Filmstrip.SelectedItem is { } selected && Filmstrip.ContainerFromItem(selected) is Control container)
            container.Focus(NavigationMethod.Tab);
        else
            Filmstrip.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Input);

    /// <summary>
    /// Ctrl+click picks or unpicks a thumbnail and Shift+click picks a run of them, as in Explorer, without changing the open file.
    /// A plain click still opens the file.
    /// </summary>
    private void FilmstripPickPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        var modifiers = args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift);
        if (modifiers == KeyModifiers.None || !args.GetCurrentPoint(Filmstrip).Properties.IsLeftButtonPressed
            || (args.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: AssetViewModel item } container)
            return;
        args.Handled = true;
        // The click is taken here, so the list does not move the keyboard to the thumbnail itself: Esc and Ctrl+A then work on the picks.
        container.Focus(NavigationMethod.Pointer);
        if (modifiers == KeyModifiers.Shift)
            viewModel.PickRange(item);
        else
            viewModel.TogglePick(item);
    }

    /// <summary>
    /// Tag boxes suggest the tags already in use while typing: only the tag after the last comma is matched, and choosing one
    /// keeps the tags before it. Enter adds the tags when no suggestion list is open.
    /// </summary>
    private void SetUpTagCompletion(AutoCompleteBox box, ICommand add, bool backToFilmstrip = false)
    {
        box.FilterMode = AutoCompleteFilterMode.Custom;
        box.TextFilter = (text, tag) => TagCompletion.Matches(text, tag);
        box.TextSelector = (text, tag) => TagCompletion.Complete(text, tag);
        box.AddHandler(KeyDownEvent, (_, args) =>
        {
            // With a suggestion highlighted the box takes Enter itself and fills it in; otherwise Enter adds what was typed.
            if (args.Key != Key.Enter) return;
            box.IsDropDownOpen = false;
            if (add.CanExecute(null)) add.Execute(null);
            args.Handled = true;
            // The bar's box: the keyboard goes back to the filmstrip, so the arrow keys move on to the next file to tag.
            if (backToFilmstrip) FocusFilmstrip();
        }, RoutingStrategies.Bubble);
    }

    /// <summary>A middle click on a folder tab closes it, as in a browser.</summary>
    private void FolderTabMouseUp(object? sender, PointerReleasedEventArgs args)
    {
        if (args.InitialPressMouseButton != MouseButton.Middle || args.Source is not Visual source)
            return;
        for (Visual? current = source; current is not null; current = current.GetVisualParent())
            if (current is ListBoxItem { DataContext: FolderTab tab })
            {
                viewModel.CloseTabCommand.Execute(tab);
                args.Handled = true;
                return;
            }
    }

    private async void RequestThumbnail(Control container)
    {
        if (container.DataContext is not AssetViewModel item || thumbnailRequests.ContainsKey(container)) return;
        var cancellation = new CancellationTokenSource();
        thumbnailRequests[container] = (item, cancellation);
        KeepThumbnail(item);
        if (item.Thumbnail is null)
            await viewModel.LoadThumbnailAsync(item, cancellation.Token);
    }

    private void ReleaseThumbnail(Control container)
    {
        if (!thumbnailRequests.Remove(container, out var request)) return;
        request.Cancellation.Cancel();
        request.Cancellation.Dispose();
        // The picture stays with the file: scrolling back, switching folders or tabs, or a filter change shows it at once instead
        // of a blank that fills in a moment later. Only the least recently shown are let go, so memory stays bounded.
    }

    private void KeepThumbnail(AssetViewModel item)
    {
        if (holderNodes.Remove(item, out var existing))
            thumbnailHolders.Remove(existing);
        holderNodes[item] = thumbnailHolders.AddFirst(item);
        var onScreen = thumbnailRequests.Values.Select(request => request.Item).ToHashSet();
        var guard = thumbnailHolders.Count;
        while (thumbnailHolders.Count > ThumbnailsKept && thumbnailHolders.Last is { } oldest && guard-- > 0)
        {
            thumbnailHolders.RemoveLast();
            if (onScreen.Contains(oldest.Value))
            {
                thumbnailHolders.AddFirst(oldest);
                continue;
            }
            holderNodes.Remove(oldest.Value);
            oldest.Value.Thumbnail = null;
        }
    }

    private void OnTourRequested(object? sender, EventArgs args) => StartTour();

    /// <summary>After "Tag this folder" in the tree: the Tags tab is open; put the cursor in the folder tag box once it is on screen.</summary>
    private void OnFolderTagRequested(object? sender, EventArgs args) =>
        Dispatcher.UIThread.Post(() => FolderTagBox.Focus(), DispatcherPriority.Input);

    private void OnCollectionNameRequested(object? sender, EventArgs args) =>
        Dispatcher.UIThread.Post(() => NewCollectionBox.Focus(), DispatcherPriority.Input);

    /// <summary>The rename box takes the cursor with its text selected as soon as it appears.</summary>
    private void FocusRenameBox(FolderRowViewModel row)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (FolderTreeList.ContainerFromItem(row) is not Control container) return;
            if (container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(box => box.Name == "RenameBox") is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    private void RenameBoxKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Source is not TextBox { Name: "RenameBox", DataContext: FolderRowViewModel row }) return;
        if (args.Key is Key.Enter or Key.Escape)
        {
            viewModel.FinishRename(row, keep: args.Key == Key.Enter);
            FolderTreeList.Focus();
            args.Handled = true;
        }
        else if (args.Key is Key.Left or Key.Right or Key.Space)
            args.Handled = false;
    }

    private void RenameBoxLostFocus(object? sender, RoutedEventArgs args)
    {
        if (args.Source is TextBox { Name: "RenameBox", DataContext: FolderRowViewModel row }) viewModel.FinishRename(row, keep: true);
    }

    /// <summary>Overview cards fetch their covers once they are scrolled into view, like the rows of the tree.</summary>
    private void OverviewScrollChanged(object? sender, ScrollChangedEventArgs args)
    {
        var viewport = new Rect(OverviewScroller.Viewport);
        for (var index = 0; index < OverviewFolderCards.ItemCount; index++)
        {
            if (OverviewFolderCards.ContainerFromIndex(index) is not Control card || !card.IsVisible
                || card.DataContext is not FolderRowViewModel { CoversRequested: false } row)
                continue;
            if (card.TranslatePoint(new Point(0, 0), OverviewScroller) is { } origin && new Rect(origin, card.Bounds.Size).Intersects(viewport))
                _ = viewModel.LoadFolderCoversAsync(row);
        }
    }

    /// <summary>The arrow of a row opens or closes it; clicking elsewhere on the row selects it.</summary>
    private void FolderTreePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is Control { Tag: "toggle" } toggle && toggle.DataContext is FolderRowViewModel row && args.GetCurrentPoint(toggle).Properties.IsLeftButtonPressed)
        {
            viewModel.ToggleFolderRowCommand.Execute(row);
            lastArrowClick = DateTime.UtcNow;
            args.Handled = true;
        }
    }

    /// <summary>When an arrow was last clicked: a quick second click on an arrow is two toggles, not also a double-click on the row.</summary>
    private DateTime lastArrowClick;

    private void FolderTreeDoubleClick(object? sender, TappedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        if (args.Source is Control { Tag: "toggle" } || DateTime.UtcNow - lastArrowClick < TimeSpan.FromMilliseconds(600)) return;
        if (FolderTreeList.SelectedItem is FolderRowViewModel row)
            viewModel.ToggleFolderRowCommand.Execute(row);
    }

    private void OnAudioSelectionRequested(object? sender, AudioRangeEventArgs args) => viewModel.SelectAudioRange(args.Start, args.End);

    private void OnAudioSeekRequested(object? sender, AudioSeekEventArgs args) => viewModel.SeekAudio(args.Time);

    private void FolderTreeKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Source is TextBox) return;
        if (args.Key == Key.F2 && FolderTreeList.SelectedItem is FolderRowViewModel { IsWorkspaceFolder: true } renamed)
        {
            viewModel.StartRenameCommand.Execute(renamed);
            FocusRenameBox(renamed);
            args.Handled = true;
            return;
        }
        if (FolderTreeList.SelectedItem is not FolderRowViewModel { HasChildren: true } row) return;
        if ((args.Key == Key.Right && !row.IsExpanded) || (args.Key == Key.Left && row.IsExpanded))
        {
            viewModel.ToggleFolderRowCommand.Execute(row);
            KeepTreeFocus();
            args.Handled = true;
        }
    }

    /// <summary>Opening or closing a folder rebuilds the rows; the keyboard stays on the selected one, so the next arrow works too.</summary>
    private void KeepTreeFocus() => Dispatcher.UIThread.Post(() =>
    {
        if (FolderTreeList.SelectedItem is { } selected && FolderTreeList.ContainerFromItem(selected) is Control container)
            container.Focus();
        else
            FolderTreeList.Focus();
    }, DispatcherPriority.Input);

    private object? Focused => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (IsTourActive)
        {
            switch (args.Key)
            {
                case Key.Escape: EndTour(); break;
                case Key.Right or Key.Enter or Key.Space: AdvanceTour(1); break;
                case Key.Left: AdvanceTour(-1); break;
            }
            args.Handled = true;
            return;
        }
        var focused = Focused;
        if (focused is TextBox or ComboBox)
            return;
        // A rename started from the menu: F2 equivalent focus happens when the box appears.
        if (viewModel.FolderRows.FirstOrDefault(row => row.IsRenaming) is { } renaming && focused is not TextBox)
            FocusRenameBox(renaming);
        var modifiers = args.KeyModifiers;
        if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && args.Key == Key.E)
        {
            viewModel.RevealFileCommand.Execute(null);
            args.Handled = true;
            return;
        }
        if (modifiers == KeyModifiers.None && args.Key == Key.T && viewModel.CanTagTargets && QuickTagBox.IsEffectivelyVisible)
        {
            QuickTagBox.Focus();
            args.Handled = true;
            return;
        }
        if (modifiers == KeyModifiers.Control && args.Key == Key.A && focused is Visual pickFocus
            && (ReferenceEquals(pickFocus, Filmstrip) || Filmstrip.IsVisualAncestorOf(pickFocus)))
        {
            viewModel.PickAllShownCommand.Execute(null);
            args.Handled = true;
            return;
        }
        if (modifiers == KeyModifiers.Control && args.Key == Key.C)
        {
            viewModel.CopyPreviewCommand.Execute(null);
            args.Handled = true;
            return;
        }
        if (HandleFramingKey(args, focused))
        {
            args.Handled = true;
            return;
        }
        if (modifiers != KeyModifiers.None) return;
        // Arrow keys mean "next file" in the filmstrip and "next frame" in the preview; controls with their own arrow handling keep it.
        if (args.Key is Key.Left or Key.Right && focused is Slider or TabItem or FrameTimeline)
            return;
        // The folder tree and graph use the arrow keys themselves.
        if (args.Key is Key.Left or Key.Right or Key.Space && focused is Visual libraryFocus
            && (FolderTreeList.IsVisualAncestorOf(libraryFocus) || ReferenceEquals(libraryFocus, FolderTreeList) || ReferenceEquals(libraryFocus, FolderChartView)))
            return;
        var filmstripFocused = focused is Visual visual && (ReferenceEquals(visual, Filmstrip) || Filmstrip.IsVisualAncestorOf(visual));
        var stepFrames = viewModel.IsVideo && !filmstripFocused;
        ICommand? command = args.Key switch
        {
            Key.F => viewModel.ToggleFavoriteCommand,
            Key.E => viewModel.ExportFrameCommand,
            Key.Left => stepFrames ? viewModel.PreviousFrameCommand : viewModel.PreviousAssetCommand,
            Key.Right => stepFrames ? viewModel.NextFrameCommand : viewModel.NextAssetCommand,
            Key.OemComma when viewModel.IsVideo => viewModel.PreviousFrameCommand,
            Key.OemPeriod when viewModel.IsVideo => viewModel.NextFrameCommand,
            Key.PageUp => viewModel.PreviousAssetCommand,
            Key.PageDown => viewModel.NextAssetCommand,
            Key.S => viewModel.StageSelectedCommand,
            Key.C => viewModel.ToggleCropCommand,
            Key.F11 => viewModel.ToggleFocusViewCommand,
            Key.Escape when viewModel.HasPicks && !viewModel.HasCrop && !viewModel.IsCropping => viewModel.ClearPicksCommand,
            Key.Escape when viewModel.IsFocusView && !viewModel.HasCrop && !viewModel.IsCropping => viewModel.ToggleFocusViewCommand,
            Key.Escape => viewModel.ResetCropCommand,
            Key.I when viewModel.IsVideo || viewModel.IsAudio => viewModel.MarkInCommand,
            Key.O when viewModel.IsVideo || viewModel.IsAudio => viewModel.MarkOutCommand,
            Key.Space => viewModel.TogglePlaybackCommand,
            Key.Home when viewModel.CanPlay => viewModel.RestartCommand,
            _ => null
        };
        if (command is null || !command.CanExecute(null))
            return;
        command.Execute(null);
        args.Handled = true;
    }

    /// <summary>
    /// While the video crop tools are open: Tab and Shift+Tab choose an edge, the arrow keys move the chosen edge by one source
    /// pixel (ten with Shift), and Esc lets go of the edge, then closes the tools. Without a chosen edge the arrows step frames as usual.
    /// </summary>
    private bool HandleFramingKey(KeyEventArgs args, object? focused)
    {
        if (!viewModel.ShowFramingTools || args.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
            return false;
        var inFilmstrip = focused is Visual visual && (ReferenceEquals(visual, Filmstrip) || Filmstrip.IsVisualAncestorOf(visual));
        var onPreview = ReferenceEquals(focused, PreviewSurface);
        var shift = args.KeyModifiers == KeyModifiers.Shift;
        switch (args.Key)
        {
            case Key.Tab when onPreview:
                PreviewSurface.CycleEdge(shift ? -1 : 1);
                return true;
            case Key.Escape when !shift:
                if (viewModel.SelectedCropEdge != CropEdge.None) viewModel.SelectedCropEdge = CropEdge.None;
                else viewModel.ToggleFramingCommand.Execute(null);
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when viewModel.SelectedCropEdge != CropEdge.None && !inFilmstrip && focused is not (Slider or TabItem or FrameTimeline):
                var horizontal = viewModel.SelectedCropEdge is CropEdge.Left or CropEdge.Right;
                var step = shift ? 10 : 1;
                if (horizontal && args.Key is Key.Left or Key.Right)
                    PreviewSurface.Nudge(args.Key == Key.Left ? -step : step);
                else if (!horizontal && args.Key is Key.Up or Key.Down)
                    PreviewSurface.Nudge(args.Key == Key.Up ? -step : step);
                // The other pair of arrows does nothing here, rather than stepping a frame by surprise.
                return true;
            default:
                return false;
        }
    }

    private void OnDrop(object? sender, DragEventArgs args)
    {
        if (args.DataTransfer.TryGetFiles() is not { Length: > 0 } items || items[0].TryGetLocalPath() is not { } path)
            return;
        args.Handled = true;
        _ = viewModel.OpenPathAsync(path);
    }

    private void OnDragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = args.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Link : DragDropEffects.None;
        args.Handled = true;
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.TourRequested -= OnTourRequested;
        viewModel.FolderTagRequested -= OnFolderTagRequested;
        viewModel.CollectionNameRequested -= OnCollectionNameRequested;
        StopGlide();
        settleTimer?.Stop();
        foreach (var container in thumbnailRequests.Keys.ToArray()) ReleaseThumbnail(container);
        viewModel.Dispose();
    }
}
