using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using MediaWorkbench.Avalonia.ViewModels;
using MediaWorkbench.Avalonia.Views;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia.Tests;

/// <summary>
/// The new app end to end on made-up media: a folder is added and listed, a video opens on its first exact frame, steps to
/// another exact frame, plays through VLC into the app's own picture and pauses on a frame, and the window draws all of it.
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

    private async Task<(string Media, string Video, string Photo)> MakeMediaAsync()
    {
        var media = Path.Combine(workspace, "media");
        Directory.CreateDirectory(Path.Combine(media, "clips"));
        var tools = ToolPaths.Resolve();
        var video = Path.Combine(media, "clips", "count.mp4");
        var photo = Path.Combine(media, "still.png");
        await new ProcessRunner().RunAsync(tools.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=3",
            "-c:v", "libx264", "-g", "10", "-pix_fmt", "yuv420p", video]);
        await new ProcessRunner().RunAsync(tools.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "mandelbrot=size=640x360", "-frames:v", "1", photo]);
        return (media, video, photo);
    }

    private static async Task Until(Func<bool> condition, string message, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail(message);
            await Task.Delay(25);
        }
    }

    private ShellViewModel NewShell() => new(Path.Combine(workspace, "data")) { ImportClassicWorkspace = false };

    [AvaloniaFact]
    public async Task AFolderIsListedAndAVideoStepsPlaysAndPausesOnExactFrames()
    {
        var (media, video, photo) = await MakeMediaAsync();
        using var shell = NewShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.StartAsync();
        Assert.True(shell.IsEmpty);

        await shell.AddFolderAsync(media);
        await Until(() => !shell.IsScanning && shell.Items.Count == 2, $"The folder should list its two files, not {shell.Items.Count}.");
        Assert.Equal(["media"], shell.Folders.Select(folder => folder.Name));
        Assert.Equal("2", shell.Folders[0].CountText);

        // A subfolder narrows the filmstrip to it.
        shell.Folders[0].IsExpanded = true;
        shell.SelectedFolder = shell.Folders[0].Children.Single(child => child.Name == "clips");
        Assert.Equal([video], shell.Items.Select(item => item.Asset.FullPath));
        shell.SelectedFolder = null;

        shell.SelectedItem = shell.Items.Single(item => item.Asset.FullPath == photo);
        await Until(() => shell.Picture is { PixelSize.Width: 640 } && !shell.IsLoading, "A photo should open at its own size.");

        shell.SelectedItem = shell.Items.Single(item => item.Asset.FullPath == video);
        await Until(() => shell.CanStep && shell.Picture is { PixelSize.Width: 320 }, "A video should open on its first frame and be indexed.");
        Assert.Equal(30, shell.MaximumFrame + 1);
        var first = shell.Picture;

        shell.CurrentFrame = 15;
        await Until(() => shell.Picture is { } shown && !ReferenceEquals(shown, first), "Stepping should show another decoded frame.");
        Assert.Equal("16 / 30", shell.FrameText);

        // Play starts on the frame on screen; the live picture keeps changing; pause lands on a later frame.
        shell.PlayPauseCommand.Execute(null);
        Assert.True(shell.IsPlaying);
        var revision = shell.PictureRevision;
        await Until(() => shell.PictureRevision > revision + 3, "Playing should keep drawing new pictures.");
        await Task.Delay(400);
        shell.PlayPauseCommand.Execute(null);
        Assert.False(shell.IsPlaying);
        Assert.InRange(shell.CurrentFrame, 16, 29);

        // The window draws the frame, the transport and the filmstrip.
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 900);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AFolderAlreadyOpenIsNotAddedTwiceAndRemovingItLeavesTheDiskAlone()
    {
        var (media, video, _) = await MakeMediaAsync();
        using var shell = NewShell();
        await shell.StartAsync();
        await shell.AddFolderAsync(media);
        await shell.AddFolderAsync(media + Path.DirectorySeparatorChar);
        Assert.Single(shell.Folders);
        Assert.Contains("already in the workspace", shell.Status);
        shell.RemoveFolderCommand.Execute(shell.Folders[0]);
        Assert.Empty(shell.Folders);
        Assert.True(shell.IsEmpty);
        Assert.True(File.Exists(video));
    }
}
