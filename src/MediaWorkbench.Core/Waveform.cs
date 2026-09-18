namespace MediaWorkbench.Core;

/// <summary>
/// Loudness outline of one audio track: the lowest and highest sample in each bucket of <see cref="BucketSeconds"/>,
/// scaled to -1..1. It is a picture of the sound, not the sound itself; exports always read the source file.
/// </summary>
public sealed record Waveform(float[] Minimum, float[] Maximum, double BucketSeconds)
{
    public const int SampleRate = 8000;
    /// <summary>Upper bound on stored buckets; long files get proportionally wider buckets.</summary>
    public const int MaximumBuckets = 120_000;

    public int Count => Minimum.Length;
    public double Duration => Count * BucketSeconds;

    /// <summary>Samples per bucket: 100 buckets a second for ordinary files, fewer for very long ones.</summary>
    public static int SamplesPerBucket(double durationSeconds)
    {
        var samples = SampleRate / 100;
        var expected = Math.Max(0, durationSeconds) * SampleRate / samples;
        return expected <= MaximumBuckets ? samples : (int)Math.Ceiling(Math.Max(0, durationSeconds) * SampleRate / MaximumBuckets);
    }

    /// <summary>Reduces signed 16-bit little-endian mono PCM to bucket extremes.</summary>
    public static async Task<Waveform> FromPcmAsync(Stream pcm, int samplesPerBucket, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samplesPerBucket, 1);
        var minimum = new List<float>();
        var maximum = new List<float>();
        var buffer = new byte[64 * 1024];
        var carry = -1;
        var low = short.MaxValue;
        var high = short.MinValue;
        var filled = 0;
        int read;
        while ((read = await pcm.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var offset = 0;
            if (carry >= 0)
            {
                Add((short)(carry | buffer[0] << 8));
                carry = -1;
                offset = 1;
            }
            for (; offset + 1 < read; offset += 2)
                Add((short)(buffer[offset] | buffer[offset + 1] << 8));
            if (offset < read)
                carry = buffer[offset];
        }
        if (filled > 0)
            Flush();
        return new Waveform([.. minimum], [.. maximum], samplesPerBucket / (double)SampleRate);

        void Add(short sample)
        {
            if (sample < low) low = sample;
            if (sample > high) high = sample;
            if (++filled == samplesPerBucket)
                Flush();
        }

        void Flush()
        {
            minimum.Add(low / 32768f);
            maximum.Add(high / 32768f);
            low = short.MaxValue;
            high = short.MinValue;
            filled = 0;
        }
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x31564157); // "WAV1"
            writer.Write(BucketSeconds);
            writer.Write(Count);
            for (var index = 0; index < Count; index++)
            {
                writer.Write((sbyte)Math.Clamp(Math.Round(Minimum[index] * 127), -127, 127));
                writer.Write((sbyte)Math.Clamp(Math.Round(Maximum[index] * 127), -127, 127));
            }
        }
        return stream.ToArray();
    }

    public static Waveform? FromBytes(byte[] bytes)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes));
            if (reader.ReadInt32() != 0x31564157)
                return null;
            var bucketSeconds = reader.ReadDouble();
            var count = reader.ReadInt32();
            if (!(bucketSeconds > 0) || count < 0 || count > MaximumBuckets * 2 || bytes.Length != 16 + count * 2L)
                return null;
            var minimum = new float[count];
            var maximum = new float[count];
            for (var index = 0; index < count; index++)
            {
                minimum[index] = reader.ReadSByte() / 127f;
                maximum[index] = reader.ReadSByte() / 127f;
            }
            return new Waveform(minimum, maximum, bucketSeconds);
        }
        catch (EndOfStreamException) { return null; }
    }
}

/// <summary>Frame rate of a video, measured from the indexed frame timestamps when they are available.</summary>
public sealed record FrameRateInfo(double FramesPerSecond, bool IsVariable, bool IsMeasured)
{
    /// <summary>
    /// Measures the rate from the frame times. Container timestamps are rounded (often to 1 ms), which would turn 29.97 into 29.969 on a short clip,
    /// so when the frames are evenly spaced and agree with the rate in the file header to within half a percent, the exact header rate is reported.
    /// </summary>
    public static FrameRateInfo? FromFrames(IReadOnlyList<VideoFrame> frames, FrameRateInfo? header = null)
    {
        if (frames.Count < 2)
            return null;
        var span = frames[^1].Time - frames[0].Time;
        if (!(span > 0))
            return null;
        var average = span / (frames.Count - 1);
        var variable = false;
        for (var index = 1; index < frames.Count && !variable; index++)
        {
            var gap = frames[index].Time - frames[index - 1].Time;
            // Container timestamps are rounded (often to 1 ms), so only a gap that is clearly off the average counts as variable.
            variable = Math.Abs(gap - average) > Math.Max(0.0015, average * 0.2);
        }
        var measured = (frames.Count - 1) / span;
        if (!variable && header is not null && Math.Abs(header.FramesPerSecond - measured) <= measured * 0.005)
            measured = header.FramesPerSecond;
        return new FrameRateInfo(measured, variable, true);
    }

    public static FrameRateInfo? FromRatio(string? ratio)
    {
        var parts = ratio?.Split('/');
        if (parts is not { Length: 2 }
            || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var denominator)
            || !(denominator > 0) || !(numerator > 0))
            return null;
        return new FrameRateInfo(numerator / denominator, false, false);
    }

    /// <summary>"29.97", "60", "23.976": up to three decimals, trailing zeros dropped.</summary>
    public string Number => FramesPerSecond.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
