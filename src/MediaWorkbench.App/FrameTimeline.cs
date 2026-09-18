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
    private const double RulerTop = TrackTop + TrackHeight + 4;
    private static readonly int[] TickSteps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000];
    // Colours come from the App.xaml tokens so the timeline follows the palette.
    private static readonly SolidColorBrush AccentBrush = Tokens.Brush("AccentBrush", Color.FromRgb(0xEB, 0xBC, 0x45));
    private static readonly Brush TrackBrush = Tokens.Brush("BorderBrush", Color.FromRgb(0x56, 0x50, 0x43));
    private static readonly Brush SelectionBrush = Tokens.WithAlpha(AccentBrush, 0x55);
    private static readonly Brush PlayheadBrush = Tokens.Brush("InkBrush", Color.FromRgb(0xF3, 0xEE, 0xE3));
    private static readonly SolidColorBrush MutedBrush = Tokens.Brush("MutedBrush", Color.FromRgb(0xB9, 0xB1, 0x9E));
    private static readonly Brush TickBrush = Tokens.WithAlpha(MutedBrush, 0x80);
    private static readonly Brush LiveBrush = Tokens.Brush("SuccessBrush", Color.FromRgb(0x7C, 0xB8, 0x6A));
    private static readonly Brush LabelBackground = Tokens.WithAlpha(Tokens.Brush("PanelBrush", Color.FromRgb(0x2E, 0x2B, 0x25)), 0xEB);
    private static readonly Typeface LabelTypeface = Tokens.Display;

    private enum DragTarget { None, Playhead, In, Out }
    private DragTarget drag;
    private int? pendingValue;
    private double? hoverX;

    public FrameTimeline()
    {
        Focusable = true;
        MinHeight = 52;
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
            DrawRuler(context);
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

    /// <summary>A frame ruler under the track: minor ticks at the smallest step that keeps them apart, numbered major ticks every fifth step.</summary>
    private void DrawRuler(DrawingContext context)
    {
        if (Maximum <= 0) return;
        var pixelsPerFrame = TrackWidth / Maximum;
        var minor = TickSteps.FirstOrDefault(step => step * pixelsPerFrame >= 6, TickSteps[^1]);
        var major = minor * 5;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelEvery = major;
        while (labelEvery * pixelsPerFrame < 48) labelEvery *= 2;
        for (var frame = 0; frame <= Maximum; frame += minor)
        {
            var isMajor = frame % major == 0;
            var x = Math.Round(XOf(frame)) + 0.5;
            context.DrawRectangle(isMajor ? MutedBrush : TickBrush, null, new Rect(x - 0.5, RulerTop, 1, isMajor ? 7 : 4));
            if (isMajor && frame % labelEvery == 0 && frame != Maximum)
            {
                var text = new FormattedText(frame.ToString("N0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, 10, MutedBrush, pixelsPerDip);
                context.DrawText(text, new Point(x + 3, RulerTop + 7));
            }
        }
        var end = new FormattedText(Maximum.ToString("N0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, 10, MutedBrush, pixelsPerDip);
        context.DrawText(end, new Point(XOf(Maximum) - end.Width, RulerTop + 7));
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
