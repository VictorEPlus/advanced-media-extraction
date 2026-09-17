using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

internal static partial class DesktopSmokeTest
{
    private static async Task CheckWorkspaceAsync(MainViewModel model, MainWindow window, string dataDirectory, string mediaDirectory, AssetViewModel photo, AssetViewModel video, CancellationToken token)
    {
        model.InspectorTab = 0;
        Require(model.IsPhoto && !model.IsVideo && model.CanCopy && model.ExportLabel == "Export PNG", "Photos must expose image actions, not video controls.");
        Require(window.VideoTimeline.Visibility == Visibility.Collapsed && window.PlaybackButton.Visibility == Visibility.Collapsed, "Video controls should collapse for a photo.");
        Require(model.Metadata.Any(row => row.Name == "Aspect ratio" && row.Value == "16:9"), "Photo metadata should include the reduced aspect ratio.");
        Require(model.Metadata.Any(row => row.Name == "DPI"), "Native image metadata was not loaded.");
        Require(model.Metadata.All(row => !row.IsSelected), "Metadata tag candidates must start unconfirmed.");
        var beforeHash = SHA256.HashData(File.ReadAllBytes(photo.Asset.FullPath));
        var beforeExports = Directory.GetFiles(model.ExportDirectory).Length;
        model.CropSelection = new PixelCrop(10, 20, 80, 60);
        var cropped = model.BuildClipboardImage();
        Require(cropped.PixelWidth == 80 && cropped.PixelHeight == 60, "Clipboard crop dimensions are incorrect.");
        var source = (BitmapSource)model.PreviewImage!;
        var stride = (80 * source.Format.BitsPerPixel + 7) / 8;
        var expected = new byte[stride * 60];
        var actual = new byte[stride * 60];
        source.CopyPixels(new Int32Rect(10, 20, 80, 60), expected, stride, 0);
        cropped.CopyPixels(actual, stride, 0);
        Require(expected.SequenceEqual(actual), "Crop copying changed or shifted the source pixels.");
        Require(beforeHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(photo.Asset.FullPath))) && beforeExports == Directory.GetFiles(model.ExportDirectory).Length, "Cropping must not save or modify media.");
        model.IsCropping = true;
        Render(window, Path.Combine(dataDirectory, "workspace-photo-crop.png"));
        model.ResetCropCommand.Execute(null);
        Require(!model.HasCrop && model.BuildClipboardImage().PixelWidth == 640, "Reset crop should restore full-image copying.");

        model.TagText = "review, sunset";
        model.AddTagsCommand.Execute(null);
        Require(model.SelectedTags.Count == 2, "Manual tags were not saved.");
        var aspect = model.Metadata.Single(row => row.Name == "Aspect ratio");
        aspect.IsSelected = true;
        Require(model.SelectedTags.Count == 2, "Checking metadata must not automatically create tags.");
        model.ConfirmMetadataTagsCommand.Execute(null);
        Require(model.SelectedTags.Contains("aspect ratio:16:9") && !aspect.IsSelected, "Metadata tag confirmation failed.");
        model.CollectionName = "Review staging";
        model.CreateCollectionCommand.Execute(null);
        model.StageSelectedCommand.Execute(null);
        model.StageSelectedCommand.Execute(null);
        var collectionPath = model.SelectedCollection!.FilePath;
        var store = new CollectionStore();
        Require(store.Load(collectionPath).Paths.Length == 1, "Staging should deduplicate file references.");
        model.SelectedAsset = video;
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        Require(model.IsVideo && model.HasFrames && !model.IsPhoto && model.CanCopy, "Video actions were not restored.");
        Require(model.Metadata.Any(row => row.Name == "Video codec"), "Video metadata should expose its codec.");
        model.TagText = "sunset";
        model.AddTagsCommand.Execute(null);
        model.CropSelection = new PixelCrop(0, 0, 100, 50);
        Require(model.BuildClipboardImage().PixelWidth == 100, "A paused video frame should support clipboard cropping.");
        model.CurrentFrame = 1;
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        Require(model.HasCrop && model.CropSelection == new PixelCrop(0, 0, 100, 50) && model.DisplayedFrame == 1, "Stepping frames should keep a crop that still fits the same video.");
        model.ResetCropCommand.Execute(null);
        model.InspectorTab = 2;
        Render(window, Path.Combine(dataDirectory, "workspace-video.png"));
        Require(window.VideoTimeline.Visibility == Visibility.Visible && window.PlaybackButton.Visibility == Visibility.Visible, "Video timeline and playback controls should be visible.");
        model.StageSelectedCommand.Execute(null);
        await model.FindRelatedCommand.ExecuteAsync(null);
        Require(model.Assets.Count == 1 && model.Assets[0].Asset.FullPath == photo.Asset.FullPath, "Shared tags should find related media across sources.");
        await model.OpenCollectionCommand.ExecuteAsync(null);
        Require(model.Assets.Count == 2 && model.Assets.All(item => item.IsFavorite), "Collection browsing lost media or cross-root favorites.");
        model.SelectedAsset = model.Assets.Single(item => item.Asset.Kind == MediaKind.Photo);
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        Require(model.SelectedTags.Contains("review"), "Tags should follow a file into a collection.");
        await model.RemoveFromCollectionCommand.ExecuteAsync(null);
        Require(model.Assets.Count == 1 && File.Exists(photo.Asset.FullPath), "Removing a staged item must not delete its media.");
        var collection = store.Load(collectionPath);
        store.Save(collectionPath, CollectionStore.Add(collection, [Path.Combine(mediaDirectory, "missing.png")]));
        await model.OpenCollectionCommand.ExecuteAsync(null);
        Require(model.Assets.Count == 1 && model.Status.Contains("1 missing", StringComparison.Ordinal), "Missing collection paths should be reported and preserved.");
        Require(store.Load(collectionPath).Paths.Length == 2, "Loading a collection must not remove missing references.");
        await model.OpenLibraryAsync(mediaDirectory);
        model.SelectedAsset = model.Assets.Single(item => item.Asset.Kind == MediaKind.Photo);
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        model.InspectorTab = 1;
        Render(window, Path.Combine(dataDirectory, "workspace-tags.png"));
        model.InspectorTab = 3;
        Render(window, Path.Combine(dataDirectory, "workspace-settings.png"));
        await CheckFilmstripAsync(model, window, photo, dataDirectory, token);
        await CheckExifAsync(photo.Asset.FullPath, dataDirectory, token);
    }

    private static async Task CheckExifAsync(string sourcePath, string dataDirectory, CancellationToken token)
    {
        var image = ImageLoader.Load(sourcePath).Image;
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        metadata.SetQuery("/app1/ifd/{ushort=272}", "Smoke test camera");
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        var path = Path.Combine(dataDirectory, "rotated-exif.jpg");
        using (var stream = File.Create(path)) encoder.Save(stream);
        var preview = await Task.Run(() => ImageLoader.Load(path), token);
        Require(preview.Image.PixelWidth == 360 && preview.Image.PixelHeight == 640, "JPEG EXIF orientation must be applied to the preview.");
        Require(preview.Metadata.GetValueOrDefault("Camera model") == "Smoke test camera", "Available EXIF camera metadata must be surfaced.");
        var thumbnail = await Task.Run(() => ImageLoader.Thumbnail(path, 220), token);
        Require(thumbnail.PixelHeight > thumbnail.PixelWidth, "Filmstrip thumbnail orientation must match the preview.");
        var crop = new CroppedBitmap(preview.Image, new Int32Rect(0, 0, 10, 10));
        crop.Freeze();
        var png = await Task.Run(() => ImageLoader.Encode(crop), token);
        Require(png.Length > 0, "Oriented crop must be usable outside its decoding thread.");
        var tallPath = Path.Combine(dataDirectory, "extremely-tall.png");
        var tall = BitmapSource.Create(1, 4096, 96, 96, PixelFormats.Bgra32, null, new byte[4096 * 4], 4);
        File.WriteAllBytes(tallPath, ImageLoader.Encode(tall));
        var tallThumbnail = ImageLoader.Thumbnail(tallPath, 220);
        Require(tallThumbnail.PixelWidth <= 220 && tallThumbnail.PixelHeight <= 220, "Extreme image aspect ratios must not exceed thumbnail memory bounds.");
    }

    private static async Task CheckFilmstripAsync(MainViewModel model, MainWindow window, AssetViewModel photo, string dataDirectory, CancellationToken token)
    {
        model.ClearFiltersCommand.Execute(null);
        model.SelectedAsset = null;
        model.Assets.Clear();
        model.Assets.AddRange(Enumerable.Range(1, 5000).Reverse().Select(index => new AssetViewModel(photo.Asset with { RelativePath = $"frame{index}.png" }, false)));
        model.SortMethod = "Name (natural)";
        var view = (System.Windows.Data.ListCollectionView)model.LibraryView;
        Require(((AssetViewModel)view.GetItemAt(1)).Name == "frame2.png" && ((AssetViewModel)view.GetItemAt(9)).Name == "frame10.png", "Frame sequence sorting must be natural, not lexicographic.");
        model.SortMethod = "Name (reverse)";
        Require(((AssetViewModel)view.GetItemAt(0)).Name == "frame5000.png", "Reverse sort failed.");
        model.SortMethod = "Name (natural)";
        model.InspectorTab = 0;
        Render(window, Path.Combine(dataDirectory, "workspace-filmstrip-5000.png"));
        Require(CountRealized() is > 0 and < 100, "The filmstrip should realize only a small viewport of 5,000 items.");
        window.Filmstrip.ScrollIntoView(view.GetItemAt(4000));
        window.Filmstrip.UpdateLayout();
        Require(CountRealized() is > 0 and < 100, "Scrolling should retain container virtualization.");
        model.ShowSources = false;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1050, 660));
        content.Arrange(new Rect(0, 0, 1050, 660));
        content.UpdateLayout();
        Require(window.PreviewSurface.ActualWidth > 600 && window.PreviewSurface.ActualHeight > 200, "Compact layout should reclaim space when Sources is collapsed.");
        foreach (var button in VisualChildren(content).OfType<Button>().Where(button => button.ActualHeight > 0 && button.Visibility == Visibility.Visible && button.Style is not null))
            if (button.Command is not null) Require(Math.Abs(button.ActualHeight - 32) < 0.1, "Action button heights should be consistent.");

        var loader = new ThumbnailLoader();
        var engine = new MediaEngine(new ToolPaths("nonexistent-ffmpeg", "nonexistent-ffprobe"), Path.Combine(dataDirectory, "thumbnail-test"));
        for (var index = 0; index < 104; index++)
        {
            var thumbnail = await loader.LoadAsync(photo.Asset with { ModifiedTicks = index }, engine, token);
            Require(thumbnail is { IsFrozen: true, PixelWidth: <= 220, PixelHeight: <= 220 }, "PNG thumbnails must use native downscaled decoding without FFmpeg.");
        }
        Require(loader.CachedCount == ThumbnailLoader.Capacity, "The thumbnail LRU must stay bounded.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await loader.LoadAsync(photo.Asset with { ModifiedTicks = -1 }, engine, cancelled.Token);
            throw new InvalidOperationException("Cancelled offscreen thumbnail work should not start decoding.");
        }
        catch (OperationCanceledException) { }
        return;

        int CountRealized() => VisualChildren(window.Filmstrip).OfType<ListBoxItem>().Count();
    }
}
