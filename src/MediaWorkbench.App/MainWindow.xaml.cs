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

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox)
            return;
        if (Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.C)
        {
            viewModel.CopyPreviewCommand.Execute(null);
            args.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var command = args.Key switch
        {
            Key.F => viewModel.ToggleFavoriteCommand,
            Key.E => viewModel.ExportFrameCommand,
            Key.Left => viewModel.IsVideo ? viewModel.PreviousFrameCommand : viewModel.PreviousAssetCommand,
            Key.Right => viewModel.IsVideo ? viewModel.NextFrameCommand : viewModel.NextAssetCommand,
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

    private void FilmstripMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (FindScrollViewer(Filmstrip) is not { } scroll)
            return;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - args.Delta * 1.5);
        args.Handled = true;
    }

    private void FilmstripSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Filmstrip.SelectedItem is { } selected)
            Filmstrip.ScrollIntoView(selected);
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
        foreach (var image in thumbnailRequests.Keys.ToArray()) ReleaseThumbnail(image);
        viewModel.Dispose();
    }
}
