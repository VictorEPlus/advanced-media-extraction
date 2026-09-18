using System.Text.Json;

namespace MediaWorkbench.Core;

public sealed record StagingCollection(int Version, string Name, string[] Paths);

public sealed class CollectionStore
{
    public StagingCollection Load(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024)
            throw new InvalidDataException("Collection JSON exceeds the 32 MB limit.");
        var collection = JsonSerializer.Deserialize<StagingCollection>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Collection JSON is empty.");
        return Validate(collection);
    }

    public void Save(string path, StagingCollection collection)
    {
        collection = Validate(collection);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(collection, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static StagingCollection Add(StagingCollection collection, IEnumerable<string> paths) =>
        Validate(collection with { Paths = collection.Paths.Concat(paths).ToArray() });

    public static StagingCollection Remove(StagingCollection collection, string path) =>
        collection with { Paths = collection.Paths.Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase)).ToArray() };

    private static StagingCollection Validate(StagingCollection collection)
    {
        if (collection.Version != 1 || string.IsNullOrWhiteSpace(collection.Name) || collection.Paths is null || collection.Paths.Length > 100000)
            throw new InvalidDataException("Expected collection Version 1, a Name, and up to 100,000 file Paths.");
        if (collection.Paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("Collection entries must be absolute local or UNC file paths.");
        return collection with { Name = collection.Name.Trim(), Paths = collection.Paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }
}

public static class NaturalOrder
{
    public static int Compare(string left, string right)
    {
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsAsciiDigit(left[leftIndex]) && char.IsAsciiDigit(right[rightIndex]))
            {
                var leftStart = leftIndex;
                var rightStart = rightIndex;
                while (leftIndex < left.Length && char.IsAsciiDigit(left[leftIndex])) leftIndex++;
                while (rightIndex < right.Length && char.IsAsciiDigit(right[rightIndex])) rightIndex++;
                while (leftStart < leftIndex - 1 && left[leftStart] == '0') leftStart++;
                while (rightStart < rightIndex - 1 && right[rightStart] == '0') rightStart++;
                var comparison = (leftIndex - leftStart).CompareTo(rightIndex - rightStart);
                if (comparison != 0) return comparison;
                comparison = left.AsSpan(leftStart, leftIndex - leftStart).SequenceCompareTo(right.AsSpan(rightStart, rightIndex - rightStart));
                if (comparison != 0) return comparison;
                continue;
            }
            var characterComparison = char.ToUpperInvariant(left[leftIndex++]).CompareTo(char.ToUpperInvariant(right[rightIndex++]));
            if (characterComparison != 0) return characterComparison;
        }
        var lengthComparison = (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        return lengthComparison != 0 ? lengthComparison : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }
}

public sealed record PixelCrop(int X, int Y, int Width, int Height)
{
    public static PixelCrop? FromDrag(double startX, double startY, double endX, double endY, double viewportWidth, double viewportHeight, int pixelWidth, int pixelHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || pixelWidth <= 0 || pixelHeight <= 0) return null;
        var scale = Math.Min(viewportWidth / pixelWidth, viewportHeight / pixelHeight);
        var offsetX = (viewportWidth - pixelWidth * scale) / 2;
        var offsetY = (viewportHeight - pixelHeight * scale) / 2;
        var left = (int)Math.Floor(Math.Clamp((Math.Min(startX, endX) - offsetX) / scale, 0, pixelWidth));
        var top = (int)Math.Floor(Math.Clamp((Math.Min(startY, endY) - offsetY) / scale, 0, pixelHeight));
        var right = (int)Math.Ceiling(Math.Clamp((Math.Max(startX, endX) - offsetX) / scale, 0, pixelWidth));
        var bottom = (int)Math.Ceiling(Math.Clamp((Math.Max(startY, endY) - offsetY) / scale, 0, pixelHeight));
        return right > left && bottom > top ? new PixelCrop(left, top, right - left, bottom - top) : null;
    }

    public void Validate(int width, int height)
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0 || (long)X + Width > width || (long)Y + Height > height)
            throw new ArgumentException("Crop must stay within the source image pixels.");
    }
}

public static class MediaDimensions
{
    public static string AspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0) return "Unknown";
        var divisor = width;
        var remainder = height;
        while (remainder != 0) (divisor, remainder) = (remainder, divisor % remainder);
        return $"{width / divisor}:{height / divisor}";
    }

    /// <summary>The shapes people name, landscape and portrait, widest last in each half.</summary>
    private static readonly (int Width, int Height)[] CommonRatios =
    [
        (1, 1), (5, 4), (4, 3), (3, 2), (16, 10), (16, 9), (2, 1), (21, 9),
        (4, 5), (3, 4), (2, 3), (10, 16), (9, 16), (1, 2), (9, 21)
    ];

    /// <summary>The everyday ratio closest in shape to width:height, compared in proportion so 9:16 and 16:9 are treated alike.</summary>
    public static string NearestCommonRatio(int width, int height)
    {
        if (width <= 0 || height <= 0) return "Unknown";
        var best = Nearest(width, height);
        return $"{best.Width}:{best.Height}";
    }

    private static (int Width, int Height) Nearest(int width, int height)
    {
        var shape = Math.Log((double)width / height);
        return CommonRatios.MinBy(ratio => Math.Abs(Math.Log((double)ratio.Width / ratio.Height) - shape));
    }

    /// <summary>
    /// The exact reduced ratio, followed by the closest everyday ratio when the exact one is not already an everyday one:
    /// "16:9", "683:384 (about 16:9)", "88:115 (about 3:4)".
    /// </summary>
    public static string DescribeAspect(int width, int height)
    {
        var exact = AspectRatio(width, height);
        if (width <= 0 || height <= 0) return exact;
        var best = Nearest(width, height);
        var nearest = $"{best.Width}:{best.Height}";
        // 1280 x 800 reduces to 8:5, which everyone calls 16:10: when the shape is exactly an everyday one, use its everyday name.
        return (long)width * best.Height == (long)height * best.Width ? nearest : $"{exact} (about {nearest})";
    }
}
