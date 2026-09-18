using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

/// <summary>
/// Horizontal stacked bars: one row per folder, length by file count, split into photos, videos and audio.
/// A legend is always drawn, counts are labelled in text colours (never the series colour), segments are separated
/// by a 2 px surface gap, and each row has a hover state with a tooltip. Clicking or pressing Enter selects the folder.
/// </summary>
public sealed class FolderChart : FrameworkElement
{
    public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(nameof(Nodes), typeof(IReadOnlyList<FolderNode>), typeof(FolderChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, (sender, _) => ((FolderChart)sender).hoverRow = -1));
    public static readonly DependencyProperty SelectedPathProperty = DependencyProperty.Register(nameof(SelectedPath), typeof(string), typeof(FolderChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    /// <summary>Path of the folder whose contents are charted; rows with this same path (direct files, folded folders) are not clickable.</summary>
    public static readonly DependencyProperty CurrentPathProperty = DependencyProperty.Register(nameof(CurrentPath), typeof(string), typeof(FolderChart), new FrameworkPropertyMetadata(""));

    public IReadOnlyList<FolderNode>? Nodes { get => (IReadOnlyList<FolderNode>?)GetValue(NodesProperty); set => SetValue(NodesProperty, value); }
    public string? SelectedPath { get => (string?)GetValue(SelectedPathProperty); set => SetValue(SelectedPathProperty, value); }
    public string CurrentPath { get => (string)GetValue(CurrentPathProperty); set => SetValue(CurrentPathProperty, value); }

    private const double LegendHeight = 30;
    private const double RowHeight = 30;
    private const double BarHeight = 14;
    private const double SegmentGap = 2;
    private static readonly SolidColorBrush PhotoBrush = Tokens.Brush("ChartPhotoBrush", Color.FromRgb(0x39, 0x87, 0xE5));
    private static readonly SolidColorBrush VideoBrush = Tokens.Brush("ChartVideoBrush", Color.FromRgb(0xD9, 0x59, 0x26));
    private static readonly SolidColorBrush AudioBrush = Tokens.Brush("ChartAudioBrush", Color.FromRgb(0x19, 0x9E, 0x70));
    private static readonly SolidColorBrush InkBrush = Tokens.Brush("InkBrush", Color.FromRgb(0xF3, 0xEE, 0xE3));
    private static readonly SolidColorBrush MutedBrush = Tokens.Brush("MutedBrush", Color.FromRgb(0xBB, 0xB3, 0xA0));
    private static readonly SolidColorBrush HoverBrush = Tokens.Brush("RaisedBrush", Color.FromRgb(0x40, 0x3C, 0x33));
    private static readonly SolidColorBrush TrackBrush = Tokens.WithAlpha(Tokens.Brush("BorderBrush", Color.FromRgb(0x5A, 0x54, 0x47)), 0x55);
    private static readonly SolidColorBrush FocusBrush = Tokens.Brush("AccentBrush", Color.FromRgb(0xEB, 0xBC, 0x45));
    private int hoverRow = -1;

    public FolderChart()
    {
        Focusable = true;
        FocusVisualStyle = null;
        GotKeyboardFocus += (_, _) => { if (hoverRow < 0 && Nodes is { Count: > 0 }) hoverRow = 0; InvalidateVisual(); };
        LostKeyboardFocus += (_, _) => InvalidateVisual();
    }

    private double LabelWidth => Math.Clamp(ActualWidth * 0.34, 90, 240);
    private double CountWidth => 64;
    private bool IsClickable(FolderNode node) => !node.Path.Equals(CurrentPath ?? "", StringComparison.OrdinalIgnoreCase);

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Math.Max(1, Nodes?.Count ?? 0);
        var width = double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width;
        return new Size(width, LegendHeight + rows * RowHeight + 6);
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawLegend(context, dpi);
        if (Nodes is not { Count: > 0 } nodes)
        {
            context.DrawText(Text("No media found in this folder yet.", 12, MutedBrush, dpi, ActualWidth), new Point(0, LegendHeight + 6));
            return;
        }
        var maximum = Math.Max(1, nodes.Max(node => node.Total));
        var barLeft = LabelWidth + 10;
        var barWidth = Math.Max(20, ActualWidth - barLeft - CountWidth);
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var top = LegendHeight + index * RowHeight;
            if (index == hoverRow)
                context.DrawRoundedRectangle(HoverBrush, IsKeyboardFocused ? new Pen(FocusBrush, 1) : null, new Rect(0, top + 1, ActualWidth, RowHeight - 2), 3, 3);
            var label = Text(node.Name, 12, IsClickable(node) ? InkBrush : MutedBrush, dpi, LabelWidth - 8);
            context.DrawText(label, new Point(6, top + (RowHeight - label.Height) / 2));
            var barTop = top + (RowHeight - BarHeight) / 2;
            context.DrawRoundedRectangle(TrackBrush, null, new Rect(barLeft, barTop, barWidth, BarHeight), 3, 3);
            var length = Math.Max(4, barWidth * node.Total / maximum);
            // Clip to a rectangle rounded only by the outer radius so the data end is rounded and the baseline end stays square-anchored.
            context.PushClip(new RectangleGeometry(new Rect(barLeft - 4, barTop, length + 4, BarHeight), 4, 4));
            var x = barLeft;
            foreach (var (count, brush) in new[] { (node.Photos, PhotoBrush), (node.Videos, VideoBrush), (node.Audio, AudioBrush) })
            {
                if (count <= 0) continue;
                var width = length * count / node.Total;
                context.DrawRectangle(brush, null, new Rect(x, barTop, Math.Max(1, width - SegmentGap), BarHeight));
                x += width;
            }
            context.Pop();
            var countText = Text(node.Total.ToString("N0", CultureInfo.CurrentCulture), 12, InkBrush, dpi, CountWidth - 8);
            context.DrawText(countText, new Point(barLeft + barWidth + 8, top + (RowHeight - countText.Height) / 2));
        }
    }

    private void DrawLegend(DrawingContext context, double dpi)
    {
        double x = 6;
        foreach (var (name, brush) in new[] { ("Photos", PhotoBrush), ("Videos", VideoBrush), ("Audio", AudioBrush) })
        {
            context.DrawRoundedRectangle(brush, null, new Rect(x, 9, 12, 12), 2, 2);
            var text = Text(name, 11, MutedBrush, dpi, 120);
            context.DrawText(text, new Point(x + 17, 15 - text.Height / 2));
            x += 17 + text.Width + 18;
        }
    }

    private static FormattedText Text(string value, double size, Brush brush, double dpi, double maxWidth)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Tokens.Text, size, brush, dpi)
        {
            MaxTextWidth = Math.Max(10, maxWidth), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis
        };
        return text;
    }

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
            ToolTip = $"{node.Name}\n{MainViewModel.DescribeKinds(node.Photos, node.Videos, node.Audio)}, {MainViewModel.DescribeSize(node.Bytes)}" + (IsClickable(node) ? "\nClick to open this folder" : "");
            Cursor = IsClickable(node) ? Cursors.Hand : Cursors.Arrow;
        }
        else
        {
            ToolTip = null;
            Cursor = Cursors.Arrow;
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs args) => SetHover(RowAt(args.GetPosition(this)));
    protected override void OnMouseLeave(MouseEventArgs args) { if (!IsKeyboardFocused) SetHover(-1); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        Focus();
        Activate(RowAt(args.GetPosition(this)));
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
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
