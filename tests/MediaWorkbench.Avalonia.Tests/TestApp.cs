using Avalonia;
using Avalonia.Headless;
using MediaWorkbench.Avalonia.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace MediaWorkbench.Avalonia.Tests;

/// <summary>The real app's styles and theme, on Avalonia's headless platform with Skia, so windows can be drawn into pictures.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
