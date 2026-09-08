namespace MediaWorkbench.Tests;

public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MediaWorkbench.Tests", Guid.NewGuid().ToString("N"));

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public string FilePath(string relative)
    {
        var path = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return path;
    }

    public void Dispose()
    {
        var allowed = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MediaWorkbench.Tests")) + System.IO.Path.DirectorySeparatorChar;
        var resolved = System.IO.Path.GetFullPath(Path);
        if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to clean a test folder outside the test workspace.");
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, true);
    }
}
