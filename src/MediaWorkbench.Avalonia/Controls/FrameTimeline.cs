using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia;

/// <summary>
/// Frame-ordinal timeline with a playhead, in/out handles, a shaded selection and hover readout.
/// Dragging the playhead shows a pending position and commits <see cref="Value"/> on release so every
/// intermediate frame is not decoded. Keyboard: Left/Right step one frame, Shift steps ten, Home/End jump. Ported from the WPF app.
/// </summary>
public sealed class FrameTimeline : Control
{
    public static readonly StyledProperty<int> MaximumProperty = AvaloniaProperty.Register<FrameTimeline, int>(nameof(Maximum));
    public static readonly StyledProperty<int> ValueProperty = AvaloniaProperty.Register<FrameTimeline, int>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<int> InFrameProperty = AvaloniaProperty.Register<FrameTimeline, int>(nameof(InFrame), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<int> OutFrameProperty = AvaloniaProperty.Register<FrameTimeline, int>(nameof(OutFrame), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<int> DisplayedFrameProperty = AvaloniaProperty.Register<FrameTimeline, int>(nameof(DisplayedFrame), -1);
    public static readonly StyledProperty<int> LivePositionProperty = AvaloniaProperty.Register<FrameTimeline, int>(nameof(LivePosition), -1);
    public static readonly StyledProperty<IReadOnlyList<VideoFrame>?> FramesProperty = AvaloniaProperty.Register<FrameTimeline, IReadOnlyList<VideoFrame>?>(nameof(Frames));

    public int Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int InFrame { get => GetValue(InFrameProperty); set => SetValue(InFrameProperty, value); }
    public int OutFrame { get => GetValue(OutFrameProperty); set => SetValue(OutFrameProperty, value); }
    public int DisplayedFrame { get => GetValue(DisplayedFrameProperty); set => SetValue(DisplayedFrameProperty, value); }
    public int LivePosition { get => GetValue(LivePositionProperty); set => SetValue(LivePositionProperty, value); }
    public IReadOnlyList<VideoFrame>? Frames { get => GetValue(FramesProperty); set => SetValue(FramesProperty, value); }

    private const double SidePadding = 10;
    private const double TrackTop = 24;
    private const double TrackHeight = 6;
    private const double HandleReach = 7;
    private const double RulerTop = TrackTop + TrackHeight + 4;
    private static readonly int[] TickSteps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000];
    // Colours come from the Theme.axaml tokens so the timeline follows the palette.
    private readonly SolidColorBrush accentBrush = Tokens.Brush("AccentBrush", Color.FromRgb(0xF2, 0xA5, 0x41));
    private readonly IBrush trackBrush = Tokens.Brush("HairlineBrush", Color.FromRgb(0x2A, 0x2E, 0x36));
    private readonly IBrush selectionBrush;
    private readonly IBrush playheadBrush = Tokens.Brush("InkBrush", Color.FromRgb(0xEC, 0xED, 0xEF));
    private readonly SolidColorBrush mutedBrush = Tokens.Brush("SecondaryBrush", Color.FromRgb(0x9A, 0xA0, 0xAA));
    private readonly IBrush tickBrush;
    private readonly IBrush liveBrush = Tokens.Brush("SuccessBrush", Color.FromRgb(0x5F, 0xD3, 0xA6));
    private readonly IBrush labelBackground = Tokens.WithAlpha(Tokens.Brush("RaisedBrush", Color.FromRgb(0x1D, 0x20, 0x27)), 0xEB);
    private static readonly Typeface LabelTypeface = Tokens.Display;

    private enum DragTarget { None, Playhead, In, Out }
    private DragTarget drag;
    private int? pendingValue;
    private double? hoverX;

    static FrameTimeline()
    {
        AffectsRender<FrameTimeline>(MaximumProperty, ValueProperty, InFrameProperty, OutFrameProperty, DisplayedFrameProperty, LivePositionProperty, FramesProperty, IsEnabledProperty);
        FocusableProperty.OverrideDefaultValue<FrameTimeline>(true);
        MinHeightProperty.OverrideDefaultValue<FrameTimeline>(52);
    }

    public FrameTimeline()
    {
        selectionBrush = Tokens.WithAlpha(accentBrush, 0x55);
        tickBrush = Tokens.WithAlpha(mutedBrush, 0x80);
    }

    /// <summary>The frame the playhead is drawn at: the pending drag position while dragging, otherwise <see cref="Value"/>.</summary>
    public int PlayheadFrame => pendingValue ?? Value;

    private double ActualWidth => Bounds.Width;
    private double TrackLeft => SidePadding;
    private double TrackWidth => Math.Max(1, ActualWidth - 2 * SidePadding);
    private double XOf(int frame) => Maximum <= 0 ? TrackLeft : TrackLeft + TrackWidth * Math.Clamp(frame, 0, Maximum) / Maximum;
    private int FrameAt(double x) => Maximum <= 0 ? 0 : Math.Clamp((int)Math.Round((x - TrackLeft) / TrackWidth * Maximum), 0, Maximum);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var enabled = IsEnabled && Maximum >= 0 && Frames is { Count: > 0 };
        using var dimmed = context.PushOpacity(enabled ? 1.0 : 0.4);
        context.DrawRectangle(trackBrush, null, new Rect(TrackLeft, TrackTop, TrackWidth, TrackHeight), 3, 3);
        if (!enabled) return;
        DrawRuler(context);
        var inX = XOf(InFrame);
        var outX = XOf(OutFrame);
        if (outX >= inX)
            context.FillRectangle(selectionBrush, new Rect(inX, TrackTop - 2, Math.Max(2, outX - inX), TrackHeight + 4));
        DrawHandle(context, inX, true);
        DrawHandle(context, outX, false);
        if (LivePosition >= 0)
            context.FillRectangle(liveBrush, new Rect(XOf(LivePosition) - 1, TrackTop - 6, 2, TrackHeight + 12));
        if (DisplayedFrame >= 0 && DisplayedFrame != PlayheadFrame)
            context.DrawEllipse(null, new Pen(mutedBrush, 1.5), new Point(XOf(DisplayedFrame), TrackTop + TrackHeight / 2), 5, 5);
        var playX = XOf(PlayheadFrame);
        context.FillRectangle(playheadBrush, new Rect(playX - 1, TrackTop - 9, 2, TrackHeight + 18));
        context.DrawEllipse(playheadBrush, null, new Point(playX, TrackTop + TrackHeight / 2), 5, 5);
        if (hoverX is not null || pendingValue is not null)
        {
            var frame = pendingValue ?? FrameAt(hoverX ?? playX);
            var x = pendingValue is not null ? playX : hoverX ?? playX;
            context.FillRectangle(mutedBrush, new Rect(x - 0.5, TrackTop - 12, 1, TrackHeight + 24));
            DrawLabel(context, x, LabelFor(frame));
        }
    }

