namespace MediaWorkbench.Core;

/// <summary>One small picture of a video, and the decoded frame ordinal it really shows.</summary>
public sealed record PreviewThumbnail(int Frame, byte[] Jpeg);

/// <summary>
/// Small pictures spread along a video for the hover preview on the timeline. They are a browsing aid: each one knows
/// which frame it shows, and the caller says so when that is not the frame under the pointer. Exports never use them.
/// </summary>
public sealed class PreviewStrip(IReadOnlyList<PreviewThumbnail> thumbnails)
{
    private const int Magic = 0x31505453; // "STP1"

    public IReadOnlyList<PreviewThumbnail> Thumbnails { get; } = [.. thumbnails.OrderBy(thumbnail => thumbnail.Frame)];

    /// <summary>Position in <see cref="Thumbnails"/> of the picture closest to <paramref name="frame"/>, or -1 when there are none.</summary>
    public int NearestIndex(int frame) => NearestIndex(Thumbnails.Count, index => Thumbnails[index].Frame, frame);

    /// <summary>Binary search over ascending values for the position whose value is closest to <paramref name="target"/>; ties go to the earlier one.</summary>
    public static int NearestIndex(int count, Func<int, double> valueAt, double target)
    {
        if (count == 0) return -1;
        var low = 0;
        var high = count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (valueAt(middle) < target) low = middle + 1;
            else high = middle;
        }
        return low > 0 && target - valueAt(low - 1) <= valueAt(low) - target ? low - 1 : low;
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(Thumbnails.Count);
            foreach (var thumbnail in Thumbnails)
            {
                writer.Write(thumbnail.Frame);
                writer.Write(thumbnail.Jpeg.Length);
                writer.Write(thumbnail.Jpeg);
            }
        }
        return stream.ToArray();
    }

    public static PreviewStrip? FromBytes(byte[] bytes)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes));
            if (reader.ReadInt32() != Magic) return null;
            var count = reader.ReadInt32();
            if (count is < 0 or > 4096) return null;
            var thumbnails = new List<PreviewThumbnail>(count);
            for (var index = 0; index < count; index++)
            {
                var frame = reader.ReadInt32();
                var length = reader.ReadInt32();
                if (frame < 0 || length <= 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) return null;
                thumbnails.Add(new PreviewThumbnail(frame, reader.ReadBytes(length)));
            }
            return reader.BaseStream.Position == reader.BaseStream.Length ? new PreviewStrip(thumbnails) : null;
        }
        catch (EndOfStreamException) { return null; }
    }
}
