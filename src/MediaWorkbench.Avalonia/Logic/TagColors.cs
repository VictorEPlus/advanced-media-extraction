using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MediaWorkbench.Avalonia;

/// <summary>
/// Every tag has its own matte pastel colour, worked out from its name, so the same tag looks the same everywhere and keeps its
/// colour as other tags come and go. A tag that is on what is being tagged is filled with it (dark text); one that is not is shown
/// in it on the dark glass.
/// </summary>
public static class TagColors
{
    private static readonly Dictionary<string, Color> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dark navy text for a filled pastel bubble.</summary>
    public static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#131B30"));

    /// <summary>The tag's own pastel: hue from its name, kept soft (low saturation, high lightness) so a row of them stays calm.</summary>
    public static Color Of(string? tag)
    {
        tag ??= "";
        lock (Cache)
        {
            if (Cache.TryGetValue(tag, out var known)) return known;
            // FNV-1a over the lower-case name: stable across runs and machines, unlike string.GetHashCode.
            var hash = 2166136261u;
            foreach (var character in tag.ToLowerInvariant())
                hash = (hash ^ character) * 16777619u;
            var hue = hash % 360;
            var saturation = 0.42 + (hash / 360 % 3) * 0.06;
            var lightness = 0.78 + (hash / 1080 % 3) * 0.03;
            var color = new HslColor(1, hue, saturation, lightness).ToRgb();
            Cache[tag] = color;
            return color;
        }
    }

    private static SolidColorBrush WithAlpha(string? tag, byte alpha) => new(Color.FromArgb(alpha, Of(tag).R, Of(tag).G, Of(tag).B));

    /// <summary>Filled bubble: the tag is on it.</summary>
    public static readonly IValueConverter Fill = new FuncValueConverter<string?, IBrush>(tag => new SolidColorBrush(Of(tag)));
    /// <summary>A faint wash of the colour: the tag exists but is not on it.</summary>
    public static readonly IValueConverter Wash = new FuncValueConverter<string?, IBrush>(tag => WithAlpha(tag, 0x24));
    /// <summary>The colour as an outline, for a tag only some of the picked files carry or one that comes from a folder.</summary>
    public static readonly IValueConverter Outline = new FuncValueConverter<string?, IBrush>(tag => WithAlpha(tag, 0xB0));
    /// <summary>The colour as text on the dark glass.</summary>
    public static readonly IValueConverter Text = new FuncValueConverter<string?, IBrush>(tag => new SolidColorBrush(Of(tag)));

    /// <summary>Text on a tag bubble: dark on a filled one, the tag's colour otherwise. Bound as (tag, filled).</summary>
    public static readonly IMultiValueConverter Foreground = new FuncMultiValueConverter<object?, IBrush>(values =>
    {
        var list = values.ToList();
        return list is [string tag, true] ? Ink : new SolidColorBrush(Of(list.FirstOrDefault() as string));
    });

    /// <summary>A bubble's fill: the colour when the tag is on it, a faint wash otherwise. Bound as (tag, filled).</summary>
    public static readonly IMultiValueConverter Background = new FuncMultiValueConverter<object?, IBrush>(values =>
    {
        var list = values.ToList();
        var tag = list.FirstOrDefault() as string;
        return list is [_, true] ? new SolidColorBrush(Of(tag)) : WithAlpha(tag, 0x24);
    });
}
