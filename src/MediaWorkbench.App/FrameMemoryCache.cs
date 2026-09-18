using System.Windows.Media.Imaging;

namespace MediaWorkbench.App;

/// <summary>
/// Decoded frames of the selected video kept in memory so stepping to a neighbouring frame is instant: no process, no disk read
/// and no PNG decode. Bounded by decoded pixel bytes and entry count; least recently used frames are dropped first.
/// Clearing bumps a generation so late background results for a previous video are ignored.
/// </summary>
internal sealed class FrameMemoryCache
{
    public const long ByteBudget = 384L << 20;
    public const int MaximumEntries = 96;

    private readonly Dictionary<int, (byte[] Bytes, BitmapSource Image, long Size, LinkedListNode<int> Node)> items = new();
    private readonly LinkedList<int> recent = new();
    private readonly object gate = new();
    private long bytes;

    public int Generation { get; private set; }
    public int Count { get { lock (gate) return items.Count; } }

    /// <summary>How many frames of this size fit in the budget; used to size the read-ahead.</summary>
    public static int CapacityFor(BitmapSource image) => (int)Math.Clamp(ByteBudget / Math.Max(1, (long)image.PixelWidth * image.PixelHeight * 4), 4, MaximumEntries);

    public bool Contains(int index) { lock (gate) return items.ContainsKey(index); }

    public bool TryGet(int index, out byte[] frameBytes, out BitmapSource image)
    {
        lock (gate)
        {
            if (items.TryGetValue(index, out var entry))
            {
                recent.Remove(entry.Node);
                recent.AddFirst(entry.Node);
                frameBytes = entry.Bytes;
                image = entry.Image;
                return true;
            }
        }
        frameBytes = [];
        image = null!;
        return false;
    }

    public void Add(int generation, int index, byte[] frameBytes, BitmapSource image)
    {
        lock (gate)
        {
            if (generation != Generation || items.ContainsKey(index)) return;
            var size = (long)image.PixelWidth * image.PixelHeight * 4 + frameBytes.Length;
            items[index] = (frameBytes, image, size, recent.AddFirst(index));
            bytes += size;
            while ((bytes > ByteBudget || items.Count > MaximumEntries) && recent.Last is { } oldest && oldest.Value != index)
            {
                bytes -= items[oldest.Value].Size;
                items.Remove(oldest.Value);
                recent.RemoveLast();
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            Generation++;
            items.Clear();
            recent.Clear();
            bytes = 0;
        }
    }
}
