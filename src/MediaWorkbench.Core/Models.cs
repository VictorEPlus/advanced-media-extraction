using System.Globalization;

namespace MediaWorkbench.Core;

public enum MediaKind { Photo, Video, Audio }

public sealed record MediaAsset(string Root, string RelativePath, MediaKind Kind, long Length, long ModifiedTicks)
{
    public string FullPath => Path.Combine(Root, RelativePath);
    public string Name => Path.GetFileName(RelativePath);
    public string Identity => $"{FullPath}|{Length}|{ModifiedTicks}";
}

public sealed record AudioTrack(int StreamIndex, int Channels, string Label);
public sealed record MediaInfo(double Duration, double StartTime, bool HasVideo, IReadOnlyList<AudioTrack> AudioTracks)
{
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
public sealed record VideoFrame(int Index, double Time, double Duration);
public sealed record TimeRange(double Start, double End)
{
    public double Duration => End - Start;

    public void Validate(double duration)
    {
        if (!double.IsFinite(Start) || !double.IsFinite(End) || Start < 0 || End <= Start || End > duration + 0.001)
            throw new ArgumentException("Choose a range with 0 <= start < end <= duration.");
    }
}

public sealed record FrameRange(int Start, int EndInclusive)
{
    public int Count => EndInclusive - Start + 1;

    public void Validate(int frameCount)
    {
        if (Start < 0 || EndInclusive < Start || EndInclusive >= frameCount)
            throw new ArgumentException("Choose frame indices with 0 <= in <= out < frame count.");
    }

    public TimeRange ToTimeRange(IReadOnlyList<VideoFrame> frames, double duration)
    {
        Validate(frames.Count);
        var last = frames[EndInclusive];
        var end = EndInclusive + 1 < frames.Count
            ? frames[EndInclusive + 1].Time
            : last.Duration > 0 ? last.Time + last.Duration : duration;
        var range = new TimeRange(frames[Start].Time, Math.Min(end, duration));
        range.Validate(duration);
        return range;
    }
}

public sealed record ExportProgress(double Fraction, string Message);

public static class MediaNumber
{
    public static string Format(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
