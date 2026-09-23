using System.Windows;
using System.Windows.Controls;

namespace MediaWorkbench.App;

/// <summary>
/// A row (or column) with the same gap between every visible child. Button rows use this instead of margins on each button, so
/// the spacing is one number per row and a hidden button never leaves a double gap.
/// </summary>
public sealed class SpacedPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(SpacedPanel),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(SpacedPanel),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    private bool Horizontal => Orientation == Orientation.Horizontal;

    protected override Size MeasureOverride(Size available)
    {
        double along = 0, across = 0;
        var count = 0;
        var room = Horizontal ? new Size(double.PositiveInfinity, available.Height) : new Size(available.Width, double.PositiveInfinity);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(room);
            if (child.Visibility == Visibility.Collapsed) continue;
            along += Horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            across = Math.Max(across, Horizontal ? child.DesiredSize.Height : child.DesiredSize.Width);
            count++;
        }
        along += Math.Max(0, count - 1) * Spacing;
        return Horizontal ? new Size(along, across) : new Size(across, along);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double offset = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (Horizontal)
            {
                child.Arrange(new Rect(offset, 0, child.DesiredSize.Width, final.Height));
                offset += child.DesiredSize.Width + Spacing;
            }
            else
            {
                child.Arrange(new Rect(0, offset, final.Width, child.DesiredSize.Height));
                offset += child.DesiredSize.Height + Spacing;
            }
        }
        return final;
    }
}
