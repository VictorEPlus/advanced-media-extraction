using Avalonia.Media.Imaging;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia;

/// <summary>
/// Thumbnails for the filmstrip and folder covers, two at a time, with the last <see cref="Capacity"/> kept in memory. Photos are
/// decoded at thumbnail size and upright; videos use the engine's cached thumbnail.
/// </summary>
internal sealed class ThumbnailLoader
{
    private readonly SemaphoreSlim workers = new(2);
    private readonly Dictionary<string, (Bitmap Image, LinkedListNode<string> Node)> cache = new();
    private readonly LinkedList<string> recent = new();
    private readonly object cacheLock = new();
    public const int Capacity = 96;
    public int CachedCount { get { lock (cacheLock) return cache.Count; } }

    public async Task<Bitmap?> LoadAsync(MediaAsset asset, MediaEngine engine, CancellationToken token)
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
            Bitmap? image = null;
            if (asset.Kind == MediaKind.Photo)
            {
                try { image = await Task.Run(() => ImageLoader.Thumbnail(asset.FullPath, 220), token); }
                catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException or InvalidOperationException) { }
            }
            image ??= await Task.Run(async () => Pixels.Decode(await engine.GetThumbnailAsync(asset, token)), token);
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
