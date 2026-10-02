using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MediaWorkbench.Avalonia.Controls;

/// <summary>
/// The viewer: draws the picture fitted to the space, centred, with no letterbox colour of its own. The mouse wheel zooms towards
/// the pointer and eases there instead of jumping; dragging pans; a double-click fits again. A new file fades in over the old
/// one; a new frame of the same video replaces it at once, so stepping stays exact.
/// </summary>
public sealed class MediaSurface : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty = AvaloniaProperty.Register<MediaSurface, Bitmap?>(nameof(Source));
    /// <summary>Changes whenever the pixels of the same bitmap change (the live video picture).</summary>
    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<MediaSurface, int>(nameof(Revision));
    /// <summary>Changes when a different file is opened: zoom resets and the picture fades in.</summary>
    public static readonly StyledProperty<object?> ViewKeyProperty = AvaloniaProperty.Register<MediaSurface, object?>(nameof(ViewKey));

    private const double MaximumZoom = 16;
    private readonly DispatcherTimer animation;
    private double zoom = 1;
    private double targetZoom = 1;
    // The point of the picture (0..1) that sits at the centre of the view.
    private Point centre = new(0.5, 0.5);
    private Point targetCentre = new(0.5, 0.5);
    private Point? dragStart;
    private Point dragCentre;
    private double fade = 1;
    private DateTime fadeStarted;

    static MediaSurface()
    {
        AffectsRender<MediaSurface>(SourceProperty, RevisionProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MediaSurface>(true);
        FocusableProperty.OverrideDefaultValue<MediaSurface>(true);
    }

    public MediaSurface()
    {
        animation = new DispatcherTimer(TimeSpan.FromMilliseconds(1000 / 120.0), DispatcherPriority.Render, (_, _) => Step());
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public object? ViewKey { get => GetValue(ViewKeyProperty); set => SetValue(ViewKeyProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewKeyProperty)
        {
            zoom = targetZoom = 1;
            centre = targetCentre = new Point(0.5, 0.5);
            fade = 0;
            fadeStarted = DateTime.UtcNow;
            animation.Start();
        }
    }

    public override void Render(DrawingContext context)
    {
        // Transparent fill so the whole area takes the wheel and the mouse.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Source is not { } bitmap || bitmap.PixelSize.Width == 0)
            return;
        var target = PictureRect(bitmap.Size);
        using (context.PushOpacity(fade))
            context.DrawImage(bitmap, new Rect(bitmap.Size), target);
    }

    /// <summary>Where the picture is drawn: fitted, then zoomed around <see cref="centre"/>.</summary>
    private Rect PictureRect(Size picture)
    {
        var fit = Math.Min(Bounds.Width / picture.Width, Bounds.Height / picture.Height);
        if (double.IsNaN(fit) || fit <= 0) fit = 1;
        var width = picture.Width * fit * zoom;
        var height = picture.Height * fit * zoom;
        var left = Bounds.Width / 2 - centre.X * width;
        var top = Bounds.Height / 2 - centre.Y * height;
        // At fit size the picture is simply centred; zoomed in, its edges never come further in than the view's edges.
        if (width <= Bounds.Width) left = (Bounds.Width - width) / 2;
        else left = Math.Clamp(left, Bounds.Width - width, 0);
        if (height <= Bounds.Height) top = (Bounds.Height - height) / 2;
        else top = Math.Clamp(top, Bounds.Height - height, 0);
        return new Rect(left, top, width, height);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is not { } bitmap) return;
        var factor = Math.Pow(1.18, e.Delta.Y);
        var nextZoom = Math.Clamp(targetZoom * factor, 1, MaximumZoom);
        // Keep the point under the pointer where it is: work out which point of the picture it is, then move the centre so that
        // point lands under the pointer again at the new zoom.
        var rect = PictureRect(bitmap.Size);
        var pointer = e.GetPosition(this);
        var picturePoint = new Point((pointer.X - rect.X) / rect.Width, (pointer.Y - rect.Y) / rect.Height);
        var fit = Math.Min(Bounds.Width / bitmap.Size.Width, Bounds.Height / bitmap.Size.Height);
        var width = bitmap.Size.Width * fit * nextZoom;
        var height = bitmap.Size.Height * fit * nextZoom;
        targetCentre = new Point(
            Math.Clamp(picturePoint.X - (pointer.X - Bounds.Width / 2) / width, 0, 1),
            Math.Clamp(picturePoint.Y - (pointer.Y - Bounds.Height / 2) / height, 0, 1));
        if (nextZoom <= 1.0001) targetCentre = new Point(0.5, 0.5);
        targetZoom = nextZoom;
        animation.Start();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.ClickCount == 2)
        {
            targetZoom = 1;
            targetCentre = new Point(0.5, 0.5);
            animation.Start();
            return;
        }
        if (targetZoom > 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            dragStart = e.GetPosition(this);
            dragCentre = targetCentre;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragStart is not { } start || Source is not { } bitmap) return;
        var rect = PictureRect(bitmap.Size);
        var now = e.GetPosition(this);
        targetCentre = centre = new Point(
            Math.Clamp(dragCentre.X - (now.X - start.X) / rect.Width, 0, 1),
            Math.Clamp(dragCentre.Y - (now.Y - start.Y) / rect.Height, 0, 1));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragStart = null;
        e.Pointer.Capture(null);
        Cursor = null;
    }

    /// <summary>One animation tick: zoom and centre ease towards their targets, and a new file finishes fading in.</summary>
    private void Step()
    {
        const double ease = 0.22;
        zoom += (targetZoom - zoom) * ease;
        centre = new Point(centre.X + (targetCentre.X - centre.X) * ease, centre.Y + (targetCentre.Y - centre.Y) * ease);
        fade = Math.Min(1, (DateTime.UtcNow - fadeStarted).TotalMilliseconds / 180);
        var settled = Math.Abs(targetZoom - zoom) < 0.001 && Math.Abs(targetCentre.X - centre.X) < 0.0005 && Math.Abs(targetCentre.Y - centre.Y) < 0.0005 && fade >= 1;
        if (settled)
        {
            zoom = targetZoom;
            centre = targetCentre;
            animation.Stop();
        }
        InvalidateVisual();
    }
}
