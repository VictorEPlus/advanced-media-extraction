using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MediaWorkbench.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly Dictionary<Image, (AssetViewModel Item, CancellationTokenSource Cancellation)> thumbnailRequests = new();
    private GridLength inspectorWidth = new(324);

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.TourRequested += OnTourRequested;
        ApplyInspectorVisibility();
        SyncFilmstripSelection();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(MainViewModel.SelectedAsset): SyncFilmstripSelection(); break;
            case nameof(MainViewModel.ShowInspector): ApplyInspectorVisibility(); break;
        }
    }

    /// <summary>
    /// The filmstrip highlight follows the view model, never the other way round for clears: a collection reset or a
    /// filter that hides the selected item must not tear down the preview, so a null from the ListBox is ignored.
    /// </summary>
    private void SyncFilmstripSelection()
    {
        var selected = viewModel.SelectedAsset;
        var target = selected is not null && viewModel.IsVisible(selected) ? selected : null;
        if (!ReferenceEquals(Filmstrip.SelectedItem, target))
            Filmstrip.SelectedItem = target;
    }

    private void FilmstripSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Filmstrip.SelectedItem is not AssetViewModel item)
            return;
        if (!ReferenceEquals(viewModel.SelectedAsset, item))
            viewModel.SelectedAsset = item;
        Filmstrip.ScrollIntoView(item);
    }

    private void ApplyInspectorVisibility()
    {
        if (viewModel.ShowInspector)
        {
            InspectorPanel.Visibility = Visibility.Visible;
            InspectorSplitter.Visibility = Visibility.Visible;
            InspectorColumn.MinWidth = 304;
            InspectorColumn.MaxWidth = 460;
            InspectorColumn.Width = inspectorWidth;
        }
        else
        {
            if (InspectorColumn.ActualWidth > 0)
                inspectorWidth = new GridLength(InspectorColumn.ActualWidth);
            InspectorPanel.Visibility = Visibility.Collapsed;
            InspectorSplitter.Visibility = Visibility.Collapsed;
            InspectorColumn.MinWidth = 0;
            InspectorColumn.MaxWidth = double.PositiveInfinity;
            InspectorColumn.Width = new GridLength(0);
        }
    }

    private async void ThumbnailLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image) await RequestThumbnailAsync(image);
    }

    private async void ThumbnailContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Image image) return;
        ReleaseThumbnail(image);
        if (image.IsLoaded) await RequestThumbnailAsync(image);
    }

    private void ThumbnailUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image) ReleaseThumbnail(image);
    }

    private async Task RequestThumbnailAsync(Image image)
    {
        if (image.DataContext is not AssetViewModel item || thumbnailRequests.ContainsKey(image)) return;
        var cancellation = new CancellationTokenSource();
        thumbnailRequests[image] = (item, cancellation);
        await viewModel.LoadThumbnailAsync(item, cancellation.Token);
    }

    private void ReleaseThumbnail(Image image)
    {
        if (!thumbnailRequests.Remove(image, out var request)) return;
        request.Cancellation.Cancel();
        request.Cancellation.Dispose();
        request.Item.Thumbnail = null;
    }

    private void OnTourRequested(object? sender, EventArgs args) => StartTour();

    private void FolderToggleClicked(object sender, MouseButtonEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: FolderRowViewModel row })
        {
            viewModel.ToggleFolderRowCommand.Execute(row);
            args.Handled = true;
        }
    }

    private void FolderTreeDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (FolderTreeList.SelectedItem is FolderRowViewModel row)
            viewModel.ToggleFolderRowCommand.Execute(row);
    }

    private void FolderTreeKeyDown(object sender, KeyEventArgs args)
    {
        if (FolderTreeList.SelectedItem is not FolderRowViewModel { HasChildren: true } row) return;
        if ((args.Key == Key.Right && !row.IsExpanded) || (args.Key == Key.Left && row.IsExpanded))
        {
            viewModel.ToggleFolderRowCommand.Execute(row);
            args.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
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
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is TextBoxBase or PasswordBox or ComboBox)
            return;
        if (Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.C)
        {
            viewModel.CopyPreviewCommand.Execute(null);
            args.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        // Arrow keys mean "next file" in the filmstrip and "next frame" in the preview; controls with their own arrow handling keep it.
        if (args.Key is Key.Left or Key.Right && focused is Slider or TabItem or FrameTimeline)
            return;
        // The folder tree and graph use the arrow keys themselves.
        if (args.Key is Key.Left or Key.Right or Key.Space && focused is Visual libraryFocus && (FolderTreeList.IsAncestorOf(libraryFocus) || ReferenceEquals(libraryFocus, FolderTreeList) || ReferenceEquals(libraryFocus, FolderChartView)))
            return;
        var filmstripFocused = focused is Visual visual && (ReferenceEquals(visual, Filmstrip) || Filmstrip.IsAncestorOf(visual));
        var stepFrames = viewModel.IsVideo && !filmstripFocused;
        var command = args.Key switch
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
            Key.Escape => viewModel.ResetCropCommand,
            Key.I when viewModel.IsVideo => viewModel.MarkInCommand,
            Key.O when viewModel.IsVideo => viewModel.MarkOutCommand,
            Key.Space => viewModel.TogglePlaybackCommand,
            _ => null
        };
        if (command is null || !command.CanExecute(null))
            return;
        command.Execute(null);
        args.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs args)
    {
        if (args.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return;
        args.Handled = true;
        _ = viewModel.OpenPathAsync(paths[0]);
    }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        args.Effects = args.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        args.Handled = true;
    }

    private void FilmstripMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (FindScrollViewer(Filmstrip) is not { } scroll)
            return;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - args.Delta * 1.5);
        args.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer)
                return viewer;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }
        return null;
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.TourRequested -= OnTourRequested;
        foreach (var image in thumbnailRequests.Keys.ToArray()) ReleaseThumbnail(image);
        viewModel.Dispose();
    }
}
