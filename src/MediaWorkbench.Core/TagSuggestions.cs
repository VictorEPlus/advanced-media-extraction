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

/// <summary>A tag worth adding, the strongest reason, how many resembling files carry it and how many resembling files there were.</summary>
public sealed record TagSuggestion(string Tag, double Score, string Reason, int Files, int Similar = 0)
{
    /// <summary>The reason in words, with the counts behind it: "3 of 4 similar files, same day".</summary>
    public string Why => Similar > 0 ? $"{Files} of {Similar} similar files, {Reason}" : $"{Files} tagged, {Reason}";
}

/// <summary>
/// Suggests tags for a file from tagged files that resemble it. Every tagged file that resembles it adds evidence, weighted by how
/// strong the resemblance is (only its strongest reason counts). A tag is suggested when a real share of that evidence carries it
/// and it is clearly more common among the resembling files than among the rest, so a tag that is simply used a lot is not
/// pushed onto everything, and weak coincidences (same resolution, same camera on another day) never suggest anything alone.
/// </summary>
public static class TagSuggester
{
    /// <summary>The least evidence for a tag: two files on the same day, say, or three in the same folder.</summary>
    public const double Threshold = 2.5;
    /// <summary>The least share of the resembling files' evidence that must carry the tag.</summary>
    public const double MinimumShare = 0.35;
    /// <summary>How much more common the tag must be among the resembling files than among the other tagged files.</summary>
    public const double MinimumLift = 1.5;
    public const int MaximumSuggestions = 6;
    /// <summary>Look-alike fingerprints this close are treated as versions of the same picture: one such file is enough.</summary>
    public const int SameLookDistance = 6;
    public const int SimilarLookDistance = 10;
    public const double SameLookWeight = 4;
    /// <summary>
    /// Up to this many tagged near copies, each one's tags are suggested on their own strength. More files than this looking the same
    /// means the look is a common one (the same app on screen, a plain sky), so it only counts as ordinary evidence.
    /// </summary>
    public const int FewNearCopies = 3;
    private const string SameLookReason = "looks the same";
    /// <summary>Numbered names this close are treated as one sequence, for example one burst or one card.</summary>
    public const int SequenceReach = 60;

    public static IReadOnlyList<TagSuggestion> Suggest(FileTraits file, IEnumerable<(FileTraits Traits, IReadOnlyCollection<string> Tags)> tagged, IEnumerable<string> alreadyOn)
    {
        var skip = new HashSet<string>(alreadyOn, StringComparer.OrdinalIgnoreCase);
        var all = tagged.Select(entry => (entry.Traits, Tags: entry.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Match: Resemblance(file, entry.Traits))).ToList();
        var resembling = all.Where(entry => entry.Match.Weight > 0).ToList();
        var evidence = resembling.Sum(entry => entry.Match.Weight);
        if (evidence <= 0) return [];
        var everywhere = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in all.SelectMany(entry => entry.Tags)) everywhere[tag] = everywhere.GetValueOrDefault(tag) + 1;
        var scores = new Dictionary<string, (double Support, double Best, string Reason, int Files)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, tags, (weight, reason)) in resembling)
            foreach (var tag in tags)
            {
                if (skip.Contains(tag)) continue;
                var entry = scores.GetValueOrDefault(tag);
                scores[tag] = (entry.Support + weight, Math.Max(entry.Best, weight), weight > entry.Best ? reason : entry.Reason, entry.Files + 1);
            }
        var others = all.Count - resembling.Count;
        var nearCopies = resembling.Where(entry => entry.Match.Reason == SameLookReason).ToList();
        var fromNearCopy = nearCopies.Count is > 0 and <= FewNearCopies
            ? nearCopies.SelectMany(entry => entry.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var suggestions = new List<TagSuggestion>();
        foreach (var (tag, (support, best, reason, files)) in scores)
        {
            var share = support / evidence;
            // Among the tagged files that do not resemble this one, how often the tag turns up; with none to compare, it passes.
            var elsewhere = others == 0 ? 0 : (everywhere[tag] - files) / (double)others;
            var enoughFiles = files >= 2 || best >= SameLookWeight;
            if (fromNearCopy.Contains(tag)
                || enoughFiles && support >= Threshold && share >= MinimumShare && (others == 0 || share >= elsewhere * MinimumLift))
                suggestions.Add(new TagSuggestion(tag, support * share, reason, files, resembling.Count));
        }
        return suggestions.OrderByDescending(suggestion => suggestion.Score).ThenBy(suggestion => suggestion.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumSuggestions).ToList();
    }

    /// <summary>
    /// How strongly two files resemble each other, and why. Only the strongest reason counts, so one pair is never counted twice.
    /// Things many unrelated files share (a resolution, a codec, a camera on another day) are not reasons on their own.
    /// </summary>
    public static (double Weight, string Reason) Resemblance(FileTraits file, FileTraits other)
    {
        var candidates = new List<(double, string)>();
        if (file.Visual is { } look && other.Visual is { } otherLook)
        {
            var distance = VisualHash.Distance(look, otherLook);
            if (distance <= SameLookDistance) candidates.Add((SameLookWeight, SameLookReason));
            else if (distance <= SimilarLookDistance) candidates.Add((1.5, "looks similar"));
        }
        var sameCamera = file.Camera is not null && string.Equals(file.Camera, other.Camera, StringComparison.OrdinalIgnoreCase);
        var sameDay = file.Day is not null && file.Day == other.Day;
        if (sameCamera && sameDay) candidates.Add((3, "same camera, same day"));
        else if (sameDay) candidates.Add((1.5, "same day"));
        if (file.NameStem is not null && string.Equals(file.NameStem, other.NameStem, StringComparison.OrdinalIgnoreCase)
            && file.NameNumber is { } number && other.NameNumber is { } otherNumber && Math.Abs(number - otherNumber) <= SequenceReach
            && string.Equals(file.Folder, other.Folder, StringComparison.OrdinalIgnoreCase))
            candidates.Add((2.5, "same numbered sequence"));
        else if (file.Folder.Length > 0 && string.Equals(file.Folder, other.Folder, StringComparison.OrdinalIgnoreCase))
            candidates.Add((1, "same folder"));
        return candidates.Count == 0 ? (0, "") : candidates.MaxBy(candidate => candidate.Item1);
    }
}
