using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia;

/// <summary>
/// The tag graph: each tag a glowing node (bigger = more files, in a vivid version of its own colour), and a line between tags used
/// on the same files (thicker = used together more). Click a tag to bring it to the middle with the tags it goes with around it,
/// closer the more they go together; click one of those to go on from there. Drag to move the map, wheel to zoom, double-click a tag
/// to show its files. Everything eases into place, and the tag in focus breathes softly.
/// </summary>
public sealed class TagGraphView : Control
{
    public static readonly StyledProperty<TagGraph?> GraphProperty = AvaloniaProperty.Register<TagGraphView, TagGraph?>(nameof(Graph));
    public static readonly StyledProperty<string?> FocusTagProperty =
        AvaloniaProperty.Register<TagGraphView, string?>(nameof(FocusTag), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    /// <summary>The width covered on the right (by the card beside the map): the map is laid out in the space left of it.</summary>
    public static readonly StyledProperty<double> RightInsetProperty = AvaloniaProperty.Register<TagGraphView, double>(nameof(RightInset));

    /// <summary>The overview shows the most used tags; the one in focus and those around it are always shown.</summary>
    internal const int OverviewLimit = 150;
    private const int RingLimit = 28;

    private static readonly Color Ink = Color.Parse("#EEF2FF");
    private static readonly Typeface LabelFace = new(new FontFamily("fonts:Inter#Inter, Segoe UI"), FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IBrush Background = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#1C1650"), 0), new GradientStop(Color.Parse("#0E1838"), 0.55), new GradientStop(Color.Parse("#061A2C"), 1) }
    };
    private static readonly IBrush VioletGlow = Glow(Color.Parse("#7C4DFF"), 0x55);
    private static readonly IBrush CyanGlow = Glow(Color.Parse("#00D1FF"), 0x33);
    private static readonly IBrush LabelShadow = new SolidColorBrush(Color.FromArgb(0xB0, 4, 6, 20));

    private sealed class Node(string tag, int files)
    {
        public string Tag { get; } = tag;
        public int Files { get; set; } = files;
        public Color Color { get; } = TagColors.Vibrant(tag);
        public Color Light { get; } = TagColors.Vibrant(tag, 0.82);
        public double X, Y, TargetX, TargetY;
        public double Opacity, TargetOpacity = 1;
    }

    private readonly Dictionary<string, Node> nodes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (double X, double Y)> overview = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer timer;
    private double zoom = 1;
    private Vector pan;
    private double time;
    private string? hovered;
    private string? pressedTag;
    /// <summary>The tag the last click landed on: a double-click acts on it, though the first click has started it gliding to the middle.</summary>
    private string? lastClickedTag;
    private Point? pressPoint;
    private Point lastPoint;
    private bool dragged;
    private int maxFiles = 1;

    static TagGraphView()
    {
        AffectsRender<TagGraphView>(GraphProperty, FocusTagProperty, RightInsetProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TagGraphView>(true);
        FocusableProperty.OverrideDefaultValue<TagGraphView>(true);
    }

    public TagGraphView()
    {
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
    }

    public TagGraph? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public string? FocusTag
    {
        get => GetValue(FocusTagProperty);
        set => SetValue(FocusTagProperty, value);
    }

    public double RightInset
    {
        get => GetValue(RightInsetProperty);
        set => SetValue(RightInsetProperty, value);
    }

    /// <summary>Double-click on a tag: show its files.</summary>
    public event EventHandler<string>? TagActivated;

    /// <summary>For checks: the tags on the map now.</summary>
    internal IReadOnlyCollection<string> ShownTags => nodes.Keys;

    /// <summary>For checks: where a tag is on screen (once the motion has settled), or null when it is not shown.</summary>
    internal Point? ScreenOf(string tag) => nodes.TryGetValue(tag, out var node) ? ToScreen(node.TargetX, node.TargetY) : null;

    /// <summary>For checks: jumps to the end of the motion.</summary>
    internal void Settle()
    {
        foreach (var node in nodes.Values)
        {
            node.X = node.TargetX;
            node.Y = node.TargetY;
            node.Opacity = node.TargetOpacity;
        }
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty) Rebuild();
        else if (change.Property == FocusTagProperty) Arrange();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>A new graph: the most used tags (plus the one in focus and its neighbours) are laid out; nodes already on the map glide.</summary>
    private void Rebuild()
    {
        var graph = Graph ?? TagGraph.Empty;
        var shown = graph.Nodes.Take(OverviewLimit).Select(node => node.Tag).ToList();
        if (FocusTag is { } focus && graph.Find(focus) is not null)
            shown.AddRange(graph.NeighboursOf(focus).Take(RingLimit).Select(neighbour => neighbour.Tag).Prepend(focus));
        shown = shown.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        overview = TagGraphLayout.Overview(graph, shown);
        maxFiles = Math.Max(1, graph.Nodes.Count == 0 ? 1 : graph.Nodes[0].Files);
        foreach (var gone in nodes.Keys.Where(tag => !overview.ContainsKey(tag)).ToList())
            nodes.Remove(gone);
        foreach (var tag in shown)
        {
            var files = graph.Find(tag)!.Files;
            if (nodes.TryGetValue(tag, out var node)) node.Files = files;
            else nodes[tag] = new Node(graph.Find(tag)!.Tag, files);
        }
        Arrange();
    }

    /// <summary>Where every node should go: the overview, or the tag in focus in the middle with its neighbours around it.</summary>
    private void Arrange()
    {
        var graph = Graph ?? TagGraph.Empty;
        var focus = FocusTag is { } wanted && nodes.ContainsKey(wanted) ? wanted : null;
        if (FocusTag is { } missing && focus is null && graph.Find(missing) is not null)
        {
            Rebuild();
            return;
        }
        var ring = focus is null ? [] : graph.NeighboursOf(focus).Take(RingLimit).ToList();
        var ringIndex = ring.Select((neighbour, index) => (neighbour.Tag, index)).ToDictionary(pair => pair.Tag, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var strongest = ring.Count == 0 ? 1 : ring.Max(neighbour => neighbour.Strength);
        foreach (var node in nodes.Values)
        {
            var (x, y) = overview.GetValueOrDefault(node.Tag);
            if (focus is null)
            {
                (node.TargetX, node.TargetY, node.TargetOpacity) = (x, y, 1);
            }
            else if (string.Equals(node.Tag, focus, StringComparison.OrdinalIgnoreCase))
            {
                (node.TargetX, node.TargetY, node.TargetOpacity) = (0, 0, 1);
            }
            else if (ringIndex.TryGetValue(node.Tag, out var index))
            {
                // Around the tag in focus, strongest first from the top, closer the more they go together.
                var angle = -Math.PI / 2 + index * 2 * Math.PI / ring.Count;
                var radius = 0.78 - 0.4 * (ring[index].Strength / strongest);
                (node.TargetX, node.TargetY, node.TargetOpacity) = (Math.Cos(angle) * radius, Math.Sin(angle) * radius, 1);
            }
            else
            {
                // Everything else steps back to the edge and dims.
                var length = Math.Max(0.2, Math.Sqrt(x * x + y * y));
                (node.TargetX, node.TargetY, node.TargetOpacity) = (x / length * 1.15, y / length * 1.15, 0.12);
            }
            if (node.Opacity == 0 && node.X == 0 && node.Y == 0)
            {
                // A node that just appeared grows out of the middle.
                node.X = node.TargetX * 0.2;
                node.Y = node.TargetY * 0.2;
            }
        }
        timer.Start();
        InvalidateVisual();
    }

    private void Tick()
    {
        if (!IsEffectivelyVisible)
        {
            timer.Stop();
            return;
        }
        time += 0.016;
        var moving = false;
        foreach (var node in nodes.Values)
        {
            node.X += (node.TargetX - node.X) * 0.14;
            node.Y += (node.TargetY - node.Y) * 0.14;
            node.Opacity += (node.TargetOpacity - node.Opacity) * 0.14;
            moving |= Math.Abs(node.TargetX - node.X) > 0.0005 || Math.Abs(node.TargetY - node.Y) > 0.0005 || Math.Abs(node.TargetOpacity - node.Opacity) > 0.005;
        }
        // The focus keeps breathing; otherwise the timer stops once everything is in place.
        if (!moving && FocusTag is null) timer.Stop();
        InvalidateVisual();
    }

    private double OpenWidth => Math.Max(120, Bounds.Width - RightInset);
    private double Scale => Math.Min(OpenWidth, Bounds.Height) * 0.4 * zoom;
    private Point Centre => new Point(OpenWidth / 2, Bounds.Height / 2) + pan;
    private Point ToScreen(double x, double y) => Centre + new Vector(x * Scale, y * Scale);
    private double RadiusOf(Node node) => (7 + 17 * Math.Sqrt(node.Files / (double)maxFiles)) * Math.Clamp(Math.Sqrt(zoom), 0.7, 1.6);

    private string? HitTest(Point point)
    {
        string? best = null;
        var bestDistance = double.MaxValue;
        foreach (var node in nodes.Values)
        {
            if (node.Opacity < 0.3) continue;
            var centre = ToScreen(node.X, node.Y);
            var distance = Point.Distance(centre, point);
            if (distance <= RadiusOf(node) + 6 && distance < bestDistance)
                (best, bestDistance) = (node.Tag, distance);
        }
        return best;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds);
        context.DrawEllipse(VioletGlow, null, new Point(bounds.Width * 0.22, bounds.Height * 0.18), bounds.Width * 0.55, bounds.Height * 0.6);
        context.DrawEllipse(CyanGlow, null, new Point(bounds.Width * 0.85, bounds.Height * 0.9), bounds.Width * 0.5, bounds.Height * 0.55);
        if (nodes.Count == 0)
        {
            var empty = Label("No tags yet. Tag a few files and they appear here, linked when they are used together.", 13, Color.FromArgb(0xC0, Ink.R, Ink.G, Ink.B), FontWeight.Normal);
            context.DrawText(empty, new Point((OpenWidth - empty.Width) / 2, (bounds.Height - empty.Height) / 2));
            return;
        }
        var graph = Graph ?? TagGraph.Empty;
        var focus = FocusTag is { } wanted && nodes.ContainsKey(wanted) ? wanted : null;
        var spotlight = hovered ?? focus;

        foreach (var edge in graph.Edges)
        {
            if (!nodes.TryGetValue(edge.From, out var from) || !nodes.TryGetValue(edge.To, out var to)) continue;
            var touches = spotlight is not null && (string.Equals(edge.From, spotlight, StringComparison.OrdinalIgnoreCase) || string.Equals(edge.To, spotlight, StringComparison.OrdinalIgnoreCase));
            var alpha = Math.Min(from.Opacity, to.Opacity) * (spotlight is null ? 0.38 : touches ? 0.9 : focus is null ? 0.16 : 0.05);
            if (alpha < 0.02) continue;
            var start = ToScreen(from.X, from.Y);
            var end = ToScreen(to.X, to.Y);
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(start, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(end, RelativeUnit.Absolute),
                GradientStops = { new GradientStop(WithAlpha(from.Color, alpha), 0), new GradientStop(WithAlpha(to.Color, alpha), 1) }
            };
            context.DrawLine(new Pen(brush, 1 + 5 * edge.Strength * (touches ? 1.3 : 1), lineCap: PenLineCap.Round), start, end);
        }

        // Dim ones first, so the lit ones and the spotlight sit on top.
        foreach (var node in nodes.Values.OrderBy(node => node.Opacity).ThenBy(node => string.Equals(node.Tag, spotlight, StringComparison.OrdinalIgnoreCase)))
        {
            if (node.Opacity < 0.02) continue;
            var centre = ToScreen(node.X, node.Y);
            var radius = RadiusOf(node);
            var isFocus = string.Equals(node.Tag, focus, StringComparison.OrdinalIgnoreCase);
            var isLit = isFocus || string.Equals(node.Tag, hovered, StringComparison.OrdinalIgnoreCase);
            var glow = radius * (isFocus ? 3.2 + 0.35 * Math.Sin(time * 2.4) : isLit ? 3 : 2.4);
            context.DrawEllipse(Glow(node.Color, (byte)(node.Opacity * (isLit ? 150 : 95))), null, centre, glow, glow);
            var core = new RadialGradientBrush
            {
                GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
                Center = new RelativePoint(0.4, 0.35, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
                GradientStops = { new GradientStop(WithAlpha(node.Light, node.Opacity), 0), new GradientStop(WithAlpha(node.Color, node.Opacity), 0.7), new GradientStop(WithAlpha(Darker(node.Color), node.Opacity), 1) }
            };
            context.DrawEllipse(core, isLit ? new Pen(new SolidColorBrush(WithAlpha(Colors.White, 0.85 * node.Opacity)), 1.6) : null, centre, radius, radius);
        }

        // Labels: the tag in focus and its neighbours, the one under the pointer, and in the overview the most used ones.
        var labelled = nodes.Values.Where(node => node.Opacity > 0.5).OrderByDescending(node => node.Files).Take(focus is null && zoom < 1.4 ? 30 : int.MaxValue).ToList();
        if (hovered is { } hover && nodes.TryGetValue(hover, out var hoverNode) && !labelled.Contains(hoverNode)) labelled.Add(hoverNode);
        foreach (var node in labelled)
        {
            var centre = ToScreen(node.X, node.Y);
            var isFocus = string.Equals(node.Tag, focus, StringComparison.OrdinalIgnoreCase);
            var text = Label(node.Tag, isFocus ? 15 : 12, WithAlpha(Ink, node.Opacity), isFocus ? FontWeight.Bold : FontWeight.SemiBold);
            var origin = new Point(centre.X - text.Width / 2, centre.Y + RadiusOf(node) + 5);
            var plate = new Rect(origin.X - 6, origin.Y - 1, text.Width + 12, text.Height + 2);
            context.DrawRectangle(LabelShadow, null, plate, 8, 8);
            context.DrawText(text, origin);
            if (isFocus || string.Equals(node.Tag, hovered, StringComparison.OrdinalIgnoreCase))
            {
                var count = Label($"{node.Files:N0} files", 11, WithAlpha(node.Light, node.Opacity), FontWeight.Normal);
                context.DrawText(count, new Point(centre.X - count.Width / 2, plate.Bottom + 2));
            }
        }
        if (focus is not null && !timer.IsEnabled && IsEffectivelyVisible) timer.Start();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        pressPoint = lastPoint = point;
        pressedTag = HitTest(point);
        dragged = false;
        e.Pointer.Capture(this);
        if (e.ClickCount == 2 && lastClickedTag is { } clicked && nodes.TryGetValue(clicked, out var node))
        {
            TagActivated?.Invoke(this, node.Tag);
            pressPoint = null;
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (pressPoint is { } start)
        {
            if (!dragged && Point.Distance(start, point) > 4) dragged = true;
            if (dragged)
            {
                pan += point - lastPoint;
                InvalidateVisual();
            }
            lastPoint = point;
            return;
        }
        var hit = HitTest(point);
        if (!string.Equals(hit, hovered, StringComparison.OrdinalIgnoreCase))
        {
            hovered = hit;
            Cursor = hit is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (pressPoint is not null && !dragged)
        {
            lastClickedTag = pressedTag;
            SetCurrentValue(FocusTagProperty, pressedTag is null ? null : nodes[pressedTag].Tag);
        }
        pressPoint = null;
        pressedTag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hovered is null) return;
        hovered = null;
        InvalidateVisual();
    }

    /// <summary>The wheel zooms around the pointer, so what is under it stays there.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        var next = Math.Clamp(zoom * Math.Pow(1.15, e.Delta.Y), 0.4, 5);
        var centre = new Point(OpenWidth / 2, Bounds.Height / 2);
        pan = point - centre - (point - centre - pan) * (next / zoom);
        zoom = next;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Back to the whole map at its first size and place.</summary>
    internal void ResetView()
    {
        zoom = 1;
        pan = default;
        InvalidateVisual();
    }

    private static FormattedText Label(string text, double size, Color color, FontWeight weight) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(LabelFace.FontFamily, FontStyle.Normal, weight), size, new SolidColorBrush(color));

    private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);

    private static Color Darker(Color color) => Color.FromRgb((byte)(color.R * 0.55), (byte)(color.G * 0.55), (byte)(color.B * 0.6));

    private static IBrush Glow(Color color, byte alpha) => new RadialGradientBrush
    {
        GradientStops = { new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb((byte)(alpha / 3), color.R, color.G, color.B), 0.45), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1) }
    };
}
