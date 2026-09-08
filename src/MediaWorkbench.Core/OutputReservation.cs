namespace MediaWorkbench.Core;

public sealed class OutputReservation : IDisposable
{
    public string Path { get; }
    private bool complete;

    private OutputReservation(string path) => Path = path;

    public static OutputReservation Create(string directory, string stem, string extension)
    {
        if (!System.IO.Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Choose an absolute export directory in Settings.");
        Directory.CreateDirectory(directory);
        var safeStem = new string(stem.Select(character => System.IO.Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
        safeStem = safeStem.TrimEnd('.', ' ');
        if (safeStem.Length > 100)
            safeStem = safeStem[..100];
        if (string.IsNullOrWhiteSpace(safeStem))
            safeStem = "export";
        if (extension.Length < 2 || extension[0] != '.' || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("Invalid export extension.");
        for (var suffix = 0; suffix < 10000; suffix++)
        {
            var name = suffix == 0 ? safeStem : $"{safeStem}_{suffix:000}";
            var path = System.IO.Path.Combine(directory, name + extension);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return new OutputReservation(path);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("Too many exports share this name. Choose another export directory.");
    }

    public void Complete()
    {
        if (!File.Exists(Path) || new FileInfo(Path).Length == 0)
            throw new IOException("The export produced no output.");
        complete = true;
    }

    public void Dispose()
    {
        if (!complete && File.Exists(Path))
            File.Delete(Path);
    }
}
