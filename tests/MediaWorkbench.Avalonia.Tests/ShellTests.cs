using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia.Tests;

/// <summary>
/// The Avalonia app end to end on made-up media, through the real window: the folder tree opens and closes from its arrows and
/// the keyboard, a video opens on its first exact frame, steps to another exact frame, plays through VLC into the app's own
/// picture and pauses on a frame, and Copy saves what it copies.
/// </summary>
public sealed class ShellTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(), "MediaWorkbench.Avalonia.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(workspace, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private (MainViewModel Model, MainWindow Window) Open()
    {
        // Settings are written before the app starts, so exports (and Copy, which saves a PNG) go to the test's own folder.
        // Without this a fresh data folder falls back to the default under the user's Pictures.
        var data = Path.Combine(workspace, "data");
        Directory.CreateDirectory(data);
        new SettingsStore(Path.Combine(data, "settings.json")).Save(new AppSettings { ExportDirectory = Path.Combine(workspace, "exports") });
        var model = new MainViewModel(data);
        var window = new MainWindow(model) { Width = 1500, Height = 940 };
        window.Show();
        return (model, window);
    }

    private static async Task Until(Func<bool> condition, string message, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail(message);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Clicks the open/close arrow of a tree row, where a person would.</summary>
    private static void ClickArrow(MainWindow window, string rowPath)
    {
        Settle(window);
        var list = window.FindControl<ListBox>("FolderTreeList")!;
        var row = ((MainViewModel)window.DataContext!).FolderRows.Single(row => row.Node.Path == rowPath);
        var container = list.ContainerFromItem(row) ?? throw new InvalidOperationException("The row is not on screen: " + rowPath);
        var arrow = container.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "FolderToggle");
        var point = arrow.TranslatePoint(new Point(arrow.Bounds.Width / 2, arrow.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    /// <summary>Clicks the name of a tree row: selects it and gives the tree the keyboard.</summary>
    private static void ClickName(MainWindow window, string rowPath)
    {
        Settle(window);
        var list = window.FindControl<ListBox>("FolderTreeList")!;
        var row = ((MainViewModel)window.DataContext!).FolderRows.Single(row => row.Node.Path == rowPath);
        var container = list.ContainerFromItem(row)!;
        var name = container.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("folderName"));
        var point = name.TranslatePoint(new Point(4, name.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    [AvaloniaFact]
    public async Task TheFolderTreeOpensAndClosesFromItsArrowsAndTheKeyboard()
    {
        var root = Path.Combine(workspace, "Shoots");
        foreach (var folder in new[] { @"shoot A\day 1", @"shoot A\day 2", "shoot B" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            for (var index = 0; index < 3; index++)
                File.WriteAllBytes(Path.Combine(root, folder, $"file{index}.png"), [1, 2, 3]);
        }
        var (model, window) = Open();
        await model.OpenLibraryAsync(root);
        Settle(window);
        string Rows() => string.Join("|", model.FolderRows.Select(row => row.Node.Path));
        Assert.Equal("|Shoots|Shoots\\shoot A|Shoots\\shoot B", Rows());

        ClickArrow(window, "Shoots");
        Assert.Equal("|Shoots", Rows());
        ClickArrow(window, "Shoots");
        Assert.Equal("|Shoots|Shoots\\shoot A|Shoots\\shoot B", Rows());

        // A subfolder opens from its arrow, and its days show; clicking the arrow again closes it.
        ClickArrow(window, "Shoots\\shoot A");
        Assert.Equal("|Shoots|Shoots\\shoot A|Shoots\\shoot A\\day 1|Shoots\\shoot A\\day 2|Shoots\\shoot B", Rows());
        ClickArrow(window, "Shoots\\shoot A");
        Assert.Equal("|Shoots|Shoots\\shoot A|Shoots\\shoot B", Rows());

        // Clicking the name selects the folder and shows it in the filmstrip; Right and Left open and close it.
        ClickName(window, "Shoots\\shoot A");
        Assert.Equal("Shoots\\shoot A", model.SelectedFolderRow?.Node.Path);
        Assert.Equal(6, model.LibraryView.Count);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Settle(window);
        Assert.Contains("Shoots\\shoot A\\day 1", Rows());
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Settle(window);
        Assert.DoesNotContain("Shoots\\shoot A\\day 1", Rows());
        window.Close();
    }

    private async Task<(string Media, string Video, string Photo)> MakeMediaAsync()
    {
        var media = Path.Combine(workspace, "media");
        Directory.CreateDirectory(Path.Combine(media, "clips"));
        var tools = ToolPaths.Resolve();
        var video = Path.Combine(media, "clips", "count.mp4");
        var photo = Path.Combine(media, "still.png");
        await new ProcessRunner().RunAsync(tools.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:v", "libx264", "-g", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video]);
        await new ProcessRunner().RunAsync(tools.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "mandelbrot=size=640x360", "-frames:v", "1", photo]);
        return (media, video, photo);
    }

    [AvaloniaFact]
    public async Task AVideoStepsPlaysAndPausesOnExactFramesAndCopySavesWhatItCopies()
    {
        var (media, video, photo) = await MakeMediaAsync();
        var (model, window) = Open();
        await model.OpenLibraryAsync(media);
        Assert.Equal(2, model.Assets.Count);

        model.SelectedAsset = model.Assets.Single(item => item.Asset.FullPath == photo);
        await Until(() => model.PreviewImage is { PixelSize.Width: 640 } && !model.IsPreviewBusy, "A photo should open at its own size.");
        model.CropSelection = new PixelCrop(10, 20, 80, 60);
        var (copied, copiedPath) = await model.ExportForClipboardAsync();
        Assert.True(File.Exists(copiedPath) && copiedPath.Contains("_crop_80x60"), "Copy should save the crop as a PNG: " + copiedPath);
        Assert.True(((IDataTransfer)copied).Contains(DataFormat.Bitmap) && ((IDataTransfer)copied).Contains(MainViewModel.PngFormat), "Copy should offer the picture and the PNG.");
        model.CropSelection = null;

        model.SelectedAsset = model.Assets.Single(item => item.Asset.FullPath == video);
        await Until(() => model.HasFrames && !model.IsPreviewBusy && model.PreviewImage is { PixelSize.Width: 320 }, "A video should open on its first frame and be indexed.");
        Assert.Equal(29, model.MaximumFrame);
        var first = model.PreviewImage;

        model.CurrentFrame = 15;
        await Until(() => model.DisplayedFrame == 15 && !ReferenceEquals(model.PreviewImage, first), "Stepping should show the exact decoded frame.");

        // Play starts on the frame on screen; the live picture keeps changing; pause lands on a later frame.
        model.TogglePlaybackCommand.Execute(null);
        await Until(() => model.LiveRevision > 3, "Playing should keep drawing new pictures.");
        await Task.Delay(300);
        model.TogglePlaybackCommand.Execute(null);
        await Until(() => !model.ShowPlayback && !model.IsPreviewBusy, "Pausing should settle on a frame.");
        Assert.InRange(model.CurrentFrame, 16, 29);

        model.MainTab = 1;
        Settle(window);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        window.Close();
    }
}
