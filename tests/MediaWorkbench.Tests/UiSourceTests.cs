using System.Text;
using System.Xml.Linq;

namespace MediaWorkbench.Tests;

public sealed class UiSourceTests
{
    private static readonly string SourceDirectory = Path.Combine(AppContext.BaseDirectory, "UiSource");

    [Fact]
    public void UiSourcesAreValidUtf8WithoutCorruptedText()
    {
        var files = Directory.GetFiles(SourceDirectory, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var utf8 = new UTF8Encoding(false, true);
        foreach (var file in files)
        {
            var text = utf8.GetString(File.ReadAllBytes(file));
            foreach (var marker in new[] { "\u00C2\u00B7", "\u00E2\u20AC", "\u00E2\u02DC", "\u00E2\u2020", "\u00E2\u2013", "\u00E2\u2014", "\u00E2\u2026", "\uFFFD" })
                Assert.False(text.Contains(marker, StringComparison.Ordinal), $"Corrupted Unicode in {Path.GetFileName(file)}.");
        }
    }

    [Theory]
    [InlineData("ToggleFavoriteCommand", "\u2605 Favorite")]
    [InlineData("OpenExportsCommand", "Open exports \u2197")]
    [InlineData("ExportFavoritesCommand", "Export favorites\u2026")]
    [InlineData("TogglePlaybackCommand", "\u25B6 Play / pause")]
    public void XamlDecodesSymbolsExactly(string command, string expected)
    {
        var document = XDocument.Load(Path.Combine(SourceDirectory, "MainWindow.xaml"));
        var button = document.Descendants().Single(element => (string?)element.Attribute("Command") == $"{{Binding {command}}}");
        Assert.Equal(expected, (string?)button.Attribute("Content"));
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("StartupErrorWindow.xaml")]
    public void DarkThemeIsExplicitRatherThanInheritedFromWindows(string filename)
    {
        var app = XDocument.Load(Path.Combine(SourceDirectory, "App.xaml"));
        var window = XDocument.Load(Path.Combine(SourceDirectory, filename));
        Assert.Equal("Dark", (string?)app.Root!.Attribute("ThemeMode"));
        Assert.Equal("Dark", (string?)window.Root!.Attribute("ThemeMode"));
        Assert.Equal("{StaticResource DarkWindowStyle}", (string?)window.Root.Attribute("Style"));
    }

    [Fact]
    public void FolderAndFilePickersAreTheNativeWindowsDialogs()
    {
        // Product decision (2026-09-18): use the built-in Explorer dialogs, not an in-app picker.
        var dialogs = File.ReadAllText(Path.Combine(SourceDirectory, "NativeDialogs.cs"));
        Assert.Contains("new OpenFolderDialog", dialogs);
        Assert.Contains("new OpenFileDialog", dialogs);
        Assert.Contains("new SaveFileDialog", dialogs);
        Assert.Contains("OverwritePrompt = true", dialogs);
        foreach (var filename in new[] { "MainViewModel.cs", "MainViewModel.Organization.cs", "MainViewModel.Library.cs", "App.xaml.cs" })
        {
            var text = File.ReadAllText(Path.Combine(SourceDirectory, filename));
            Assert.DoesNotContain("PathPickerWindow", text);
            Assert.DoesNotContain("MessageBox.Show", text);
        }
    }

    [Fact]
    public void EveryTourStepTargetsANamedElementInTheMainWindow()
    {
        var names = XDocument.Load(Path.Combine(SourceDirectory, "MainWindow.xaml")).Descendants()
            .Select(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))).Where(name => name is not null).ToHashSet();
        var tour = File.ReadAllText(Path.Combine(SourceDirectory, "MainWindow.Tour.cs"));
        var targets = System.Text.RegularExpressions.Regex.Matches(tour, "new\\(\"([A-Za-z]+)\", \"").Select(match => match.Groups[1].Value).ToArray();
        Assert.True(targets.Length >= 50, "The tour should cover every panel and the main buttons.");
        Assert.All(targets, target => Assert.Contains(target, names));
    }

    [Fact]
    public void FilmstripIsPixelVirtualizedAndUsesRecycling()
    {
        var document = XDocument.Load(Path.Combine(SourceDirectory, "MainWindow.xaml"));
        var filmstrip = document.Descendants().Single(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "Filmstrip");
        Assert.Equal("Recycling", (string?)filmstrip.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("Pixel", (string?)filmstrip.Attribute("VirtualizingPanel.ScrollUnit"));
        Assert.Equal("True", (string?)filmstrip.Attribute("VirtualizingPanel.IsVirtualizing"));
        var images = filmstrip.Descendants().Where(element => element.Name.LocalName == "Image");
        Assert.All(images, image => Assert.Equal("ThumbnailUnloaded", (string?)image.Attribute("Unloaded")));
    }

    [Fact]
    public void ToolbarLabelsAndVideoPanelsAreMediaAware()
    {
        var document = XDocument.Load(Path.Combine(SourceDirectory, "MainWindow.xaml"));
        var export = document.Descendants().Single(element => (string?)element.Attribute("Command") == "{Binding ExportFrameCommand}");
        Assert.Equal("{Binding ExportLabel}", (string?)export.Attribute("Content"));
        foreach (var name in new[] { "VideoTimeline", "VideoExportPanel" })
        {
            var panel = document.Descendants().Single(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == name);
            Assert.Contains("IsVideo", (string?)panel.Attribute("Visibility"));
        }
    }
}
