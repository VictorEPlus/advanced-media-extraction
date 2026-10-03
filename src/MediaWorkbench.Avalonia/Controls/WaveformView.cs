using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia;

public sealed class AudioRangeEventArgs(double start, double end) : EventArgs
{
    public double Start { get; } = start;
    public double End { get; } = end;
}

public sealed class AudioSeekEventArgs(double time) : EventArgs
{
    public double Time { get; } = time;
}

/// <summary>
/// Picture of an audio track with a time ruler, a playhead and a selected section. Drag to select a section, drag a section edge
/// to adjust it, click to move the playhead, double-click to select everything. The control never changes its own selection:
/// it shows a pending section while dragging and raises <see cref="SelectionRequested"/> on release, so the owner can snap the
/// section to video frames. The side padding matches <see cref="FrameTimeline"/> so both line up when stacked. Ported from the WPF app.
/// </summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<Waveform?> WaveformProperty = AvaloniaProperty.Register<WaveformView, Waveform?>(nameof(Waveform));
    public static readonly StyledProperty<double> DurationProperty = AvaloniaProperty.Register<WaveformView, double>(nameof(Duration));
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<WaveformView, double>(nameof(Position), -1.0);
    public static readonly StyledProperty<bool> IsLiveProperty = AvaloniaProperty.Register<WaveformView, bool>(nameof(IsLive));
    public static readonly StyledProperty<double> SelectionStartProperty = AvaloniaProperty.Register<WaveformView, double>(nameof(SelectionStart));
    public static readonly StyledProperty<double> SelectionEndProperty = AvaloniaProperty.Register<WaveformView, double>(nameof(SelectionEnd));
    public static readonly StyledProperty<bool> ShowRulerProperty = AvaloniaProperty.Register<WaveformView, bool>(nameof(ShowRuler), true);
    public static readonly StyledProperty<string> MessageProperty = AvaloniaProperty.Register<WaveformView, string>(nameof(Message), "");

    public Waveform? Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    /// <summary>Seconds shown across the full width.</summary>
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    /// <summary>True while the media is playing; the playhead is drawn in the live colour.</summary>
    public bool IsLive { get => GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
    public double SelectionStart { get => GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public double SelectionEnd { get => GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    /// <summary>The time ruler under the picture. Off when the control sits under a timeline that already has one.</summary>
    public bool ShowRuler { get => GetValue(ShowRulerProperty); set => SetValue(ShowRulerProperty, value); }
    /// <summary>Shown instead of the picture while it is being read, or when there is nothing to draw.</summary>
    public string Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    public event EventHandler<AudioRangeEventArgs>? SelectionRequested;
    public event EventHandler<AudioSeekEventArgs>? SeekRequested;

    private const double SidePadding = 10;
    private const double RulerHeight = 16;
    private const double HandleReach = 6;
    private const double ClickSlop = 4;
    private static readonly double[] TickSteps = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);
    private readonly SolidColorBrush accentBrush = Tokens.Brush("AccentBrush", Color.FromRgb(0x5C, 0xAB, 0xFF));
    private readonly IBrush waveBrush = Tokens.Brush("AudioBrush", Color.FromRgb(0x4F, 0xE0, 0xA0));
    private readonly SolidColorBrush stageBrush = Tokens.Brush("StageBrush", Color.FromRgb(0x05, 0x0A, 0x16));
    private readonly SolidColorBrush mutedBrush = Tokens.Brush("SecondaryBrush", Color.FromRgb(0xB6, 0xC4, 0xDD));
    private readonly IBrush hairlineBrush = Tokens.Brush("HairlineBrush", Color.FromRgb(0x27, 0x3A, 0x5E));
    private readonly IBrush playheadBrush = Tokens.Brush("InkBrush", Color.FromRgb(0xF1, 0xF5, 0xFC));
    private readonly IBrush liveBrush = Tokens.Brush("SuccessBrush", Color.FromRgb(0x4F, 0xE0, 0xA0));
    private readonly IBrush selectionBrush;
    private readonly IBrush outsideBrush;
    private readonly IBrush tickBrush;
    private readonly IBrush labelBackground = Tokens.WithAlpha(Tokens.Brush("RaisedBrush", Color.FromRgb(0x1B, 0x2B, 0x4B)), 0xEB);

    private enum DragTarget { None, New, Start, End }
    private DragTarget drag;
    private double dragOrigin;
    private double dragOriginX;
    private bool dragMoved;
    private (double Start, double End)? pending;
    private double? hoverX;
    private Geometry? outline;
    private Size outlineSize;

    static WaveformView()
    {
        AffectsRender<WaveformView>(WaveformProperty, DurationProperty, PositionProperty, IsLiveProperty, SelectionStartProperty, SelectionEndProperty, ShowRulerProperty, MessageProperty, IsEnabledProperty);
        MinHeightProperty.OverrideDefaultValue<WaveformView>(28);
        ClipToBoundsProperty.OverrideDefaultValue<WaveformView>(true);
    }

    public WaveformView()
    {
        selectionBrush = Tokens.WithAlpha(accentBrush, 0x30);
        outsideBrush = Tokens.WithAlpha(stageBrush, 0xA8);
        tickBrush = Tokens.WithAlpha(mutedBrush, 0x80);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WaveformProperty || change.Property == DurationProperty || change.Property == ShowRulerProperty)
            outline = null;
    }

    private double ActualWidth => Bounds.Width;
    private double ActualHeight => Bounds.Height;
    private bool HasPicture => Waveform is { Count: > 0 } && Duration > 0;
    private double PlotLeft => SidePadding;
    private double PlotWidth => Math.Max(1, ActualWidth - 2 * SidePadding);
    private double PlotHeight => Math.Max(8, ActualHeight - (ShowRuler ? RulerHeight : 0));
    private double XOf(double seconds) => Duration <= 0 ? PlotLeft : PlotLeft + PlotWidth * Math.Clamp(seconds / Duration, 0, 1);
    private double TimeAt(double x) => Duration <= 0 ? 0 : Math.Clamp((x - PlotLeft) / PlotWidth, 0, 1) * Duration;

    /// <summary>"1:02.35" or "12.35 s"; hundredths are enough to place a cut by eye.</summary>
    public static string FormatTime(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var whole = (int)(seconds / 60);
        return whole == 0
            ? seconds.ToString("0.00", CultureInfo.CurrentCulture) + " s"
            : whole >= 60
                ? $"{whole / 60}:{whole % 60:00}:{(seconds - whole * 60).ToString("00.00", CultureInfo.CurrentCulture)}"
                : $"{whole}:{(seconds - whole * 60).ToString("00.00", CultureInfo.CurrentCulture)}";
    }

    public override void Render(DrawingContext context)
    {
        var plot = new Rect(0, 0, Math.Max(0, ActualWidth), PlotHeight);
        context.DrawRectangle(stageBrush, new Pen(hairlineBrush, 1), plot, 6, 6);
        if (!HasPicture)
        {
            if (!string.IsNullOrEmpty(Message))
            {
                var note = Text(Message, 12, mutedBrush);
                context.DrawText(note, new Point(Math.Max(SidePadding, (ActualWidth - note.Width) / 2), Math.Max(0, (PlotHeight - note.Height) / 2)));
            }
            return;
        }
        var middle = PlotHeight / 2;
        context.FillRectangle(tickBrush, new Rect(PlotLeft, Math.Round(middle), PlotWidth, 1));
        context.DrawGeometry(waveBrush, null, Outline());

        var (start, end) = pending ?? (SelectionStart, SelectionEnd);
        var startX = XOf(start);
        var endX = XOf(end);
        var partial = end > start && (start > 0.0005 || end < Duration - 0.0005);
        if (partial)
        {
            if (startX > PlotLeft) context.FillRectangle(outsideBrush, new Rect(PlotLeft, 1, startX - PlotLeft, PlotHeight - 2));
            if (endX < PlotLeft + PlotWidth) context.FillRectangle(outsideBrush, new Rect(endX, 1, PlotLeft + PlotWidth - endX, PlotHeight - 2));
            context.FillRectangle(selectionBrush, new Rect(startX, 1, Math.Max(1, endX - startX), PlotHeight - 2));
        }
        if (end > start)
        {
            DrawEdge(context, startX, true);
            DrawEdge(context, endX, false);
        }
        if (ShowRuler)
            DrawRuler(context);
        if (Position >= 0)
            context.FillRectangle(IsLive ? liveBrush : playheadBrush, new Rect(XOf(Position) - 1, 0, 2, PlotHeight));
        if (pending is { } section)
            DrawLabel(context, (XOf(section.Start) + XOf(section.End)) / 2, $"{FormatTime(section.Start)} to {FormatTime(section.End)} ({FormatTime(section.End - section.Start)})");
        else if (hoverX is { } hover && IsEnabled)
        {
            context.FillRectangle(mutedBrush, new Rect(Math.Clamp(hover, PlotLeft, PlotLeft + PlotWidth) - 0.5, 0, 1, PlotHeight));
            DrawLabel(context, hover, FormatTime(TimeAt(hover)));
        }
    }

    /// <summary>One closed shape: the loudest point of every pixel column left to right, then the quietest right to left.</summary>
    private Geometry Outline()
    {
        var size = new Size(PlotWidth, PlotHeight);
        if (outline is not null && outlineSize == size)
            return outline;
        var waveform = Waveform!;
        var columns = Math.Max(1, (int)Math.Ceiling(PlotWidth));
        var high = new double[columns];
        var low = new double[columns];
        var peak = 0.0;
        for (var column = 0; column < columns; column++)
        {
            var first = (int)(column / (double)columns * Duration / waveform.BucketSeconds);
            var last = (int)((column + 1) / (double)columns * Duration / waveform.BucketSeconds);
            var top = 0f;
            var bottom = 0f;
            for (var bucket = Math.Max(0, first); bucket <= last && bucket < waveform.Count; bucket++)
            {
                if (waveform.Maximum[bucket] > top) top = waveform.Maximum[bucket];
                if (waveform.Minimum[bucket] < bottom) bottom = waveform.Minimum[bucket];
            }
            high[column] = top;
            low[column] = bottom;
            peak = Math.Max(peak, Math.Max(top, -bottom));
        }
        // Quiet recordings are drawn taller so their shape is readable; the gain is capped so silence stays flat.
        var gain = peak <= 0 ? 1 : Math.Min(8, 0.92 / peak);
        var middle = PlotHeight / 2;
        var reach = Math.Max(1, middle - 3);
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(new Point(PlotLeft, middle - 0.5), true);
            for (var column = 0; column < columns; column++)
                figure.LineTo(new Point(PlotLeft + column + 0.5, middle - Math.Max(0.5, Math.Min(1, high[column] * gain) * reach)));
            for (var column = columns - 1; column >= 0; column--)
                figure.LineTo(new Point(PlotLeft + column + 0.5, middle + Math.Max(0.5, Math.Min(1, -low[column] * gain) * reach)));
            figure.EndFigure(true);
        }
        outline = geometry;
        outlineSize = size;
        return geometry;
    }

    private void DrawEdge(DrawingContext context, double x, bool isStart)
    {
        context.FillRectangle(accentBrush, new Rect(x - 1, 0, 2, PlotHeight));
        var direction = isStart ? 1 : -1;
        var flag = new StreamGeometry();
        using (var figure = flag.Open())
        {
            figure.BeginFigure(new Point(x, 0), true);
            figure.LineTo(new Point(x + direction * 8, 0));
            figure.LineTo(new Point(x, 8));
            figure.EndFigure(true);
        }
        context.DrawGeometry(accentBrush, null, flag);
    }

    private void DrawRuler(DrawingContext context)
    {
        var pixelsPerSecond = PlotWidth / Duration;
        var step = TickSteps.FirstOrDefault(candidate => candidate * pixelsPerSecond >= 64, TickSteps[^1]);
        while (step * pixelsPerSecond < 64) step *= 2;
        var top = PlotHeight + 1;
        var lastLabelEnd = double.MinValue;
        for (var index = 0; index * step <= Duration + 0.0001; index++)
        {
            var seconds = index * step;
            var x = Math.Round(XOf(seconds)) + 0.5;
            context.FillRectangle(mutedBrush, new Rect(x - 0.5, top, 1, 4));
            var label = Text(FormatTick(seconds, step), 10, mutedBrush);
            if (x + 3 > lastLabelEnd && x + 3 + label.Width <= ActualWidth)
            {
                context.DrawText(label, new Point(x + 3, top));
                lastLabelEnd = x + 3 + label.Width + 8;
            }
        }
    }

    private static string FormatTick(double seconds, double step)
    {
        var decimals = step < 0.1 ? "00.00" : step < 1 ? "00.0" : "00";
        var minutes = (int)(seconds / 60);
        var rest = (seconds - minutes * 60).ToString(decimals, CultureInfo.CurrentCulture);
        return minutes >= 60 ? $"{minutes / 60}:{minutes % 60:00}:{rest}" : $"{minutes}:{rest}";
    }

    private void DrawLabel(DrawingContext context, double x, string text)
    {
        var formatted = Text(text, 11, playheadBrush);
        var width = formatted.Width + 12;
        var left = Math.Clamp(x - width / 2, 0, Math.Max(0, ActualWidth - width));
        context.DrawRectangle(labelBackground, null, new Rect(left, 2, width, 18), 5, 5);
        context.DrawText(formatted, new Point(left + 6, 4));
    }

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Tokens.Display, size, brush);

    protected override void OnPointerPressed(PointerPressedEventArgs args)
    {
        base.OnPointerPressed(args);
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsEnabled || !HasPicture) return;
        var x = args.GetPosition(this).X;
        if (args.ClickCount == 2)
        {
            drag = DragTarget.None;
            pending = null;
            SelectionRequested?.Invoke(this, new AudioRangeEventArgs(0, Duration));
            args.Handled = true;
            return;
        }
        var hasSection = SelectionEnd > SelectionStart;
        var startDistance = Math.Abs(x - XOf(SelectionStart));
        var endDistance = Math.Abs(x - XOf(SelectionEnd));
        drag = hasSection && startDistance <= HandleReach && startDistance <= endDistance ? DragTarget.Start
            : hasSection && endDistance <= HandleReach ? DragTarget.End
            : DragTarget.New;
        dragOrigin = drag switch { DragTarget.Start => SelectionEnd, DragTarget.End => SelectionStart, _ => TimeAt(x) };
        dragOriginX = x;
        dragMoved = drag != DragTarget.New;
        args.Pointer.Capture(this);
        args.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs args)
    {
        base.OnPointerMoved(args);
        var x = args.GetPosition(this).X;
        hoverX = x;
        if (drag != DragTarget.None && args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            dragMoved |= Math.Abs(x - dragOriginX) >= ClickSlop;
            if (dragMoved)
            {
                var time = TimeAt(x);
                pending = (Math.Min(time, dragOrigin), Math.Max(time, dragOrigin));
            }
        }
        if (IsEnabled && HasPicture)
            Cursor = drag is DragTarget.Start or DragTarget.End
                || SelectionEnd > SelectionStart && (Math.Abs(x - XOf(SelectionStart)) <= HandleReach || Math.Abs(x - XOf(SelectionEnd)) <= HandleReach)
                ? ResizeCursor : TextCursor;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs args)
    {
        base.OnPointerReleased(args);
        if (drag == DragTarget.None) return;
        var section = pending;
        var wasClick = drag == DragTarget.New && !dragMoved;
        var clickTime = TimeAt(args.GetPosition(this).X);
        drag = DragTarget.None;
        pending = null;
        args.Pointer.Capture(null);
        if (wasClick)
            SeekRequested?.Invoke(this, new AudioSeekEventArgs(clickTime));
        else if (section is { } range && range.End > range.Start)
            SelectionRequested?.Invoke(this, new AudioRangeEventArgs(range.Start, range.End));
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args)
    {
        base.OnPointerCaptureLost(args);
        drag = DragTarget.None;
        pending = null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs args)
    {
        base.OnPointerExited(args);
        hoverX = null;
        InvalidateVisual();
    }
}
