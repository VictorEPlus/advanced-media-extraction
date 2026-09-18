using System.Windows;
using System.Windows.Media;

namespace MediaWorkbench.App;

/// <summary>Reads colour tokens from App.xaml for custom-drawn controls, so the palette is defined in exactly one place.</summary>
internal static class Tokens
{
    public static SolidColorBrush Brush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
            return brush;
        var created = new SolidColorBrush(fallback);
        created.Freeze();
        return created;
    }

    public static SolidColorBrush WithAlpha(SolidColorBrush brush, byte alpha)
    {
        var color = brush.Color;
        var created = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        created.Freeze();
        return created;
    }

    public static readonly Typeface Display = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface Text = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
}