    /// <summary>A frame ruler under the track: minor ticks at the smallest step that keeps them apart, numbered major ticks every fifth step.</summary>
    private void DrawRuler(DrawingContext context)
    {
        if (Maximum <= 0) return;
        var pixelsPerFrame = TrackWidth / Maximum;
        var minor = TickSteps.FirstOrDefault(step => step * pixelsPerFrame >= 6, TickSteps[^1]);
        var major = minor * 5;
        var labelEvery = major;
        while (labelEvery * pixelsPerFrame < 48) labelEvery *= 2;
        for (var frame = 0; frame <= Maximum; frame += minor)
        {
            var isMajor = frame % major == 0;
            var x = Math.Round(XOf(frame)) + 0.5;
            context.FillRectangle(isMajor ? mutedBrush : tickBrush, new Rect(x - 0.5, RulerTop, 1, isMajor ? 7 : 4));
            if (isMajor && frame % labelEvery == 0 && frame != Maximum)
                context.DrawText(Text(frame.ToString("N0", CultureInfo.CurrentCulture), 10, mutedBrush), new Point(x + 3, RulerTop + 7));
        }
        var end = Text(Maximum.ToString("N0", CultureInfo.CurrentCulture), 10, mutedBrush);
        context.DrawText(end, new Point(XOf(Maximum) - end.Width, RulerTop + 7));
    }

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, size, brush);

    private string LabelFor(int frame)
    {
        var time = Frames is { } frames && frame >= 0 && frame < frames.Count ? frames[frame].Time.ToString("0.000", CultureInfo.CurrentCulture) + " s" : "";
        return time.Length == 0 ? $"frame {frame:N0}" : $"frame {frame:N0} · {time}";
    }

    private void DrawHandle(DrawingContext context, double x, bool isIn)
    {
        context.FillRectangle(accentBrush, new Rect(x - 1, TrackTop - 8, 2, TrackHeight + 16));
        var flag = new StreamGeometry();
        using (var geometry = flag.Open())
        {
            var direction = isIn ? 1 : -1;
            geometry.BeginFigure(new Point(x, TrackTop - 8), true);
            geometry.LineTo(new Point(x + direction * 8, TrackTop - 8));
            geometry.LineTo(new Point(x, TrackTop - 1));
            geometry.EndFigure(true);
        }
        context.DrawGeometry(accentBrush, null, flag);
    }

    private void DrawLabel(DrawingContext context, double x, string text)
    {
        var formatted = Text(text, 11, playheadBrush);
        var width = formatted.Width + 12;
        var left = Math.Clamp(x - width / 2, 0, Math.Max(0, ActualWidth - width));
        context.DrawRectangle(labelBackground, null, new Rect(left, 0, width, 18), 5, 5);
        context.DrawText(formatted, new Point(left + 6, 2));
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

    protected override void OnPointerPressed(PointerPressedEventArgs args)
    {
        base.OnPointerPressed(args);
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (!IsEnabled || Frames is not { Count: > 0 }) return;
        var position = args.GetPosition(this);
        drag = HitHandle(position.X);
        if (drag == DragTarget.Playhead)
            pendingValue = FrameAt(position.X);
        args.Pointer.Capture(this);
        InvalidateVisual();
        args.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs args)
    {
        base.OnPointerMoved(args);
        var position = args.GetPosition(this);
        hoverX = position.X;
        if (drag != DragTarget.None && args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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

    protected override void OnPointerReleased(PointerReleasedEventArgs args)
    {
        base.OnPointerReleased(args);
        if (drag == DragTarget.Playhead && pendingValue is { } frame)
            SetCurrentValue(ValueProperty, frame);
        drag = DragTarget.None;
        pendingValue = null;
        args.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args)
    {
        base.OnPointerCaptureLost(args);
        drag = DragTarget.None;
        pendingValue = null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs args)
    {
        base.OnPointerExited(args);
        hoverX = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (!IsEnabled || Frames is not { Count: > 0 }) return;
        var step = (args.KeyModifiers & KeyModifiers.Shift) != 0 ? 10 : 1;
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
}
