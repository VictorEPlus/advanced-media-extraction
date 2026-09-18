using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

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
/// section to video frames. The side padding matches <see cref="FrameTimeline"/> so both line up when stacked.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty WaveformProperty = DependencyProperty.Register(nameof(Waveform), typeof(Waveform), typeof(WaveformView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WaveformView)d).outline = null));
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WaveformView)d).outline = null));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(nameof(IsLive), typeof(bool), typeof(WaveformView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(nameof(SelectionStart), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(nameof(SelectionEnd), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(WaveformView), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public Waveform? Waveform { get => (Waveform?)GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    /// <summary>Seconds shown across the full width.</summary>
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    /// <summary>True while the media is playing; the playhead is drawn in the live colour.</summary>
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
    public double SelectionStart { get => (double)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public double SelectionEnd { get => (double)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    /// <summary>Shown instead of the picture while it is being read, or when there is nothing to draw.</summary>
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    public event EventHandler<AudioRangeEventArgs>? SelectionRequested;
    public event EventHandler<AudioSeekEventArgs>? SeekRequested;

    private const double SidePadding = 10;
    private const double RulerHeight = 16;
    private const double HandleReach = 6;
    private const double ClickSlop = 4;
    private static readonly double[] TickSteps = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
    private static readonly SolidColorBrush AccentBrush = Tokens.Brush("AccentBrush", Color.FromRgb(0xEB, 0xBC, 0x45));
    private static readonly SolidColorBrush WaveBrush = Tokens.Brush("ChartAudioBrush", Color.FromRgb(0x19, 0x9E, 0x70));
    private static readonly SolidColorBrush MonitorBrush = Tokens.Brush("MonitorBrush", Color.FromRgb(0x14, 0x13, 0x10));
    private static readonly SolidColorBrush MutedBrush = Tokens.Brush("MutedBrush", Color.FromRgb(0xBB, 0xB3, 0xA0));
    private static readonly Brush BorderBrush = Tokens.Brush("BorderBrush", Color.FromRgb(0x5A, 0x54, 0x47));
    private static readonly Brush PlayheadBrush = Tokens.Brush("InkBrush", Color.FromRgb(0xF3, 0xEE, 0xE3));
    private static readonly Brush LiveBrush = Tokens.Brush("SuccessBrush", Color.FromRgb(0x7C, 0xB8, 0x6A));
    private static readonly Brush SelectionBrush = Tokens.WithAlpha(AccentBrush, 0x30);
    private static readonly Brush OutsideBrush = Tokens.WithAlpha(MonitorBrush, 0xA8);
    private static readonly Brush TickBrush = Tokens.WithAlpha(MutedBrush, 0x80);
    private static readonly Brush LabelBackground = Tokens.WithAlpha(Tokens.Brush("PanelBrush", Color.FromRgb(0x2E, 0x2B, 0x25)), 0xEB);

    private enum DragTarget { None, New, Start, End }
    private DragTarget drag;
    private double dragOrigin;
    private double dragOriginX;
    private bool dragMoved;
    private (double Start, double End)? pending;
    private double? hoverX;
    private Geometry? outline;
    private Size outlineSize;

    public WaveformView()
    {
        MinHeight = 48;
        ClipToBounds = true;
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    private bool HasPicture => Waveform is { Count: > 0 } && Duration > 0;
    private double PlotLeft => SidePadding;
    private double PlotWidth => Math.Max(1, ActualWidth - 2 * SidePadding);
    private double PlotHeight => Math.Max(8, ActualHeight - RulerHeight);
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

    protected override void OnRender(DrawingContext context)
    {
        var plot = new Rect(0, 0, Math.Max(0, ActualWidth), PlotHeight);
        context.DrawRoundedRectangle(MonitorBrush, new Pen(BorderBrush, 1), plot, 2, 2);
        if (!HasPicture)
        {
            if (!string.IsNullOrEmpty(Message))
            {
                var note = Text(Message, 12, MutedBrush);
                context.DrawText(note, new Point(Math.Max(SidePadding, (ActualWidth - note.Width) / 2), Math.Max(0, (PlotHeight - note.Height) / 2)));
            }
            return;
        }
        var middle = PlotHeight / 2;
        context.DrawRectangle(TickBrush, null, new Rect(PlotLeft, Math.Round(middle), PlotWidth, 1));
        context.DrawGeometry(WaveBrush, null, Outline());

        var (start, end) = pending ?? (SelectionStart, SelectionEnd);
        var startX = XOf(start);
        var endX = XOf(end);
        var partial = end > start && (start > 0.0005 || end < Duration - 0.0005);
        if (partial)
        {
            if (startX > PlotLeft) context.DrawRectangle(OutsideBrush, null, new Rect(PlotLeft, 1, startX - PlotLeft, PlotHeight - 2));
            if (endX < PlotLeft + PlotWidth) context.DrawRectangle(OutsideBrush, null, new Rect(endX, 1, PlotLeft + PlotWidth - endX, PlotHeight - 2));
            context.DrawRectangle(SelectionBrush, null, new Rect(startX, 1, Math.Max(1, endX - startX), PlotHeight - 2));
        }
        if (end > start)
        {
            DrawEdge(context, startX, true);
            DrawEdge(context, endX, false);
        }
        DrawRuler(context);
        if (Position >= 0)
        {
            var x = XOf(Position);
            context.DrawRectangle(IsLive ? LiveBrush : PlayheadBrush, null, new Rect(x - 1, 0, 2, PlotHeight));
        }
        if (pending is { } section)
            DrawLabel(context, (XOf(section.Start) + XOf(section.End)) / 2, $"{FormatTime(section.Start)} to {FormatTime(section.End)} ({FormatTime(section.End - section.Start)})");
        else if (hoverX is { } hover && IsEnabled)
        {
            context.DrawRectangle(MutedBrush, null, new Rect(Math.Clamp(hover, PlotLeft, PlotLeft + PlotWidth) - 0.5, 0, 1, PlotHeight));
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
            figure.BeginFigure(new Point(PlotLeft, middle - 0.5), true, true);
            for (var column = 0; column < columns; column++)
                figure.LineTo(new Point(PlotLeft + column + 0.5, middle - Math.Max(0.5, Math.Min(1, high[column] * gain) * reach)), false, false);
            for (var column = columns - 1; column >= 0; column--)
                figure.LineTo(new Point(PlotLeft + column + 0.5, middle + Math.Max(0.5, Math.Min(1, -low[column] * gain) * reach)), false, false);
        }
        geometry.Freeze();
        outline = geometry;
        outlineSize = size;
        return geometry;
    }

    private void DrawEdge(DrawingContext context, double x, bool isStart)
    {
        context.DrawRectangle(AccentBrush, null, new Rect(x - 1, 0, 2, PlotHeight));
        var direction = isStart ? 1 : -1;
        var flag = new StreamGeometry();
        using (var figure = flag.Open())
        {
            figure.BeginFigure(new Point(x, 0), true, true);
            figure.LineTo(new Point(x + direction * 8, 0), false, false);
            figure.LineTo(new Point(x, 8), false, false);
        }
        flag.Freeze();
        context.DrawGeometry(AccentBrush, null, flag);
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
            context.DrawRectangle(MutedBrush, null, new Rect(x - 0.5, top, 1, 4));
            var label = Text(FormatTick(seconds, step), 10, MutedBrush);
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
        var formatted = Text(text, 11, PlayheadBrush);
        var width = formatted.Width + 12;
        var left = Math.Clamp(x - width / 2, 0, Math.Max(0, ActualWidth - width));
        context.DrawRoundedRectangle(LabelBackground, null, new Rect(left, 2, width, 18), 4, 4);
        context.DrawText(formatted, new Point(left + 6, 3));
    }

    private FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Tokens.Display, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        if (!IsEnabled || !HasPicture) return;
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
        CaptureMouse();
        args.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        var x = args.GetPosition(this).X;
        hoverX = x;
        if (drag != DragTarget.None && args.LeftButton == MouseButtonState.Pressed)
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
                ? Cursors.SizeWE : Cursors.IBeam;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        if (drag == DragTarget.None) return;
        var section = pending;
        var wasClick = drag == DragTarget.New && !dragMoved;
        var clickTime = TimeAt(args.GetPosition(this).X);
        drag = DragTarget.None;
        pending = null;
        ReleaseMouseCapture();
        if (wasClick)
            SeekRequested?.Invoke(this, new AudioSeekEventArgs(clickTime));
        else if (section is { } range && range.End > range.Start)
            SelectionRequested?.Invoke(this, new AudioRangeEventArgs(range.Start, range.End));
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        drag = DragTarget.None;
        pending = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        hoverX = null;
        InvalidateVisual();
    }
}
