using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MediaWorkbench.Avalonia;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWorkbench.Avalonia");

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var at = Array.IndexOf(args, "--data-dir");
            var dataDirectory = at >= 0 && at + 1 < args.Length ? Path.GetFullPath(args[at + 1]) : DefaultDataDirectory;
            try
            {
                if (at < 0) ImportClassicData(dataDirectory);
                var viewModel = new MainViewModel(dataDirectory);
                var window = new MainWindow(viewModel);
                desktop.MainWindow = window;
                desktop.ShutdownRequested += (_, _) => viewModel.Dispose();
                window.Opened += async (_, _) =>
                {
                    await viewModel.InitializeAsync();
                    // --open <file> selects that file once the workspace is up; --play also plays it (for checking by hand).
                    var open = Array.IndexOf(args, "--open") is var index and >= 0 && index + 1 < args.Length ? args[index + 1] : null;
                    if (open is not null) await viewModel.OpenPathAsync(open);
                    if (open is not null && args.Contains("--play"))
                    {
                        for (var wait = 0; wait < 400 && !(viewModel.CanPlay && !viewModel.IsPreviewBusy); wait++)
                            await Task.Delay(50);
                        if (viewModel.CanPlay) viewModel.TogglePlaybackCommand.Execute(null);
                    }
                };
            }
            catch (Exception exception)
            {
                Directory.CreateDirectory(dataDirectory);
                File.WriteAllText(Path.Combine(dataDirectory, "startup-error.log"), exception.ToString());
                throw;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The first start copies the WPF app's settings, catalog (index, favorites, tags), collections and export history, so the
    /// new app opens with the same workspace. It is a copy: nothing this app does can change the data the WPF app uses.
    /// </summary>
    private static void ImportClassicData(string dataDirectory)
    {
        // A marker rather than "no settings yet": the first preview of this app wrote settings of its own, worth nothing.
        var marker = Path.Combine(dataDirectory, "imported-from-wpf-app.txt");
        if (File.Exists(marker))
            return;
        var classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWorkbench");
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(marker, File.Exists(Path.Combine(classic, "settings.json"))
                ? $"Copied from {classic} on {DateTime.Now:yyyy-MM-dd HH:mm}. The WPF app's own data was not changed."
                : "There was no WPF app data to copy.");
            if (!File.Exists(Path.Combine(classic, "settings.json")))
                return;
            Directory.CreateDirectory(dataDirectory);
            foreach (var name in new[] { "catalog.db", "catalog.db-wal", "catalog.db-shm", "export-history.json", "settings.json" })
                if (File.Exists(Path.Combine(classic, name)))
                    File.Copy(Path.Combine(classic, name), Path.Combine(dataDirectory, name), overwrite: true);
            var collections = Path.Combine(classic, "collections");
            if (Directory.Exists(collections))
            {
                Directory.CreateDirectory(Path.Combine(dataDirectory, "collections"));
                foreach (var file in Directory.EnumerateFiles(collections, "*.json"))
                    File.Copy(file, Path.Combine(dataDirectory, "collections", Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
