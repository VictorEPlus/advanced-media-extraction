using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MediaWorkbench.App;

// Filmstrip scrolling: the wheel glides instead of jumping, and with "Preview follows scroll" on, the preview
// shows the thumbnail under the marker on every scroll step and opens that file once scrolling settles.
public partial class MainWindow
{
    /// <summary>How long the filmstrip must rest before the file under the marker is opened for real.</summary>
    internal static readonly TimeSpan FollowSettleDelay = TimeSpan.FromMilliseconds(140);
    private const double GlideSeconds = 0.085;
    private ScrollViewer? filmstripScroll;
    private double? glideTarget;
    private long glideTicks;
    private bool gliding;
    private bool quietGlide;
    private bool committing;
    private DispatcherTimer? settleTimer;
    private VirtualizingStackPanel? itemsHost;

    private ScrollViewer? FilmstripScroll => filmstripScroll ??= FindScrollViewer(Filmstrip);

    private void FilmstripMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (FilmstripScroll is not { } scroll)
            return;
        args.Handled = true;
        if (scroll.ScrollableWidth <= 0)
        {
            // Everything fits without scrolling: with follow on, the wheel walks through the files instead.
            if (viewModel.FollowFilmstrip)
                (args.Delta < 0 ? viewModel.NextAssetCommand : viewModel.PreviousAssetCommand).Execute(null);
            return;
        }
        quietGlide = false;
        GlideTo((glideTarget ?? scroll.HorizontalOffset) - args.Delta * 1.5);
    }

    private int frameWheelRemainder;

    /// <summary>
    /// Over the frame timeline or the sound under it, the wheel steps through the video: one frame per notch, ten with Shift,
    /// towards you for the next frame. Over the picture the plain wheel zooms, so there it is Ctrl+wheel that steps frames. High-resolution wheels and touchpads send fractions of a notch, which add up.
    /// </summary>
    private void FrameWheel(object sender, MouseWheelEventArgs args)
    {
        if (!viewModel.IsVideo || !viewModel.HasFrames || IsTourActive)
            return;
        args.Handled = true;
        frameWheelRemainder += args.Delta;
        var notches = frameWheelRemainder / Mouse.MouseWheelDeltaForOneLine;
        if (notches == 0)
            return;
        frameWheelRemainder -= notches * Mouse.MouseWheelDeltaForOneLine;
        viewModel.StepFrames(-notches * ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1));
    }

    /// <summary>For checks that cannot turn a real wheel.</summary>
    internal void TurnFrameWheel(int delta) =>
        FrameWheel(this, new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = MouseWheelEvent });

    /// <summary>Eases the filmstrip towards <paramref name="offset"/>; further wheel notches move the target, so fast spinning stays fluid.</summary>
    private void GlideTo(double offset)
    {
        if (FilmstripScroll is not { } scroll)
            return;
        glideTarget = Math.Clamp(offset, 0, scroll.ScrollableWidth);
        if (gliding)
            return;
        gliding = true;
        glideTicks = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnGlideFrame;
    }

    private void StopGlide()
    {
        if (gliding)
            CompositionTarget.Rendering -= OnGlideFrame;
        gliding = false;
        quietGlide = false;
        glideTarget = null;
    }

    private void OnGlideFrame(object? sender, EventArgs args)
    {
        if (FilmstripScroll is not { } scroll || glideTarget is not { } target)
        {
            StopGlide();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Min(0.1, Stopwatch.GetElapsedTime(glideTicks, now).TotalSeconds);
        glideTicks = now;
        var current = scroll.HorizontalOffset;
        target = Math.Clamp(target, 0, scroll.ScrollableWidth);
        // Frame-rate independent ease-out: the same glide at 60 Hz and 144 Hz.
        var next = current + (target - current) * (1 - Math.Exp(-elapsed / GlideSeconds));
        if (Math.Abs(target - next) < 0.5)
        {
            scroll.ScrollToHorizontalOffset(target);
            StopGlide();
            return;
        }
        scroll.ScrollToHorizontalOffset(next);
    }

    /// <summary>Grabbing the scrollbar or clicking a thumbnail takes over from a glide in progress.</summary>
    private void FilmstripMouseDown(object sender, MouseButtonEventArgs args) => StopGlide();

    private void FilmstripScrollChanged(object sender, ScrollChangedEventArgs args) => UpdateFollowFocus(args.HorizontalChange != 0 && IsUserScrolling);

    private void FilmstripSizeChanged(object sender, SizeChangedEventArgs args) => UpdateFollowFocus(false);

    /// <summary>
    /// Only scrolling the person does moves the preview: the wheel glide, or the mouse held on the scrollbar. A scan adding files,
    /// a filter, a thumbnail-size change or bringing a clicked file to the marker also scroll the strip, and must not change the preview.
    /// </summary>
    private bool IsUserScrolling => gliding && !quietGlide
        || Mouse.LeftButton == MouseButtonState.Pressed && Mouse.Captured is Visual captured && !ReferenceEquals(captured, Filmstrip) && captured.IsDescendantOf(Filmstrip) && captured is not ListBoxItem;

    /// <summary>
    /// Where along the filmstrip the preview looks: the middle, except within half a view of either end, where it slides
    /// out to the edge so the first and last files can be reached. Continuous, so the marker never jumps.
    /// </summary>
    internal static double FollowFocusX(double offset, double scrollable, double viewport)
    {
        if (viewport <= 0) return 0;
        var middle = viewport / 2;
        if (scrollable <= 0) return middle;
        var ramp = Math.Min(middle, scrollable / 2);
        if (offset < ramp) return middle * offset / ramp;
        if (offset > scrollable - ramp) return viewport - middle * (scrollable - offset) / ramp;
        return middle;
    }

    private void UpdateFollowFocus(bool follow)
    {
        if (!viewModel.FollowFilmstrip || FilmstripScroll is not { } scroll || scroll.ViewportWidth <= 0)
            return;
        var focus = FollowFocusX(scroll.HorizontalOffset, scroll.ScrollableWidth, scroll.ViewportWidth);
        var origin = scroll.TranslatePoint(new Point(0, 0), Filmstrip);
        // Keep the marker inside the strip even at the very ends.
        Canvas.SetLeft(FollowMarkerShape, origin.X + Math.Clamp(focus, 8, Math.Max(8, scroll.ViewportWidth - 8)));
        Canvas.SetTop(FollowMarkerShape, origin.Y);
        if (!follow || scroll.ScrollableWidth <= 0)
            return;
        if (AssetAt(origin.X + focus) is { } item)
            Follow(item);
    }

    /// <summary>
    /// The file whose thumbnail is closest to this x position of the filmstrip. Only the few dozen thumbnails that exist right now
    /// are looked at; a recycled container that is not showing a file is skipped.
    /// </summary>
    private AssetViewModel? AssetAt(double x)
    {
        itemsHost ??= FindVisualChild<VirtualizingStackPanel>(Filmstrip);
        if (itemsHost is null)
            return null;
        AssetViewModel? best = null;
        var bestDistance = double.MaxValue;
        foreach (var child in itemsHost.Children)
        {
            if (child is not ListBoxItem { DataContext: AssetViewModel item, Visibility: Visibility.Visible } container
                || container.ActualWidth <= 0 || Filmstrip.ItemContainerGenerator.IndexFromContainer(container) < 0)
                continue;
            var left = container.TranslatePoint(new Point(0, 0), Filmstrip).X;
            var distance = x < left ? left - x : x > left + container.ActualWidth ? x - left - container.ActualWidth : 0;
            if (distance < bestDistance)
            {
                best = item;
                bestDistance = distance;
            }
        }
        return bestDistance <= 24 ? best : null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private void Follow(AssetViewModel item)
    {
        viewModel.PeekAsset(item);
        if (!viewModel.IsPeeking)
            return;
        settleTimer ??= CreateSettleTimer();
        settleTimer.Stop();
        settleTimer.Start();
    }

    private DispatcherTimer CreateSettleTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FollowSettleDelay };
        timer.Tick += (_, _) =>
        {
            // Still gliding or the button is held on the scrollbar: keep peeking, open the file when it really rests.
            if (gliding || Mouse.LeftButton == MouseButtonState.Pressed)
                return;
            timer.Stop();
            committing = true;
            try { viewModel.CommitPeek(); }
            finally { committing = false; }
        };
        return timer;
    }

    /// <summary>With follow on, a clicked thumbnail glides to the marker so the strip and the preview agree.</summary>
    private void BringToMarker(AssetViewModel item)
    {
        if (FilmstripScroll is not { } scroll || Filmstrip.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
            return;
        var left = container.TranslatePoint(new Point(0, 0), scroll).X;
        var contentCentre = scroll.HorizontalOffset + left + container.ActualWidth / 2;
        quietGlide = true;
        GlideTo(contentCentre - scroll.ViewportWidth / 2);
    }

    /// <summary>Selection made by a click or a key while follow is on: bring it to the marker. One that came from the marker is already there.</summary>
    private void RevealSelection(AssetViewModel item)
    {
        if (!viewModel.FollowFilmstrip)
        {
            Filmstrip.ScrollIntoView(item);
            return;
        }
        if (committing)
            return;
        StopGlide();
        Filmstrip.ScrollIntoView(item);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (ReferenceEquals(viewModel.SelectedAsset, item)) BringToMarker(item); });
    }

    /// <summary>For checks that cannot turn a real wheel: follow whatever is under the marker right now.</summary>
    internal void FollowMarkerNow() => UpdateFollowFocus(true);

    internal string FollowDiagnostics => FilmstripScroll is not { } scroll ? "no scroll viewer"
        : $"offset {scroll.HorizontalOffset:0} scrollable {scroll.ScrollableWidth:0} viewport {scroll.ViewportWidth:0}x{scroll.ViewportHeight:0} origin {scroll.TranslatePoint(new Point(0, 0), Filmstrip)} live thumbnails {itemsHost?.Children.OfType<ListBoxItem>().Count(item => Filmstrip.ItemContainerGenerator.IndexFromContainer(item) >= 0)}";

    private void OnFollowFilmstripChanged()
    {
        StopGlide();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            UpdateFollowFocus(false);
            if (viewModel.FollowFilmstrip && viewModel.SelectedAsset is { } selected && viewModel.IsVisible(selected))
                RevealSelection(selected);
        });
    }
}

// Focus view: everything except the preview and its tools is put away, and brought back exactly as it was.
public partial class MainWindow
{
    private (bool Sources, bool Inspector)? focusRestore;

    private void ApplyFocusView()
    {
        var focus = viewModel.IsFocusView;
        if (focus && focusRestore is null)
        {
            focusRestore = (viewModel.ShowSources, viewModel.ShowInspector);
            viewModel.ShowSources = false;
            viewModel.ShowInspector = false;
        }
        var visibility = focus ? Visibility.Collapsed : Visibility.Visible;
        HeaderBar.Visibility = visibility;
        FilterBar.Visibility = visibility;
        CenterHeader.Visibility = visibility;
        FilmstripPanel.Visibility = visibility;
        StatusRow.Visibility = visibility;
        RootGrid.RowDefinitions[0].Height = new GridLength(focus ? 0 : 52);
        RootGrid.RowDefinitions[3].Height = new GridLength(focus ? 0 : 24);
        if (!focus && focusRestore is { } restore)
        {
            focusRestore = null;
            viewModel.ShowSources = restore.Sources;
            viewModel.ShowInspector = restore.Inspector;
        }
        if (focus)
            PreviewSurface.Focus();
    }
}
