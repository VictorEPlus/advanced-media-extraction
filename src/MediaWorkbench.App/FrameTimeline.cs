using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

/// <summary>
/// Frame-ordinal timeline with a playhead, in/out handles, a shaded selection and hover readout.
/// Dragging the playhead shows a pending position and commits <see cref="Value"/> on release so every
/// intermediate frame is not decoded. Keyboard: Left/Right step one frame, Shift steps ten, Home/End jump.
/// </summary>
public sealed class FrameTimeline : FrameworkElement
{
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(FrameTimeline), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(FrameTimeline), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty InFrameProperty = DependencyProperty.Register(nameof(InFrame), typeof(int), typeof(FrameTimeline), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty OutFrameProperty = DependencyProperty.Register(nameof(OutFrame), typeof(int), typeof(FrameTimeline), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty DisplayedFrameProperty = DependencyProperty.Register(nameof(DisplayedFrame), typeof(int), typeof(FrameTimeline), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LivePositionProperty = DependencyProperty.Register(nameof(LivePosition), typeof(int), typeof(FrameTimeline), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FramesProperty = DependencyProperty.Register(nameof(Frames), typeof(IReadOnlyList<VideoFrame>), typeof(FrameTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int InFrame { get => (int)GetValue(InFrameProperty); set => SetValue(InFrameProperty, value); }
    public int OutFrame { get => (int)GetValue(OutFrameProperty); set => SetValue(OutFrameProperty, value); }
    public int DisplayedFrame { get => (int)GetValue(DisplayedFrameProperty); set => SetValue(DisplayedFrameProperty, value); }
    public int LivePosition { get => (int)GetValue(LivePositionProperty); set => SetValue(LivePositionProperty, value); }
    public IReadOnlyList<VideoFrame>? Frames { get => (IReadOnlyList<VideoFrame>?)GetValue(FramesProperty); set => SetValue(FramesProperty, value); }

    private const double SidePadding = 10;
    private const double TrackTop = 24;
    private const double TrackHeight = 6;
    private const double HandleReach = 7;
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x39, 0x4A)));
    private static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xEC, 0xA9, 0x8C)));
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEC, 0xA9, 0x8C)));
    private static readonly Brush PlayheadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xED, 0xF1, 0xF7)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xAA, 0xC1)));
    private static readonly Brush LiveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6F, 0xC3, 0x8E)));
    private static readonly Brush LabelBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x17, 0x22)));
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private enum DragTarget { None, Playhead, In, Out }
    private DragTarget drag;
    private int? pendingValue;
    private double? hoverX;

    public FrameTimeline()
    {
        Focusable = true;
        MinHeight = 48;
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The frame the playhead is drawn at: the pending drag position while dragging, otherwise <see cref="Value"/>.</summary>
    public int PlayheadFrame => pendingValue ?? Value;

    private double TrackLeft => SidePadding;
    private double TrackWidth => Math.Max(1, ActualWidth - 2 * SidePadding);
    private double XOf(int frame) => Maximum <= 0 ? TrackLeft : TrackLeft + TrackWidth * Math.Clamp(frame, 0, Maximum) / Maximum;
    private int FrameAt(double x) => Maximum <= 0 ? 0 : Math.Clamp((int)Math.Round((x - TrackLeft) / TrackWidth * Maximum), 0, Maximum);

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var enabled = IsEnabled && Maximum >= 0 && Frames is { Count: > 0 };
        var opacity = enabled ? 1.0 : 0.4;
        context.PushOpacity(opacity);
        context.DrawRoundedRectangle(TrackBrush, null, new Rect(TrackLeft, TrackTop, TrackWidth, TrackHeight), 3, 3);
        if (enabled)
        {
            var inX = XOf(InFrame);
            var outX = XOf(OutFrame);
            if (outX >= inX)
                context.DrawRectangle(SelectionBrush, null, new Rect(inX, TrackTop - 2, Math.Max(2, outX - inX), TrackHeight + 4));
            DrawHandle(context, inX, true);
            DrawHandle(context, outX, false);
            if (LivePosition >= 0)
                context.DrawRectangle(LiveBrush, null, new Rect(XOf(LivePosition) - 1, TrackTop - 6, 2, TrackHeight + 12));
            if (DisplayedFrame >= 0 && DisplayedFrame != PlayheadFrame)
                context.DrawEllipse(null, new Pen(MutedBrush, 1.5), new Point(XOf(DisplayedFrame), TrackTop + TrackHeight / 2), 5, 5);
            var playX = XOf(PlayheadFrame);
            context.DrawRectangle(PlayheadBrush, null, new Rect(playX - 1, TrackTop - 9, 2, TrackHeight + 18));
            context.DrawEllipse(PlayheadBrush, null, new Point(playX, TrackTop + TrackHeight / 2), 5, 5);
            if (hoverX is { } hover || pendingValue is not null)
            {
                var frame = pendingValue ?? FrameAt(hoverX ?? playX);
                var x = pendingValue is not null ? playX : hoverX ?? playX;
                context.DrawRectangle(MutedBrush, null, new Rect(x - 0.5, TrackTop - 12, 1, TrackHeight + 24));
                DrawLabel(context, x, LabelFor(frame));
            }
        }
        context.Pop();
    }

    private string LabelFor(int frame)
    {
        var time = Frames is { } frames && frame >= 0 && frame < frames.Count ? frames[frame].Time.ToString("0.000", CultureInfo.CurrentCulture) + " s" : "";
        return time.Length == 0 ? $"frame {frame:N0}" : $"frame {frame:N0} · {time}";
    }

    private void DrawHandle(DrawingContext context, double x, bool isIn)
    {
        context.DrawRectangle(AccentBrush, null, new Rect(x - 1, TrackTop - 8, 2, TrackHeight + 16));
        var flag = new StreamGeometry();
        using (var geometry = flag.Open())
        {
            var direction = isIn ? 1 : -1;
            geometry.BeginFigure(new Point(x, TrackTop - 8), true, true);
            geometry.LineTo(new Point(x + direction * 8, TrackTop - 8), false, false);
            geometry.LineTo(new Point(x, TrackTop - 1), false, false);
        }
        flag.Freeze();
        context.DrawGeometry(AccentBrush, null, flag);
    }

    private void DrawLabel(DrawingContext context, double x, string text)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, 11, PlayheadBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var width = formatted.Width + 12;
        var left = Math.Clamp(x - width / 2, 0, Math.Max(0, ActualWidth - width));
        context.DrawRoundedRectangle(LabelBackground, null, new Rect(left, 0, width, 18), 4, 4);
        context.DrawText(formatted, new Point(left + 6, 1));
    }

    private DragTarget HitHandle(double x)
    {
        if (Maximum <= 0) return DragTarget.Playhead;
        var inDistance = Math.Abs(x - XOf(InFrame));
        var outDistance = Math.Abs(x - XOf(OutFrame));
        if (inDistance <= HandleReach && inDistance <= outDistance) return DragTarget.In;
        if (outDistance <= HandleReach) return DragTarget.Out;
        return DragTarget.Playhead;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        Focus();
        if (!IsEnabled || Frames is not { Count: > 0 }) return;
        var position = args.GetPosition(this);
        drag = HitHandle(position.X);
        if (drag == DragTarget.Playhead)
            pendingValue = FrameAt(position.X);
        CaptureMouse();
        InvalidateVisual();
        args.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        var position = args.GetPosition(this);
        hoverX = position.X;
        if (drag != DragTarget.None && args.LeftButton == MouseButtonState.Pressed)
        {
            var frame = FrameAt(position.X);
            switch (drag)
            {
                case DragTarget.In: SetCurrentValue(InFrameProperty, Math.Min(frame, OutFrame)); break;
                case DragTarget.Out: SetCurrentValue(OutFrameProperty, Math.Max(frame, InFrame)); break;
                case DragTarget.Playhead: pendingValue = frame; break;
            }
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        if (drag == DragTarget.Playhead && pendingValue is { } frame)
            SetCurrentValue(ValueProperty, frame);
        drag = DragTarget.None;
        pendingValue = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        drag = DragTarget.None;
        pendingValue = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        hoverX = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (!IsEnabled || Frames is not { Count: > 0 }) return;
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        int? target = args.Key switch
        {
            Key.Left => Value - step,
            Key.Right => Value + step,
            Key.Home => 0,
            Key.End => Maximum,
            _ => null
        };
        if (target is not { } frame) return;
        SetCurrentValue(ValueProperty, Math.Clamp(frame, 0, Maximum));
        args.Handled = true;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
