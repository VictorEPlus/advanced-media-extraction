using System.Diagnostics;
using System.Text;

namespace MediaWorkbench.Core;

public sealed class ProcessRunner
{
    public async Task RunAsync(string executable, IEnumerable<string> arguments, Action<string>? output = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException($"Cannot start {executable}. Install FFmpeg or set its bin folder in Settings.", exception);
        }
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var errors = new StringBuilder();
        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                output?.Invoke(line);
        }, CancellationToken.None);
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                errors.AppendLine(line);
                if (errors.Length > 8192)
                    errors.Remove(0, errors.Length - 8192);
            }
        }, CancellationToken.None);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(CancellationToken.None));
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {errors.ToString().Trim()}");
    }

    public async Task<string> CaptureAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var result = new StringBuilder();
        await RunAsync(executable, arguments, line => result.AppendLine(line), cancellationToken);
        return result.ToString();
    }
}

public sealed record ToolPaths(string Ffmpeg, string Ffprobe)
{
    public static ToolPaths Resolve(string? configuredDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Environment.GetEnvironmentVariable("AME_FFMPEG_DIR") : configuredDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin");
            if (Directory.Exists(bundled))
                directory = bundled;
        }
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        return string.IsNullOrWhiteSpace(directory)
            ? new ToolPaths("ffmpeg" + suffix, "ffprobe" + suffix)
            : new ToolPaths(Path.Combine(directory, "ffmpeg" + suffix), Path.Combine(directory, "ffprobe" + suffix));
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        var runner = new ProcessRunner();
        await runner.RunAsync(Ffmpeg, ["-version"], cancellationToken: cancellationToken);
        await runner.RunAsync(Ffprobe, ["-version"], cancellationToken: cancellationToken);
    }
}
