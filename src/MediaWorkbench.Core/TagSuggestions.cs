using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaWorkbench.Core;

/// <summary>
/// The details of a file that suggestions compare: where it came from (camera, lens, day), where it lives (folder, numbered name),
/// what it is technically (size, frame rate, codec, length) and what it looks like (a 64-bit look-alike fingerprint).
/// Saved with a file when it is tagged, so suggestions never need to open other files.
/// </summary>
public sealed record FileTraits
{
    public MediaKind Kind { get; init; }
    public string? Camera { get; init; }
    public string? Lens { get; init; }
    /// <summary>The day it was taken or recorded, yyyy-MM-dd; from the file's own date only, never the file system's.</summary>
    public string? Day { get; init; }
    public string Folder { get; init; } = "";
    /// <summary>A numbered name split into its stem and number: DSC_0412 is ("DSC_", 412).</summary>
    public string? NameStem { get; init; }
    public long? NameNumber { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? FrameRate { get; init; }
    public string? Codec { get; init; }
    public double Duration { get; init; }
    public ulong? Visual { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static FileTraits? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FileTraits>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Builds traits from what the app already reads: the scan (kind, path) and the details list, whose names come from the image
    /// reader ("Camera maker", "Date taken") and FFprobe (width, codec_name, "Embedded creation_time", phone make and model tags).
    /// </summary>
    public static FileTraits From(MediaAsset asset, IReadOnlyDictionary<string, string> details, int width, int height, double duration, ulong? visual)
    {
        string? Value(params string[] keys) => keys.Select(key => details.GetValueOrDefault(key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        var maker = Value("Camera maker", "Embedded com.apple.quicktime.make", "Embedded com.android.manufacturer", "Embedded make");
        var model = Value("Camera model", "Embedded com.apple.quicktime.model", "Embedded com.android.model", "Embedded model");
        var camera = string.Join(" ", new[] { maker, model }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var taken = Value("Date taken", "Embedded creation_time", "Embedded com.apple.quicktime.creationdate", "Video creation_time");
        string? day = null;
        if (taken is not null && (DateTimeOffset.TryParse(taken, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            || DateTimeOffset.TryParse(taken, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed)))
            day = parsed.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (stem, number) = SplitName(Path.GetFileNameWithoutExtension(asset.Name));
        return new FileTraits
        {
            Kind = asset.Kind,
            Camera = camera.Length > 0 ? camera : null,
            Lens = Value("Lens"),
            Day = day,
            Folder = Path.GetDirectoryName(asset.FullPath) ?? "",
            NameStem = stem,
            NameNumber = number,
            Width = width,
            Height = height,
            FrameRate = Value("avg_frame_rate"),
            Codec = Value("codec_name"),
            Duration = duration,
            Visual = visual
        };
    }

    private static readonly Regex Numbered = new(@"^(?<stem>.*?)(?<number>\d{1,9})$", RegexOptions.CultureInvariant);

    public static (string? Stem, long? Number) SplitName(string name)
    {
        var match = Numbered.Match(name);
        return match.Success && match.Groups["stem"].Value.Length > 0
            ? (match.Groups["stem"].Value, long.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture))
            : (null, null);
    }
}

public sealed record TagSuggestion(string Tag, double Score, string Reason, int Files);

/// <summary>
/// Suggests tags for a file from files that are already tagged and resemble it. Each kind of resemblance has a weight; a tag's score
/// is the sum over the tagged files that carry it of their strongest resemblance, so one weak coincidence never suggests anything,
/// while a dozen files from the same camera on the same day do. The reason given is the strongest resemblance, in words.
/// </summary>
public static class TagSuggester
{
    public const double Threshold = 3;
    public const int MaximumSuggestions = 6;
    /// <summary>Look-alike fingerprints this close are treated as versions of the same picture.</summary>
    public const int SameLookDistance = 6;
    public const int SimilarLookDistance = 12;
    /// <summary>Numbered names this close are treated as one sequence, for example one burst or one card.</summary>
    public const int SequenceReach = 60;

    public static IReadOnlyList<TagSuggestion> Suggest(FileTraits file, IEnumerable<(FileTraits Traits, IReadOnlyCollection<string> Tags)> tagged, IEnumerable<string> alreadyOn)
    {
        var skip = new HashSet<string>(alreadyOn, StringComparer.OrdinalIgnoreCase);
        var scores = new Dictionary<string, (double Score, double Best, string Reason, int Files)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (other, tags) in tagged)
        {
            var (weight, reason) = Resemblance(file, other);
            if (weight <= 0) continue;
            foreach (var tag in tags)
            {
                if (skip.Contains(tag)) continue;
                var entry = scores.GetValueOrDefault(tag);
                scores[tag] = (entry.Score + weight, Math.Max(entry.Best, weight), weight > entry.Best ? reason : entry.Reason, entry.Files + 1);
            }
        }
        return scores
            .Where(pair => pair.Value.Score >= Threshold)
            .OrderByDescending(pair => pair.Value.Score).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumSuggestions)
            .Select(pair => new TagSuggestion(pair.Key, pair.Value.Score, pair.Value.Reason, pair.Value.Files))
            .ToList();
    }

    /// <summary>How strongly two files resemble each other, and why. Only the strongest reason counts, so one pair is never counted twice.</summary>
    public static (double Weight, string Reason) Resemblance(FileTraits file, FileTraits other)
    {
        var candidates = new List<(double, string)>();
        if (file.Visual is { } look && other.Visual is { } otherLook)
        {
            var distance = VisualHash.Distance(look, otherLook);
            if (distance <= SameLookDistance) candidates.Add((4, "looks the same"));
            else if (distance <= SimilarLookDistance) candidates.Add((2, "looks similar"));
        }
        var sameCamera = file.Camera is not null && string.Equals(file.Camera, other.Camera, StringComparison.OrdinalIgnoreCase);
        var sameDay = file.Day is not null && file.Day == other.Day;
        if (sameCamera && sameDay) candidates.Add((3, "same camera, same day"));
        else if (sameDay) candidates.Add((1.5, "same day"));
        else if (sameCamera) candidates.Add((0.75, "same camera"));
        if (file.NameStem is not null && string.Equals(file.NameStem, other.NameStem, StringComparison.OrdinalIgnoreCase)
            && file.NameNumber is { } number && other.NameNumber is { } otherNumber && Math.Abs(number - otherNumber) <= SequenceReach
            && string.Equals(file.Folder, other.Folder, StringComparison.OrdinalIgnoreCase))
            candidates.Add((2.5, "same numbered sequence"));
        else if (file.Folder.Length > 0 && string.Equals(file.Folder, other.Folder, StringComparison.OrdinalIgnoreCase))
            candidates.Add((1.5, "same folder"));
        if (file.Kind == other.Kind && file.Width > 0 && file.Width == other.Width && file.Height == other.Height)
        {
            var sameMotion = file.Kind != MediaKind.Video || file.FrameRate == other.FrameRate && file.Codec == other.Codec;
            var similarLength = file.Kind == MediaKind.Photo || other.Duration > 0 && Math.Abs(file.Duration - other.Duration) <= Math.Max(2, other.Duration * 0.25);
            if (sameMotion && similarLength) candidates.Add((1, file.Kind == MediaKind.Video ? "same resolution, frame rate and codec" : "same size"));
        }
        return candidates.Count == 0 ? (0, "") : candidates.MaxBy(candidate => candidate.Item1);
    }
}
