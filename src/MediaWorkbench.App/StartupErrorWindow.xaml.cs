using System.Windows;

namespace MediaWorkbench.App;

public partial class StartupErrorWindow : Window
{
    public StartupErrorWindow(string message)
    {
        InitializeComponent();
        DataContext = message;
    }
}
