using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MediaWorkbench.Avalonia;

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
    private bool scrollbarHeld;
    private DispatcherTimer? settleTimer;
    private DispatcherTimer? glideTimer;
    private double lastOffset;

    private ScrollViewer? FilmstripScroll
    {
        get
        {
            if (filmstripScroll is null && Filmstrip.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } found)
            {
                filmstripScroll = found;
                found.ScrollChanged += (_, _) =>
                {
                    var moved = Math.Abs(found.Offset.X - lastOffset) > 0.01;
                    lastOffset = found.Offset.X;
                    UpdateFollowFocus(moved && IsUserScrolling);
                };
                // The scrollbar held down counts as the person scrolling, like the wheel.
                found.AddHandler(PointerPressedEvent, (_, args) => scrollbarHeld = args.Source is Visual source && source.FindAncestorOfType<ScrollBar>() is not null, global::Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
                found.AddHandler(PointerReleasedEvent, (_, _) => scrollbarHeld = false, global::Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            }
            return filmstripScroll;
        }
    }

    private double ScrollableWidth => FilmstripScroll is { } scroll ? Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width) : 0;

    private void FilmstripMouseWheel(object? sender, PointerWheelEventArgs args)
    {
        if (FilmstripScroll is not { } scroll)
            return;
        args.Handled = true;
        var delta = args.Delta.Y + args.Delta.X;
        if (ScrollableWidth <= 0)
        {
            // Everything fits without scrolling: with follow on, the wheel walks through the files instead.
            if (viewModel.FollowFilmstrip)
                (delta < 0 ? viewModel.NextAssetCommand : viewModel.PreviousAssetCommand).Execute(null);
            return;
        }
        quietGlide = false;
        GlideTo((glideTarget ?? scroll.Offset.X) - delta * 180);
    }

    private double frameWheelRemainder;

    /// <summary>
    /// Over the frame timeline or the sound under it, the wheel steps through the video: one frame per notch, ten with Shift,
    /// towards you for the next frame. Over the picture the plain wheel zooms, so there it is Ctrl+wheel that steps frames. High-resolution wheels and touchpads send fractions of a notch, which add up.
    /// </summary>
    private void FrameWheel(object? sender, PointerWheelEventArgs args)
    {
        if (!viewModel.IsVideo || !viewModel.HasFrames || IsTourActive || args.Handled)
            return;
        args.Handled = true;
        frameWheelRemainder += args.Delta.Y;
        var notches = (int)Math.Truncate(frameWheelRemainder);
        if (notches == 0)
            return;
        frameWheelRemainder -= notches;
        viewModel.StepFrames(-notches * ((args.KeyModifiers & KeyModifiers.Shift) != 0 ? 10 : 1));
    }

    /// <summary>For checks that cannot turn a real wheel: notches towards you are negative, as with a mouse.</summary>
    internal void TurnFrameWheel(int notches)
    {
        if (!viewModel.IsVideo || !viewModel.HasFrames) return;
        viewModel.StepFrames(-notches);
    }

    /// <summary>Eases the filmstrip towards <paramref name="offset"/>; further wheel notches move the target, so fast spinning stays fluid.</summary>
    private void GlideTo(double offset)
    {
        if (FilmstripScroll is null)
            return;
        glideTarget = Math.Clamp(offset, 0, ScrollableWidth);
        if (gliding)
            return;
        gliding = true;
        glideTicks = Stopwatch.GetTimestamp();
        glideTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1000 / 120.0), DispatcherPriority.Render, (_, _) => OnGlideFrame());
        glideTimer.Start();
    }

    private void StopGlide()
    {
        glideTimer?.Stop();
        gliding = false;
        quietGlide = false;
        glideTarget = null;
    }

    private void OnGlideFrame()
    {
        if (FilmstripScroll is not { } scroll || glideTarget is not { } target)
        {
            StopGlide();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Min(0.1, Stopwatch.GetElapsedTime(glideTicks, now).TotalSeconds);
        glideTicks = now;
        var current = scroll.Offset.X;
        target = Math.Clamp(target, 0, ScrollableWidth);
        // Frame-rate independent ease-out: the same glide at 60 Hz and 144 Hz.
        var next = current + (target - current) * (1 - Math.Exp(-elapsed / GlideSeconds));
        if (Math.Abs(target - next) < 0.5)
        {
            scroll.Offset = new Vector(target, scroll.Offset.Y);
            StopGlide();
            return;
        }
        scroll.Offset = new Vector(next, scroll.Offset.Y);
    }

    /// <summary>
    /// Only scrolling the person does moves the preview: the wheel glide, or the mouse held on the scrollbar. A scan adding files,
    /// a filter, a thumbnail-size change or bringing a clicked file to the marker also scroll the strip, and must not change the preview.
    /// </summary>
    private bool IsUserScrolling => gliding && !quietGlide || scrollbarHeld;

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
        if (!viewModel.FollowFilmstrip || FilmstripScroll is not { } scroll || scroll.Viewport.Width <= 0)
            return;
        var focus = FollowFocusX(scroll.Offset.X, ScrollableWidth, scroll.Viewport.Width);
        var origin = scroll.TranslatePoint(new Point(0, 0), Filmstrip) ?? default;
        // Keep the marker inside the strip even at the very ends.
        Canvas.SetLeft(FollowMarkerShape, origin.X + Math.Clamp(focus, 8, Math.Max(8, scroll.Viewport.Width - 8)));
        Canvas.SetTop(FollowMarkerShape, origin.Y);
        if (!follow || ScrollableWidth <= 0)
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
        AssetViewModel? best = null;
        var bestDistance = double.MaxValue;
        foreach (var container in Filmstrip.GetRealizedContainers())
        {
            if (container is not ListBoxItem { DataContext: AssetViewModel item, IsVisible: true } || container.Bounds.Width <= 0 || Filmstrip.IndexFromContainer(container) < 0)
                continue;
            var left = container.TranslatePoint(new Point(0, 0), Filmstrip)?.X ?? 0;
            var distance = x < left ? left - x : x > left + container.Bounds.Width ? x - left - container.Bounds.Width : 0;
            if (distance < bestDistance)
            {
                best = item;
                bestDistance = distance;
            }
        }
        return bestDistance <= 24 ? best : null;
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
            if (gliding || scrollbarHeld)
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
        if (FilmstripScroll is not { } scroll || Filmstrip.ContainerFromItem(item) is not Control container)
            return;
        var left = container.TranslatePoint(new Point(0, 0), scroll)?.X ?? 0;
        var contentCentre = scroll.Offset.X + left + container.Bounds.Width / 2;
        quietGlide = true;
        GlideTo(contentCentre - scroll.Viewport.Width / 2);
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
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(viewModel.SelectedAsset, item)) BringToMarker(item); }, DispatcherPriority.Background);
    }

    /// <summary>For checks that cannot turn a real wheel: follow whatever is under the marker right now.</summary>
    internal void FollowMarkerNow() => UpdateFollowFocus(true);

    private void OnFollowFilmstripChanged()
    {
        StopGlide();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateFollowFocus(false);
            if (viewModel.FollowFilmstrip && viewModel.SelectedAsset is { } selected && viewModel.IsVisible(selected))
                RevealSelection(selected);
        }, DispatcherPriority.Background);
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
        HeaderBar.IsVisible = !focus;
        FilterBar.IsVisible = !focus;
        CenterHeader.IsVisible = !focus;
        FilmstripPanel.IsVisible = !focus;
        StatusRow.IsVisible = !focus;
        RootGrid.RowDefinitions[0].Height = new GridLength(focus ? 0 : 52);
        RootGrid.RowDefinitions[3].Height = new GridLength(focus ? 0 : 26);
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
