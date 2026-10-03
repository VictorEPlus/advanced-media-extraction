using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia;

// Resting the pointer on a video's thumbnail plays five pictures from it, first to last at even steps, over the thumbnail.
public partial class MainWindow
{
    /// <summary>A pointer just passing over a thumbnail does not start anything.</summary>
    internal static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(250);
    /// <summary>How long each of the five pictures shows.</summary>
    internal static readonly TimeSpan HoverStepTime = TimeSpan.FromMilliseconds(550);

    private AssetViewModel? hovered;
    private CancellationTokenSource? hoverWork;
    private DispatcherTimer? hoverTimer;
    private Bitmap[] hoverFrames = [];
    private int hoverStep;

    private void SetUpHoverPreview()
    {
        Filmstrip.AddHandler(PointerMovedEvent, (_, args) =>
            HoverOver((args.Source as global::Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as AssetViewModel),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Filmstrip.PointerExited += (_, _) => HoverOver(null);
        // Scrolling moves other thumbnails under a still pointer: stop, and start again when the pointer moves.
        Filmstrip.AddHandler(PointerWheelChangedEvent, (_, _) => HoverOver(null), RoutingStrategies.Tunnel, handledEventsToo: true);
        Filmstrip.ContainerClearing += (_, args) => { if (ReferenceEquals(args.Container.DataContext, hovered)) HoverOver(null); };
    }

    /// <summary>The pointer is now over <paramref name="item"/> (or over no thumbnail): stop the old preview and, for a video, start its own.</summary>
    internal async void HoverOver(AssetViewModel? item)
    {
        if (ReferenceEquals(item, hovered)) return;
        StopHover();
        if (item is not { Asset.Kind: MediaKind.Video }) return;
        hovered = item;
        var work = hoverWork = new CancellationTokenSource();
        try
        {
            await Task.Delay(HoverDelay, work.Token);
            var frames = await viewModel.HoverFramesAsync(item, work.Token);
            if (work.IsCancellationRequested || frames.Length == 0) return;
            hoverFrames = frames;
            hoverStep = 0;
            item.HoverFrame = frames[0];
            hoverTimer ??= new DispatcherTimer(HoverStepTime, DispatcherPriority.Background, (_, _) => NextHoverFrame());
            hoverTimer.Start();
        }
        catch (OperationCanceledException) { }
        // A video FFmpeg cannot read just keeps its thumbnail; the preview is a nicety, never an error.
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private void NextHoverFrame()
    {
        if (hovered is null || hoverFrames.Length == 0) return;
        hoverStep = (hoverStep + 1) % hoverFrames.Length;
        hovered.HoverFrame = hoverFrames[hoverStep];
    }

    private void StopHover()
    {
        hoverWork?.Cancel();
        hoverWork = null;
        hoverTimer?.Stop();
        if (hovered is not null) hovered.HoverFrame = null;
        hovered = null;
        hoverFrames = [];
    }

    /// <summary>For checks: the picture the hover preview shows now, and which of the five it is.</summary>
    internal (AssetViewModel? Item, int Step, int Count) HoverState => (hovered, hoverStep, hoverFrames.Length);
}
