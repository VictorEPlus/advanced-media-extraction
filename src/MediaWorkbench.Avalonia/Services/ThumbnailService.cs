using Avalonia.Media.Imaging;
using MediaWorkbench.Avalonia.ViewModels;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia.Services;

/// <summary>
/// Makes filmstrip thumbnails, three at a time. Photos are decoded straight from the file at thumbnail size; videos use the
/// engine's cached thumbnail (FFmpeg). Only items that are on screen ask (their thumbnail is fetched the first time a card
/// binds to it), and at most <see cref="Capacity"/> stay in memory: the least recently shown give theirs back and fetch again
/// if they scroll into view later.
/// </summary>
public sealed class ThumbnailService(MediaEngine engine)
{
    public const int Capacity = 360;
    public const int DecodeWidth = 360;

    private readonly SemaphoreSlim workers = new(3);
    private readonly LinkedList<MediaItemViewModel> recent = new();
    private readonly Dictionary<MediaItemViewModel, LinkedListNode<MediaItemViewModel>> nodes = new();

    public async Task<Bitmap?> LoadAsync(MediaItemViewModel item, CancellationToken token)
    {
        if (item.Asset.Kind == MediaKind.Audio)
            return null;
        await workers.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var asset = item.Asset;
            return await Task.Run(async () =>
            {
                if (asset.Kind == MediaKind.Photo)
                {
                    try
                    {
                        await using var stream = File.OpenRead(asset.FullPath);
                        return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.MediumQuality);
                    }
                    // Formats Avalonia cannot decode itself (HEIC, AVIF, some TIFFs) go through FFmpeg like video.
                    catch (Exception exception) when (exception is not OperationCanceledException) { }
                }
                var bytes = await engine.GetThumbnailAsync(asset, token);
                using var memory = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(memory, DecodeWidth, BitmapInterpolationMode.MediumQuality);
            }, token);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception) { return null; }
        finally { workers.Release(); }
    }

    /// <summary>Called on the UI thread when an item shows its thumbnail. Returns items that should give theirs back.</summary>
    public IReadOnlyList<MediaItemViewModel> Touch(MediaItemViewModel item)
    {
        if (nodes.Remove(item, out var existing))
            recent.Remove(existing);
        nodes[item] = recent.AddFirst(item);
        List<MediaItemViewModel>? evicted = null;
        while (nodes.Count > Capacity && recent.Last is { } oldest)
        {
            recent.RemoveLast();
            nodes.Remove(oldest.Value);
            (evicted ??= []).Add(oldest.Value);
        }
        return evicted ?? (IReadOnlyList<MediaItemViewModel>)[];
    }
}
