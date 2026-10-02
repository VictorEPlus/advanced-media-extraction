using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaWorkbench.Core;
using Avalonia.Headless.XUnit;

namespace MediaWorkbench.Avalonia.Tests;

/// <summary>
/// The WPF app's whole on-screen check, run against the Avalonia app: every feature on made-up media in a data folder of its
/// own, through the real window. The checks are the WPF ones, adapted only where Avalonia names things differently.
/// </summary>
public sealed class DesktopChecksTest : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "MediaWorkbench.Avalonia.Tests", "checks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // Kept when a check fails, for the pictures it drew, or when MEDIAWORKBENCH_KEEP_CHECKS is set; otherwise removed.
        if (passed && Environment.GetEnvironmentVariable("MEDIAWORKBENCH_KEEP_CHECKS") is null)
            try { Directory.Delete(dataDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private bool passed;

    [AvaloniaFact]
    public async Task EveryFeatureWorksLikeTheWpfApp()
    {
        Directory.CreateDirectory(dataDirectory);
        using var viewModel = new MainViewModel(dataDirectory);
        var window = new MainWindow(viewModel) { Width = 1440, Height = 900 };
        window.Show();
        try
        {
            await DesktopSmokeTest.RunAsync(viewModel, window, dataDirectory);
            passed = true;
        }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException($"{exception.Message}\nPictures and data: {dataDirectory}\n{exception.StackTrace}");
        }
        finally { window.Close(); }
    }
}
