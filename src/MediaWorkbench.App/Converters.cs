using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MediaWorkbench.App;

/// <summary>
/// True shows, false hides but keeps the space. For controls that come and go with the selected file: collapsing them
/// made everything after them move, which is the jumping this app must not do.
/// </summary>
public sealed class BoolToHiddenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Visible : Visibility.Hidden;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Visible;
}
