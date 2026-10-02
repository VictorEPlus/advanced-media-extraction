using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MediaWorkbench.Avalonia.ViewModels;
using MediaWorkbench.Avalonia.Views;

namespace MediaWorkbench.Avalonia;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new ShellViewModel(ShellViewModel.DefaultDataDirectory);
            var window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => shell.Dispose();
            // --open <file> selects that file once the workspace is up (used to check the app by hand and in screenshots).
            var args = desktop.Args ?? [];
            var open = Array.IndexOf(args, "--open") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
            shell.PlayWhenReady = args.Contains("--play");
            _ = shell.StartAsync(open);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
