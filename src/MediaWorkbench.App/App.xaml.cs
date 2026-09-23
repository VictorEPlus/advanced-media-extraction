using System.IO;
using System.Windows;

namespace MediaWorkbench.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        var smokeTest = args.Args.Contains("--smoke-test");
        var dataArgument = Array.IndexOf(args.Args, "--data-dir");
        var dataDirectory = dataArgument >= 0 && dataArgument + 1 < args.Args.Length
            ? Path.GetFullPath(args.Args[dataArgument + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWorkbench");
        try
        {
            var viewModel = new MainViewModel(dataDirectory);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            if (smokeTest)
            {
                await DesktopSmokeTest.RunAsync(viewModel, window, dataDirectory);
                viewModel.Dispose();
                File.WriteAllText(Path.Combine(dataDirectory, "smoke-test.txt"), "PASS: media tools, dark layout, contextual controls, favorites, exports, metadata/EXIF, crop pixels, tags, collections, and 5,000-item filmstrip virtualization with bounded thumbnails.");
                Shutdown(0);
                return;
            }
            if (args.Args.Contains("--motion-probe"))
            {
                await DesktopMotionProbe.RunAsync(viewModel, window, dataDirectory);
                window.Close();
                Shutdown(0);
                return;
            }
            window.Show();
            await viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "startup-error.log"), exception.ToString());
            if (!smokeTest)
                new StartupErrorWindow($"{exception.Message}\n\nDetails: {dataDirectory}").ShowDialog();
            Shutdown(1);
        }
    }
}
