using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MediaWorkbench.Avalonia;

/// <summary>Reads colour tokens from Theme.axaml for custom-drawn controls, so the palette is defined in exactly one place.</summary>
internal static class Tokens
{
    public static SolidColorBrush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is ISolidColorBrush brush
            ? new SolidColorBrush(brush.Color, brush.Opacity)
            : new SolidColorBrush(fallback);

    public static SolidColorBrush WithAlpha(ISolidColorBrush brush, byte alpha) =>
        new(Color.FromArgb(alpha, brush.Color.R, brush.Color.G, brush.Color.B));

    public static readonly Typeface Display = new(new FontFamily("avares://MediaWorkbench.Avalonia/Assets/Fonts#Cascadia Mono"));
    public static readonly Typeface Text = new(new FontFamily("fonts:Inter#Inter, Segoe UI"));
}
