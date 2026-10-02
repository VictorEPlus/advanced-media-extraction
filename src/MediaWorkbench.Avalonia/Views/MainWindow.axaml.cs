using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaWorkbench.Avalonia.ViewModels;

namespace MediaWorkbench.Avalonia.Views;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer toastTimer;
    private readonly DispatcherTimer glide;
    private ScrollViewer? filmstripScroller;
    private double glideTarget;

    public MainWindow()
    {
        InitializeComponent();
        toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) => HideToast());
        glide = new DispatcherTimer(TimeSpan.FromMilliseconds(1000 / 120.0), DispatcherPriority.Render, (_, _) => GlideStep());
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Filmstrip.AddHandler(PointerWheelChangedEvent, OnFilmstripWheel, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel shell) shell.PropertyChanged += OnShellChanged;
        };
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private async void AddFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (Shell is not { } shell) return;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add a folder to the workspace", AllowMultiple = true });
        foreach (var folder in picked)
            if (folder.TryGetLocalPath() is { } path)
                await shell.AddFolderAsync(path);
    }

    /// <summary>Keys work anywhere except while typing: Space plays, arrows step frames, Home restarts.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Shell is not { } shell || e.Source is TextBox) return;
        var handled = true;
        switch (e.Key)
        {
            case Key.Space when shell.CanPlay: shell.PlayPauseCommand.Execute(null); break;
            case Key.Right when shell.CanStep: shell.StepForwardCommand.Execute(null); break;
            case Key.Left when shell.CanStep: shell.StepBackCommand.Execute(null); break;
            case Key.Home when shell.CanPlay: shell.RestartCommand.Execute(null); break;
            case Key.B when e.KeyModifiers == KeyModifiers.Control: shell.ToggleSidebarCommand.Execute(null); break;
            case Key.E when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift): shell.RevealSelectedCommand.Execute(null); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    /// <summary>The wheel moves the filmstrip sideways and glides to a stop instead of jumping a notch at a time.</summary>
    private void OnFilmstripWheel(object? sender, PointerWheelEventArgs e)
    {
        filmstripScroller ??= Filmstrip.FindDescendantOfType<ScrollViewer>();
        if (filmstripScroller is not { } scroller) return;
        var maximum = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        var start = glide.IsEnabled ? glideTarget : scroller.Offset.X;
        glideTarget = Math.Clamp(start - (e.Delta.Y + e.Delta.X) * 220, 0, maximum);
        glide.Start();
        e.Handled = true;
    }

    private void GlideStep()
    {
        if (filmstripScroller is not { } scroller) { glide.Stop(); return; }
        var x = scroller.Offset.X;
        var next = x + (glideTarget - x) * 0.2;
        if (Math.Abs(glideTarget - next) < 0.5)
        {
            next = glideTarget;
            glide.Stop();
        }
        scroller.Offset = new Vector(next, scroller.Offset.Y);
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Status) && Shell?.Status is { Length: > 0 })
        {
            Toast.Opacity = 1;
            toastTimer.Stop();
            toastTimer.Start();
        }
        // The selected card is brought into view when the file changes from somewhere other than a click on it.
        if (e.PropertyName == nameof(ShellViewModel.SelectedItem) && Shell?.SelectedItem is { } item)
            Filmstrip.ScrollIntoView(item);
    }

    private void HideToast()
    {
        toastTimer.Stop();
        Toast.Opacity = 0;
    }
}
