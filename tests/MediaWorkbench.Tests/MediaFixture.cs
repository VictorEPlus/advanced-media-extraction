using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

public sealed class MediaFixture : IAsyncLifetime
{
    public TemporaryDirectory Temporary { get; } = new();
    public ToolPaths Tools { get; } = ToolPaths.Resolve();
    public string VideoPath => Temporary.FilePath("media folder/clip's [sample] & 100% test.mkv");
    public string VariablePath => Temporary.FilePath("media folder/variable.mkv");
    /// <summary>300 frames, a keyframe every 30, B-frames, and timestamps that start at 5 s: exercises verified seeking across GOPs.</summary>
    public string LongPath => Temporary.FilePath("media folder/long offset.mkv");
    public MediaEngine Engine { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await Tools.CheckAsync(timeout.Token); }
        catch (Exception exception) { throw new InvalidOperationException("Integration tests require FFmpeg and FFprobe. Run scripts/Verify.ps1 or set AME_FFMPEG_DIR to their bin folder.", exception); }
        var runner = new ProcessRunner();
        await runner.RunAsync(Tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=6:duration=2", "-f", "lavfi", "-i", "aevalsrc=0.2*sin(2*PI*440*t)|0.2*sin(2*PI*880*t):s=48000:d=2", "-c:v", "libx264", "-bf", "2", "-pix_fmt", "yuv420p", "-c:a", "pcm_s16le", "-shortest", VideoPath], cancellationToken: timeout.Token);
        await runner.RunAsync(Tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=10:duration=1", "-vf", "setpts=if(lt(N\\,5)\\,N/(10*TB)\\,(0.5+(N-5)*0.2)/TB)", "-fps_mode", "vfr", "-c:v", "ffv1", VariablePath], cancellationToken: timeout.Token);
        await runner.RunAsync(Tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=30:duration=10", "-c:v", "libx264", "-g", "30", "-bf", "2", "-pix_fmt", "yuv420p", "-output_ts_offset", "5", LongPath], cancellationToken: timeout.Token);
        Engine = new MediaEngine(Tools, Temporary.FilePath("cache"));
    }

    public Task DisposeAsync()
    {
        Temporary.Dispose();
        return Task.CompletedTask;
    }

    public MediaAsset Asset(string path)
    {
        var file = new FileInfo(path);
        return new MediaAsset(Temporary.Path, System.IO.Path.GetRelativePath(Temporary.Path, path), MediaKind.Video, file.Length, file.LastWriteTimeUtc.Ticks);
    }
}
