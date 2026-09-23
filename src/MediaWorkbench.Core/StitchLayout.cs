namespace MediaWorkbench.Core;

/// <summary>How the pictures are laid out when several are combined into one.</summary>
public enum StitchDirection { Row, Column, Grid }

/// <summary>The size of one source picture, in its own pixels.</summary>
public sealed record StitchItem(int Width, int Height);

/// <summary>Where one picture ends up in the combined picture, and how big it is drawn.</summary>
public sealed record StitchPlacement(int Index, int X, int Y, int Width, int Height);

public sealed record StitchPlan(int Width, int Height, IReadOnlyList<StitchPlacement> Placements)
{
    public static readonly StitchPlan Empty = new(0, 0, []);
    public bool IsEmpty => Placements.Count == 0 || Width <= 0 || Height <= 0;
}

/// <summary>
/// Works out the combined picture: its size, and where each source picture is drawn in it. Pure arithmetic, so the preview
/// on screen and the exported file are laid out by the same code and can differ only in how they are drawn.
/// </summary>
public static class StitchLayout
{
    /// <summary>No side of a combined picture goes past this; beyond it the whole plan is scaled down to fit.</summary>
    public const int MaximumSide = 20000;
    public const int MaximumGap = 400;
    public const int MaximumColumns = 12;

    /// <param name="matchSizes">
    /// True scales the pictures so they line up: to the shortest one in a row, the narrowest in a column, or into the
    /// smallest box in a grid. Nothing is ever enlarged. False keeps every picture's own pixels and centres it in its place.
    /// </param>
    public static StitchPlan Compute(IReadOnlyList<StitchItem> items, StitchDirection direction, int gap, int columns, bool matchSizes)
    {
        var usable = items.Where(item => item.Width > 0 && item.Height > 0).ToList();
        if (usable.Count == 0)
            return StitchPlan.Empty;
        gap = Math.Clamp(gap, 0, MaximumGap);
        var sizes = Sized(items, usable, direction, columns, matchSizes);
        var plan = direction switch
        {
            StitchDirection.Row => Line(items, sizes, gap, horizontal: true),
            StitchDirection.Column => Line(items, sizes, gap, horizontal: false),
            _ => Grid(items, sizes, gap, Math.Clamp(columns, 1, MaximumColumns))
        };
        return Fit(plan);
    }

    /// <summary>The size each picture is drawn at, before it is placed. Indexed like the original list; unusable pictures stay zero.</summary>
    private static (int Width, int Height)[] Sized(IReadOnlyList<StitchItem> items, List<StitchItem> usable, StitchDirection direction, int columns, bool matchSizes)
    {
        var sizes = new (int Width, int Height)[items.Count];
        var height = usable.Min(item => item.Height);
        var width = usable.Min(item => item.Width);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.Width <= 0 || item.Height <= 0)
                continue;
            sizes[index] = !matchSizes ? (item.Width, item.Height) : direction switch
            {
                StitchDirection.Row => (Scale(item.Width, height, item.Height), height),
                StitchDirection.Column => (width, Scale(item.Height, width, item.Width)),
                _ => Inside(item, width, height)
            };
        }
        return sizes;

        static int Scale(int value, int target, int from) => Math.Max(1, (int)Math.Round(value * (double)target / from));
        static (int Width, int Height) Inside(StitchItem item, int boxWidth, int boxHeight)
        {
            var factor = Math.Min(boxWidth / (double)item.Width, boxHeight / (double)item.Height);
            return (Math.Max(1, (int)Math.Round(item.Width * factor)), Math.Max(1, (int)Math.Round(item.Height * factor)));
        }
    }

    private static StitchPlan Line(IReadOnlyList<StitchItem> items, (int Width, int Height)[] sizes, int gap, bool horizontal)
    {
        var placements = new List<StitchPlacement>();
        var across = 0;
        var thickness = 0;
        for (var index = 0; index < items.Count; index++)
            if (sizes[index] is { Width: > 0 } size)
                thickness = Math.Max(thickness, horizontal ? size.Height : size.Width);
        for (var index = 0; index < items.Count; index++)
        {
            var size = sizes[index];
            if (size.Width <= 0)
                continue;
            if (placements.Count > 0)
                across += gap;
            // Anything shorter (or narrower) than the line sits in the middle of it, rather than along one edge.
            var offset = ((horizontal ? thickness - size.Height : thickness - size.Width) + 1) / 2;
            placements.Add(horizontal
                ? new StitchPlacement(index, across, offset, size.Width, size.Height)
                : new StitchPlacement(index, offset, across, size.Width, size.Height));
            across += horizontal ? size.Width : size.Height;
        }
        return new StitchPlan(horizontal ? across : thickness, horizontal ? thickness : across, placements);
    }

    private static StitchPlan Grid(IReadOnlyList<StitchItem> items, (int Width, int Height)[] sizes, int gap, int columns)
    {
        var drawn = Enumerable.Range(0, items.Count).Where(index => sizes[index].Width > 0).ToList();
        if (drawn.Count == 0)
            return StitchPlan.Empty;
        columns = Math.Min(columns, drawn.Count);
        var cellWidth = drawn.Max(index => sizes[index].Width);
        var cellHeight = drawn.Max(index => sizes[index].Height);
        var rows = (drawn.Count + columns - 1) / columns;
        var placements = new List<StitchPlacement>();
        for (var slot = 0; slot < drawn.Count; slot++)
        {
            var index = drawn[slot];
            var size = sizes[index];
            var left = slot % columns * (cellWidth + gap) + (cellWidth - size.Width + 1) / 2;
            var top = slot / columns * (cellHeight + gap) + (cellHeight - size.Height + 1) / 2;
            placements.Add(new StitchPlacement(index, left, top, size.Width, size.Height));
        }
        return new StitchPlan(columns * cellWidth + (columns - 1) * gap, rows * cellHeight + (rows - 1) * gap, placements);
    }

    /// <summary>Scales a whole plan down if it came out larger than anything sensible to write to a file.</summary>
    private static StitchPlan Fit(StitchPlan plan)
    {
        if (plan.IsEmpty)
            return StitchPlan.Empty;
        var longest = Math.Max(plan.Width, plan.Height);
        if (longest <= MaximumSide)
            return plan;
        var factor = MaximumSide / (double)longest;
        return new StitchPlan(
            Math.Max(1, (int)Math.Round(plan.Width * factor)),
            Math.Max(1, (int)Math.Round(plan.Height * factor)),
            plan.Placements.Select(placement => new StitchPlacement(placement.Index,
                (int)Math.Round(placement.X * factor), (int)Math.Round(placement.Y * factor),
                Math.Max(1, (int)Math.Round(placement.Width * factor)), Math.Max(1, (int)Math.Round(placement.Height * factor)))).ToList());
    }
}
