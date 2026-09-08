using System.IO;
using System.Windows.Media.Imaging;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

internal sealed class ThumbnailLoader
{
    private readonly SemaphoreSlim workers = new(2);
    private readonly Dictionary<string, (BitmapSource Image, LinkedListNode<string> Node)> cache = new();
    private readonly LinkedList<string> recent = new();
    private readonly object cacheLock = new();
    public const int Capacity = 96;
    public int CachedCount { get { lock (cacheLock) return cache.Count; } }

    public async Task<BitmapSource?> LoadAsync(MediaAsset asset, MediaEngine engine, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (asset.Kind == MediaKind.Audio) return null;
        lock (cacheLock)
        {
            if (cache.TryGetValue(asset.Identity, out var entry))
            {
                recent.Remove(entry.Node);
                recent.AddFirst(entry.Node);
                return entry.Image;
            }
        }
        await workers.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            BitmapSource? image = null;
            if (asset.Kind == MediaKind.Photo)
            {
                try { image = await Task.Run(() => ImageLoader.Thumbnail(asset.FullPath, 220), token); }
                catch (Exception exception) when (exception is IOException or NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException or ArgumentException) { }
            }
            image ??= MainViewModel.DecodeImage(await engine.GetThumbnailAsync(asset, token));
            token.ThrowIfCancellationRequested();
            lock (cacheLock)
            {
                if (cache.Remove(asset.Identity, out var existing)) recent.Remove(existing.Node);
                cache[asset.Identity] = (image, recent.AddFirst(asset.Identity));
                while (cache.Count > Capacity && recent.Last is { } oldest)
                {
                    cache.Remove(oldest.Value);
                    recent.RemoveLast();
                }
            }
            return image;
        }
        finally { workers.Release(); }
    }
}
