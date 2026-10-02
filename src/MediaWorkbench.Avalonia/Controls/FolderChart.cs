using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia;

/// <summary>
/// Horizontal stacked bars: one row per folder, length by file count, split into photos, videos and audio.
/// A legend is always drawn, counts are labelled in text colours (never the series colour), segments are separated
/// by a 2 px surface gap, and each row has a hover state with a tooltip. Clicking or pressing Enter selects the folder. Ported from the WPF app.
/// </summary>
public sealed class FolderChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<FolderNode>?> NodesProperty = AvaloniaProperty.Register<FolderChart, IReadOnlyList<FolderNode>?>(nameof(Nodes));
    public static readonly StyledProperty<string?> SelectedPathProperty = AvaloniaProperty.Register<FolderChart, string?>(nameof(SelectedPath), defaultBindingMode: BindingMode.TwoWay);
    /// <summary>Path of the folder whose contents are charted; rows with this same path (direct files, folded folders) are not clickable.</summary>
    public static readonly StyledProperty<string> CurrentPathProperty = AvaloniaProperty.Register<FolderChart, string>(nameof(CurrentPath), "");

    public IReadOnlyList<FolderNode>? Nodes { get => GetValue(NodesProperty); set => SetValue(NodesProperty, value); }
    public string? SelectedPath { get => GetValue(SelectedPathProperty); set => SetValue(SelectedPathProperty, value); }
    public string CurrentPath { get => GetValue(CurrentPathProperty); set => SetValue(CurrentPathProperty, value); }

    private const double LegendHeight = 30;
    private const double RowHeight = 30;
    private const double BarHeight = 14;
    private const double SegmentGap = 2;
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private readonly IBrush photoBrush = Tokens.Brush("PhotoBrush", Color.FromRgb(0x9B, 0x8C, 0xFF));
    private readonly IBrush videoBrush = Tokens.Brush("VideoBrush", Color.FromRgb(0x7A, 0xA7, 0xFF));
    private readonly IBrush audioBrush = Tokens.Brush("AudioBrush", Color.FromRgb(0x5F, 0xD3, 0xA6));
    private readonly IBrush inkBrush = Tokens.Brush("InkBrush", Color.FromRgb(0xEC, 0xED, 0xEF));
    private readonly IBrush mutedBrush = Tokens.Brush("SecondaryBrush", Color.FromRgb(0x9A, 0xA0, 0xAA));
    private readonly IBrush hoverBrush = Tokens.Brush("HoverBrush", Color.FromRgb(0x24, 0x28, 0x32));
    private readonly IBrush trackBrush = Tokens.Brush("SurfaceBrush", Color.FromRgb(0x16, 0x18, 0x1D));
    private readonly IBrush focusBrush = Tokens.Brush("AccentBrush", Color.FromRgb(0xF2, 0xA5, 0x41));
    private int hoverRow = -1;

    static FolderChart()
    {
        AffectsRender<FolderChart>(NodesProperty, CurrentPathProperty);
        AffectsMeasure<FolderChart>(NodesProperty);
        FocusableProperty.OverrideDefaultValue<FolderChart>(true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NodesProperty) hoverRow = -1;
    }

    protected override void OnGotFocus(FocusChangedEventArgs args)
    {
        base.OnGotFocus(args);
        if (hoverRow < 0 && Nodes is { Count: > 0 }) hoverRow = 0;
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs args)
    {
        base.OnLostFocus(args);
        InvalidateVisual();
    }

    private double ActualWidth => Bounds.Width;
    private double LabelWidth => Math.Clamp(ActualWidth * 0.34, 90, 240);
    private static double CountWidth => 112;
    private bool IsClickable(FolderNode node) => !node.Path.Equals(CurrentPath ?? "", StringComparison.OrdinalIgnoreCase);

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Math.Max(1, Nodes?.Count ?? 0);
        var width = double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width;
        return new Size(width, LegendHeight + rows * RowHeight + 6);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        DrawLegend(context);
        if (Nodes is not { Count: > 0 } nodes)
        {
            context.DrawText(Text("No media found in this folder yet.", 12, mutedBrush, ActualWidth), new Point(0, LegendHeight + 6));
            return;
        }
        var maximum = Math.Max(1, nodes.Max(node => node.Total));
        var total = nodes.Sum(node => (long)node.Total);
        var barLeft = LabelWidth + 10;
        var barWidth = Math.Max(20, ActualWidth - barLeft - CountWidth);
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var top = LegendHeight + index * RowHeight;
            if (index == hoverRow)
                context.DrawRectangle(hoverBrush, IsFocused ? new Pen(focusBrush, 1) : null, new Rect(0, top + 1, ActualWidth, RowHeight - 2), 6, 6);
            var label = Text(node.Name, 12, IsClickable(node) ? inkBrush : mutedBrush, LabelWidth - 8);
            context.DrawText(label, new Point(6, top + (RowHeight - label.Height) / 2));
            var barTop = top + (RowHeight - BarHeight) / 2;
            context.DrawRectangle(trackBrush, null, new Rect(barLeft, barTop, barWidth, BarHeight), 4, 4);
            var length = Math.Max(4, barWidth * node.Total / maximum);
            // Clip to a rectangle rounded only by the outer radius so the data end is rounded and the baseline end stays square-anchored.
            using (context.PushClip(new RoundedRect(new Rect(barLeft - 4, barTop, length + 4, BarHeight), 4)))
            {
                var x = barLeft;
                foreach (var (count, brush) in new[] { (node.Photos, photoBrush), (node.Videos, videoBrush), (node.Audio, audioBrush) })
                {
                    if (count <= 0) continue;
                    var width = length * count / node.Total;
                    context.FillRectangle(brush, new Rect(x, barTop, Math.Max(1, width - SegmentGap), BarHeight));
                    x += width;
                }
            }
            // Count then share of the charted folder, both in text colours: identity comes from the legend, not from coloured numbers.
            var countText = Text(node.Total.ToString("N0", CultureInfo.CurrentCulture), 12, inkBrush, 56);
            context.DrawText(countText, new Point(barLeft + barWidth + 8 + (48 - Math.Min(48, countText.Width)), top + (RowHeight - countText.Height) / 2));
            var shareText = Text(MainViewModel.Percent(node.Total, total), 12, mutedBrush, 44);
            context.DrawText(shareText, new Point(barLeft + barWidth + 64 + (40 - Math.Min(40, shareText.Width)), top + (RowHeight - shareText.Height) / 2));
        }
    }

    private void DrawLegend(DrawingContext context)
    {
        double x = 6;
        foreach (var (name, brush) in new[] { ("Photos", photoBrush), ("Videos", videoBrush), ("Audio", audioBrush) })
        {
            context.DrawRectangle(brush, null, new Rect(x, 9, 12, 12), 3, 3);
            var text = Text(name, 11, mutedBrush, 120);
            context.DrawText(text, new Point(x + 17, 15 - text.Height / 2));
            x += 17 + text.Width + 18;
        }
    }

    private static FormattedText Text(string value, double size, IBrush brush, double maxWidth) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Tokens.Text, size, brush)
        {
            MaxTextWidth = Math.Max(10, maxWidth), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis
        };

    private int RowAt(Point point)
    {
        if (Nodes is not { Count: > 0 } nodes || point.Y < LegendHeight) return -1;
        var row = (int)((point.Y - LegendHeight) / RowHeight);
        return row >= 0 && row < nodes.Count ? row : -1;
    }

    private void SetHover(int row)
    {
        if (row == hoverRow) return;
        hoverRow = row;
        if (row >= 0 && Nodes is { } nodes)
        {
            var node = nodes[row];
            var all = nodes.Sum(item => (long)item.Total);
            ToolTip.SetTip(this, $"{node.Name}\n{node.Total:N0} files, {MainViewModel.Percent(node.Total, all)} of this folder\n{MainViewModel.DescribeKinds(node.Photos, node.Videos, node.Audio, percentages: true)}\n{MainViewModel.DescribeSize(node.Bytes)}" + (IsClickable(node) ? "\nClick to open this folder" : ""));
            Cursor = IsClickable(node) ? HandCursor : null;
        }
        else
        {
            ToolTip.SetTip(this, null);
            Cursor = null;
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs args)
    {
        base.OnPointerMoved(args);
        SetHover(RowAt(args.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs args)
    {
        base.OnPointerExited(args);
        if (!IsFocused) SetHover(-1);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs args)
    {
        base.OnPointerReleased(args);
        Focus();
        Activate(RowAt(args.GetPosition(this)));
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (Nodes is not { Count: > 0 } nodes) return;
        switch (args.Key)
        {
            case Key.Down: SetHover(Math.Min(nodes.Count - 1, hoverRow + 1)); args.Handled = true; break;
            case Key.Up: SetHover(Math.Max(0, hoverRow - 1)); args.Handled = true; break;
            case Key.Enter or Key.Space: Activate(hoverRow); args.Handled = true; break;
        }
    }

    private void Activate(int row)
    {
        if (row < 0 || Nodes is not { } nodes || row >= nodes.Count || !IsClickable(nodes[row])) return;
        SetCurrentValue(SelectedPathProperty, nodes[row].Path);
    }
}
