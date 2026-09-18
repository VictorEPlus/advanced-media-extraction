using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

/// <summary>
/// The still picture in the preview. Two ways to mark an area, never both at once:
/// dragging a rectangle for the clipboard (<see cref="IsCropping"/>), and adjusting four edges for the video crop
/// (<see cref="IsEdgeEditing"/>). In edge editing every edge has a grabber to drag, one edge can be selected and nudged with
/// the arrow keys, and the picture is shown turned by <see cref="Rotation"/> so it looks like the export will.
/// </summary>
public sealed class CropSurface : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(CropSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, args) =>
    {
        // The next frame of the same video keeps the zoom; a picture of another size cannot mean the same place, so it starts fitted.
        if (args.OldValue is BitmapSource before && args.NewValue is BitmapSource after && (before.PixelWidth != after.PixelWidth || before.PixelHeight != after.PixelHeight))
            ((CropSurface)d).ResetView();
    }));
    public static readonly DependencyProperty ViewKeyProperty = DependencyProperty.Register(nameof(ViewKey), typeof(object), typeof(CropSurface), new FrameworkPropertyMetadata(null, (d, _) => ((CropSurface)d).ResetView()));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(nameof(Selection), typeof(PixelCrop), typeof(CropSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty IsCroppingProperty = DependencyProperty.Register(nameof(IsCropping), typeof(bool), typeof(CropSurface), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EdgeSelectionProperty = DependencyProperty.Register(nameof(EdgeSelection), typeof(PixelCrop), typeof(CropSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty IsEdgeEditingProperty = DependencyProperty.Register(nameof(IsEdgeEditing), typeof(bool), typeof(CropSurface), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => d.SetCurrentValue(SelectedEdgeProperty, CropEdge.None)));
    public static readonly DependencyProperty RotationProperty = DependencyProperty.Register(nameof(Rotation), typeof(int), typeof(CropSurface), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedEdgeProperty = DependencyProperty.Register(nameof(SelectedEdge), typeof(CropEdge), typeof(CropSurface), new FrameworkPropertyMetadata(CropEdge.None, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    /// <summary>Identifies the file being shown. Zoom and position are kept while it stays the same and reset when it changes.</summary>
    public object? ViewKey { get => GetValue(ViewKeyProperty); set => SetValue(ViewKeyProperty, value); }
    public PixelCrop? Selection { get => (PixelCrop?)GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public bool IsCropping { get => (bool)GetValue(IsCroppingProperty); set => SetValue(IsCroppingProperty, value); }
    /// <summary>The video crop in source pixels (before rotation). Null means the whole frame.</summary>
    public PixelCrop? EdgeSelection { get => (PixelCrop?)GetValue(EdgeSelectionProperty); set => SetValue(EdgeSelectionProperty, value); }
    public bool IsEdgeEditing { get => (bool)GetValue(IsEdgeEditingProperty); set => SetValue(IsEdgeEditingProperty, value); }
    /// <summary>Clockwise quarter turns in degrees; only used while edge editing.</summary>
    public int Rotation { get => (int)GetValue(RotationProperty); set => SetValue(RotationProperty, value); }
    /// <summary>The edge, as seen on screen, that the arrow keys move.</summary>
    public CropEdge SelectedEdge { get => (CropEdge)GetValue(SelectedEdgeProperty); set => SetValue(SelectedEdgeProperty, value); }

    private const double EdgeReach = 9;
    private const double GrabberLength = 46;
    private const double GrabberThickness = 7;
    private static readonly CropEdge[] EdgeOrder = [CropEdge.Left, CropEdge.Top, CropEdge.Right, CropEdge.Bottom];
    private static readonly SolidColorBrush AccentBrush = Tokens.Brush("AccentBrush", Color.FromRgb(235, 188, 69));
    private static readonly SolidColorBrush InkBrush = Tokens.Brush("InkBrush", Color.FromRgb(243, 238, 227));
    private static readonly Brush ShadeBrush = Frozen(new SolidColorBrush(Color.FromArgb(145, 0, 0, 0)));
    private static readonly Brush GrabberOutline = Frozen(new SolidColorBrush(Color.FromArgb(200, 20, 19, 16)));
    private static readonly Brush ChipBackground = Frozen(new SolidColorBrush(Color.FromArgb(225, 46, 43, 37)));
    private Point? dragStart;
    private CropEdge dragEdge;

    public CropSurface() => Focusable = true;

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    /// <summary>Size of the picture as it is shown: turned while edge editing.</summary>
    private (int Width, int Height) ShownSize(BitmapSource image) =>
        IsEdgeEditing && VideoTransform.NormalizeRotation(Rotation) is 90 or 270 ? (image.PixelHeight, image.PixelWidth) : (image.PixelWidth, image.PixelHeight);

    /// <summary>Largest magnification relative to "fit": enough to see single pixels of a 4K frame.</summary>
    public const double MaximumZoom = 32;
    private double zoom = 1;
    // The point of the shown picture (0..1 on each axis) that sits at the centre of the view.
    private Point viewCentre = new(0.5, 0.5);
    private Point? panStart;
    private Point panStartCentre;

    /// <summary>1 means the whole picture fits the view. Kept when the picture changes, so stepping frames stays on the same spot.</summary>
    public double Zoom => zoom;
    public bool IsZoomed => zoom > 1.0001;

    /// <summary>Raised when <see cref="Zoom"/> changes, so the window can show it.</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>
    /// Where the picture is drawn. At zoom 1 it is centred and fits; zoomed in, it is placed so <see cref="viewCentre"/> is in the
    /// middle, without ever showing empty space on a side the picture could fill.
    /// </summary>
    private (Rect Bounds, double Scale) Fit(BitmapSource image)
    {
        var (width, height) = ShownSize(image);
        var scale = Math.Min(ActualWidth / width, ActualHeight / height) * zoom;
        var (shownWidth, shownHeight) = (width * scale, height * scale);
        var left = Place(ActualWidth, shownWidth, viewCentre.X);
        var top = Place(ActualHeight, shownHeight, viewCentre.Y);
        return (new Rect(left, top, shownWidth, shownHeight), scale);

        static double Place(double view, double shown, double centre) =>
            shown <= view ? (view - shown) / 2 : Math.Clamp(view / 2 - centre * shown, view - shown, 0);
    }

    /// <summary>Multiplies the zoom by <paramref name="factor"/> keeping the picture point under <paramref name="anchor"/> where it is.</summary>
    public void ZoomAt(Point anchor, double factor)
    {
        if (Source is not { } image || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var (before, _) = Fit(image);
        var target = Math.Clamp(zoom * factor, 1, MaximumZoom);
        if (Math.Abs(target - zoom) < 0.0001)
            return;
        var pointX = (anchor.X - before.X) / before.Width;
        var pointY = (anchor.Y - before.Y) / before.Height;
        zoom = target;
        var (width, height) = ShownSize(image);
        var scale = Math.Min(ActualWidth / width, ActualHeight / height) * zoom;
        // Put the same picture point back under the pointer, then let Fit keep the picture inside the view.
        viewCentre = new Point((ActualWidth / 2 - (anchor.X - pointX * width * scale)) / (width * scale), (ActualHeight / 2 - (anchor.Y - pointY * height * scale)) / (height * scale));
        NormalizeView(image);
        OnViewChanged();
    }

    public void ResetView()
    {
        if (!IsZoomed && viewCentre == new Point(0.5, 0.5))
            return;
        zoom = 1;
        viewCentre = new Point(0.5, 0.5);
        OnViewChanged();
    }

    /// <summary>The source-independent picture point (0..1, as shown) under a point of the view; for checks.</summary>
    internal Point PicturePointAt(Point point)
    {
        if (Source is not { } image) return default;
        var (bounds, _) = Fit(image);
        return new Point((point.X - bounds.X) / bounds.Width, (point.Y - bounds.Y) / bounds.Height);
    }

    /// <summary>Stores the centre that Fit actually used, so panning away from an edge responds at once instead of first using up the overshoot.</summary>
    private void NormalizeView(BitmapSource image)
    {
        var (bounds, _) = Fit(image);
        viewCentre = new Point((ActualWidth / 2 - bounds.X) / bounds.Width, (ActualHeight / 2 - bounds.Y) / bounds.Height);
    }

    private void OnViewChanged()
    {
        // Past a few screen pixels per picture pixel, smoothing only blurs what you zoomed in to see.
        RenderOptions.SetBitmapScalingMode(this, Source is { } image && Fit(image).Scale >= 3 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Unspecified);
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs args)
    {
        // Plain wheel over the picture zooms at the pointer. With Ctrl the event is left alone and steps frames instead.
        if (Source is null || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            return;
        ZoomAt(args.GetPosition(this), Math.Pow(1.2, args.Delta / (double)Mouse.MouseWheelDeltaForOneLine));
        args.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.ChangedButton == MouseButton.Middle && IsZoomed)
            BeginPan(args);
    }

    protected override void OnMouseUp(MouseButtonEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.ChangedButton == MouseButton.Middle && panStart is not null)
            EndPan();
    }

    private void BeginPan(MouseButtonEventArgs args)
    {
        panStart = args.GetPosition(this);
        panStartCentre = viewCentre;
        CaptureMouse();
        Cursor = Cursors.ScrollAll;
        args.Handled = true;
    }

    private void EndPan()
    {
        panStart = null;
        ReleaseMouseCapture();
    }

    /// <summary>The video crop in the pixels of the picture as shown (turned), always a real rectangle.</summary>
    private PixelCrop ShownCrop(BitmapSource image) =>
        VideoTransform.ToRotated(new VideoTransform(EdgeSelection, Rotation).EffectiveCrop(image.PixelWidth, image.PixelHeight), image.PixelWidth, image.PixelHeight, Rotation);

    private Rect ShownCropOnScreen(BitmapSource image)
    {
        var (bounds, scale) = Fit(image);
        var crop = ShownCrop(image);
        return new Rect(bounds.X + crop.X * scale, bounds.Y + crop.Y * scale, crop.Width * scale, crop.Height * scale);
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Tokens.Brush("MonitorBrush", Color.FromRgb(20, 19, 16)), null, new Rect(RenderSize));
        if (Source is not { } image || ActualWidth <= 0 || ActualHeight <= 0) return;
        var (bounds, scale) = Fit(image);
        if (IsEdgeEditing)
        {
            // Draw the unturned picture around the centre of where it belongs, then turn it.
            var centre = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            context.PushTransform(new RotateTransform(VideoTransform.NormalizeRotation(Rotation), centre.X, centre.Y));
            context.DrawImage(image, new Rect(centre.X - image.PixelWidth * scale / 2, centre.Y - image.PixelHeight * scale / 2, image.PixelWidth * scale, image.PixelHeight * scale));
            context.Pop();
            var selected = ShownCropOnScreen(image);
            context.DrawGeometry(ShadeBrush, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(selected)));
            context.DrawRectangle(null, new Pen(AccentBrush, 1.5), selected);
            foreach (var edge in EdgeOrder)
                DrawGrabber(context, selected, edge);
            DrawZoomChip(context);
            return;
        }
        context.DrawImage(image, bounds);
        if (Selection is { } crop)
        {
            var selected = new Rect(bounds.X + crop.X * scale, bounds.Y + crop.Y * scale, crop.Width * scale, crop.Height * scale);
            var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(selected));
            context.DrawGeometry(ShadeBrush, null, shade);
            context.DrawRectangle(null, new Pen(AccentBrush, 2), selected);
        }
        DrawZoomChip(context);
        Cursor = IsCropping ? Cursors.Cross : IsZoomed ? Cursors.SizeAll : Cursors.Arrow;
    }

    /// <summary>While zoomed in, says by how much and how to get back, in the corner where it covers least.</summary>
    private void DrawZoomChip(DrawingContext context)
    {
        if (!IsZoomed) return;
        var text = new FormattedText($"Zoom {zoom:0.#}×   drag to move, double-click to fit", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Tokens.Display, 11, InkBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var box = new Rect(10, ActualHeight - 30, text.Width + 16, 20);
        context.DrawRoundedRectangle(ChipBackground, null, box, 4, 4);
        context.DrawText(text, new Point(box.X + 8, box.Y + 2));
    }

    private void DrawGrabber(DrawingContext context, Rect selected, CropEdge edge)
    {
        var chosen = edge == SelectedEdge;
        var brush = chosen ? InkBrush : AccentBrush;
        var (start, end) = EdgeLine(selected, edge);
        if (chosen)
            context.DrawLine(new Pen(InkBrush, 3), start, end);
        var middle = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        var vertical = edge is CropEdge.Left or CropEdge.Right;
        var length = Math.Min(GrabberLength, Math.Max(14, (vertical ? selected.Height : selected.Width) - 12));
        var grabber = vertical
            ? new Rect(middle.X - GrabberThickness / 2, middle.Y - length / 2, GrabberThickness, length)
            : new Rect(middle.X - length / 2, middle.Y - GrabberThickness / 2, length, GrabberThickness);
        context.DrawRoundedRectangle(brush, new Pen(GrabberOutline, 1), grabber, 3.5, 3.5);
    }

    private static (Point Start, Point End) EdgeLine(Rect selected, CropEdge edge) => edge switch
    {
        CropEdge.Left => (selected.TopLeft, selected.BottomLeft),
        CropEdge.Right => (selected.TopRight, selected.BottomRight),
        CropEdge.Top => (selected.TopLeft, selected.TopRight),
        _ => (selected.BottomLeft, selected.BottomRight)
    };

    /// <summary>The edge under a point: within reach of its line and alongside it. The nearest wins where two meet at a corner.</summary>
    private CropEdge EdgeAt(Point point, Rect selected)
    {
        var best = CropEdge.None;
        var bestDistance = EdgeReach;
        foreach (var edge in EdgeOrder)
        {
            var vertical = edge is CropEdge.Left or CropEdge.Right;
            var along = vertical ? point.Y >= selected.Top - EdgeReach && point.Y <= selected.Bottom + EdgeReach : point.X >= selected.Left - EdgeReach && point.X <= selected.Right + EdgeReach;
            var distance = edge switch
            {
                CropEdge.Left => Math.Abs(point.X - selected.Left),
                CropEdge.Right => Math.Abs(point.X - selected.Right),
                CropEdge.Top => Math.Abs(point.Y - selected.Top),
                _ => Math.Abs(point.Y - selected.Bottom)
            };
            if (along && distance <= bestDistance)
            {
                best = edge;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>Moves the selected edge by whole source pixels; positive is right or down on screen. Returns false when no edge is selected.</summary>
    public bool Nudge(int delta) => SelectedEdge != CropEdge.None && MoveEdge(SelectedEdge, delta, relative: true);

    /// <summary>Selects the next or previous edge, clockwise from the left.</summary>
    public void CycleEdge(int direction)
    {
        var index = Array.IndexOf(EdgeOrder, SelectedEdge);
        SetCurrentValue(SelectedEdgeProperty, EdgeOrder[index < 0 ? (direction > 0 ? 0 : EdgeOrder.Length - 1) : (index + direction + EdgeOrder.Length) % EdgeOrder.Length]);
    }

    /// <summary>Moves an edge by (relative) or to (absolute) a position in shown pixels, and writes the result back in source pixels.</summary>
    private bool MoveEdge(CropEdge edge, int value, bool relative)
    {
        if (!IsEdgeEditing || Source is not { } image || edge == CropEdge.None)
            return false;
        var (width, height) = ShownSize(image);
        var shown = ShownCrop(image);
        var delta = relative ? value : value - edge switch
        {
            CropEdge.Left => shown.X,
            CropEdge.Right => shown.X + shown.Width,
            CropEdge.Top => shown.Y,
            _ => shown.Y + shown.Height
        };
        var moved = VideoTransform.MoveEdge(shown, edge, delta, width, height);
        if (moved != shown)
            SetCurrentValue(EdgeSelectionProperty, VideoTransform.FromRotated(moved, image.PixelWidth, image.PixelHeight, Rotation));
        return true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        // Clicking the preview gives it keyboard focus so Left/Right step frames instead of changing the filmstrip file.
        Focus();
        if (Source is not { } image) return;
        if (args.ClickCount == 2 && !IsCropping && (!IsEdgeEditing || EdgeAt(args.GetPosition(this), ShownCropOnScreen(image)) == CropEdge.None))
        {
            ResetView();
            args.Handled = true;
            return;
        }
        if (IsEdgeEditing)
        {
            dragEdge = EdgeAt(args.GetPosition(this), ShownCropOnScreen(image));
            SetCurrentValue(SelectedEdgeProperty, dragEdge);
            if (dragEdge != CropEdge.None)
                CaptureMouse();
            else if (IsZoomed)
                BeginPan(args);
            args.Handled = true;
            return;
        }
        if (!IsCropping)
        {
            // Nothing to mark: dragging moves a zoomed picture around.
            if (IsZoomed)
                BeginPan(args);
            return;
        }
        dragStart = ToPicturePixels(image, args.GetPosition(this));
        CaptureMouse();
        args.Handled = true;
    }

    /// <summary>A point of the view in source pixels (fractions allowed), wherever the picture is zoomed and panned to. Not used while the picture is shown turned.</summary>
    private Point ToPicturePixels(BitmapSource image, Point point)
    {
        var (bounds, scale) = Fit(image);
        return new Point((point.X - bounds.X) / scale, (point.Y - bounds.Y) / scale);
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        if (Source is not { } source) return;
        var position = args.GetPosition(this);
        if (panStart is { } from)
        {
            if (args.LeftButton != MouseButtonState.Pressed && args.MiddleButton != MouseButtonState.Pressed)
            {
                EndPan();
                return;
            }
            var (bounds, _) = Fit(source);
            viewCentre = new Point(panStartCentre.X - (position.X - from.X) / bounds.Width, panStartCentre.Y - (position.Y - from.Y) / bounds.Height);
            var asked = viewCentre;
            NormalizeView(source);
            // At an edge the picture stops; restart the drag from there so reversing direction responds immediately.
            if (asked != viewCentre) { panStart = position; panStartCentre = viewCentre; }
            InvalidateVisual();
            return;
        }
        if (IsEdgeEditing)
        {
            if (dragEdge != CropEdge.None && args.LeftButton == MouseButtonState.Pressed)
            {
                var (bounds, scale) = Fit(source);
                var vertical = dragEdge is CropEdge.Left or CropEdge.Right;
                MoveEdge(dragEdge, (int)Math.Round(((vertical ? position.X - bounds.X : position.Y - bounds.Y)) / scale), relative: false);
            }
            Cursor = (dragEdge != CropEdge.None ? dragEdge : EdgeAt(position, ShownCropOnScreen(source))) switch
            {
                CropEdge.Left or CropEdge.Right => Cursors.SizeWE,
                CropEdge.Top or CropEdge.Bottom => Cursors.SizeNS,
                _ => IsZoomed ? Cursors.SizeAll : Cursors.Arrow
            };
            return;
        }
        if (dragStart is not { } start || args.LeftButton != MouseButtonState.Pressed) return;
        var end = ToPicturePixels(source, position);
        var left = (int)Math.Floor(Math.Clamp(Math.Min(start.X, end.X), 0, source.PixelWidth));
        var top = (int)Math.Floor(Math.Clamp(Math.Min(start.Y, end.Y), 0, source.PixelHeight));
        var right = (int)Math.Ceiling(Math.Clamp(Math.Max(start.X, end.X), 0, source.PixelWidth));
        var bottom = (int)Math.Ceiling(Math.Clamp(Math.Max(start.Y, end.Y), 0, source.PixelHeight));
        SetCurrentValue(SelectionProperty, right > left && bottom > top ? new PixelCrop(left, top, right - left, bottom - top) : null);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        dragStart = null;
        dragEdge = CropEdge.None;
        panStart = null;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        dragStart = null;
        dragEdge = CropEdge.None;
        panStart = null;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (Source is { } image && IsZoomed)
            NormalizeView(image);
    }
}
