using Avalonia.Media.Imaging;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia;

// Resting the pointer on a video in the filmstrip shows five pictures from it, from its first moment to its last at even steps,
// so what a clip is about can be seen without opening it.
public sealed partial class MainViewModel
{
    internal const int HoverFrameCount = 5;
    private const int HoverCacheSize = 16;
    private readonly Dictionary<string, double> durations = [];
    private readonly LinkedList<(string Identity, Bitmap[] Frames)> hoverCache = new();

    /// <summary>The five preview pictures of a video, decoded and small, or none for photos, sounds and unreadable files.</summary>
    internal async Task<Bitmap[]> HoverFramesAsync(AssetViewModel item, CancellationToken token)
    {
        if (item.Asset.Kind != MediaKind.Video) return [];
        var identity = item.Asset.Identity;
        for (var node = hoverCache.First; node is not null; node = node.Next)
            if (node.Value.Identity == identity)
            {
                hoverCache.Remove(node);
                hoverCache.AddFirst(node);
                return node.Value.Frames;
            }
        var source = engine;
        if (!durations.TryGetValue(identity, out var duration))
        {
            duration = (await source.ProbeAsync(item.Asset.FullPath, token)).Duration;
            durations[identity] = duration;
        }
        // Asked for together; the engine lets two run at once, so the five arrive in about the time of three.
        var result = await Task.WhenAll(MediaEngine.PreviewTimes(duration, HoverFrameCount).Select(async time =>
        {
            var bytes = await source.GetPreviewFrameAsync(item.Asset, time, token);
            return await Task.Run(() => Pixels.Decode(bytes), token);
        }));
        hoverCache.AddFirst((identity, result));
        while (hoverCache.Count > HoverCacheSize) hoverCache.RemoveLast();
        return result;
    }
}
