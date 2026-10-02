using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MediaWorkbench.Avalonia.ViewModels;

public static class Converters
{
    /// <summary>Workspace folders are set a little bolder than their subfolders.</summary>
    public static readonly IValueConverter BoolToSemiBold = new FuncValueConverter<bool, FontWeight>(value => value ? FontWeight.SemiBold : FontWeight.Normal);
    public static readonly IValueConverter BoolToOpacity = new FuncValueConverter<bool, double>(value => value ? 1 : 0);
}
