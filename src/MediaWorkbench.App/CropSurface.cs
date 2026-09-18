using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

public sealed class CropSurface : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(CropSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(nameof(Selection), typeof(PixelCrop), typeof(CropSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty IsCroppingProperty = DependencyProperty.Register(nameof(IsCropping), typeof(bool), typeof(CropSurface), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public PixelCrop? Selection { get => (PixelCrop?)GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public bool IsCropping { get => (bool)GetValue(IsCroppingProperty); set => SetValue(IsCroppingProperty, value); }
    private Point? dragStart;

    public CropSurface() => Focusable = true;

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Tokens.Brush("MonitorBrush", Color.FromRgb(20, 19, 16)), null, new Rect(RenderSize));
        if (Source is not { } image || ActualWidth <= 0 || ActualHeight <= 0) return;
        var scale = Math.Min(ActualWidth / image.PixelWidth, ActualHeight / image.PixelHeight);
        var bounds = new Rect((ActualWidth - image.PixelWidth * scale) / 2, (ActualHeight - image.PixelHeight * scale) / 2, image.PixelWidth * scale, image.PixelHeight * scale);
        context.DrawImage(image, bounds);
        if (Selection is { } crop)
        {
            var selected = new Rect(bounds.X + crop.X * scale, bounds.Y + crop.Y * scale, crop.Width * scale, crop.Height * scale);
            var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(selected));
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb(145, 0, 0, 0)), null, shade);
            context.DrawRectangle(null, new Pen(Tokens.Brush("AccentBrush", Color.FromRgb(235, 188, 69)), 2), selected);
        }
        Cursor = IsCropping ? Cursors.Cross : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        // Clicking the preview gives it keyboard focus so Left/Right step frames instead of changing the filmstrip file.
        Focus();
        if (!IsCropping || Source is null) return;
        dragStart = args.GetPosition(this);
        CaptureMouse();
        args.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        if (dragStart is not { } start || Source is not { } source || args.LeftButton != MouseButtonState.Pressed) return;
        var end = args.GetPosition(this);
        SetCurrentValue(SelectionProperty, PixelCrop.FromDrag(start.X, start.Y, end.X, end.Y, ActualWidth, ActualHeight, source.PixelWidth, source.PixelHeight));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        dragStart = null;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs args) => dragStart = null;
}
