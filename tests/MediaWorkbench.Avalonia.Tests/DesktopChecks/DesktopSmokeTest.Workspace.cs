using System.Diagnostics;
using System.Security.Cryptography;
using SkiaSharp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia.Tests;

internal static partial class DesktopSmokeTest
{
    private static async Task CheckWorkspaceAsync(MainViewModel model, MainWindow window, string dataDirectory, string mediaDirectory, AssetViewModel photo, AssetViewModel video, CancellationToken token)
    {
        model.InspectorTab = 0;
        Require(model.IsPhoto && !model.IsVideo && model.CanCopy && model.ExportLabel == "Export PNG", "Photos must expose image actions, not video controls.");
        // Hidden, not collapsed: their space is kept so the picture is the same size for a photo as for a video.
        Require(window.VideoTimeline.IsVisible == false && IsHidden(window.TransportControls), "Video controls should be hidden for a photo, keeping their space.");
        Require(model.Metadata.Any(row => row.Name == "Aspect ratio" && row.Value == "16:9"), "Photo metadata should include the reduced aspect ratio.");
        Require(model.Metadata.Any(row => row.Name == "DPI"), "Native image metadata was not loaded.");
        Require(model.Metadata.All(row => !row.IsSelected), "Metadata tag candidates must start unconfirmed.");
        // Open in Explorer selects the file in its own folder; the header button is there for every file.
        Require(MainViewModel.ExplorerSelectArguments(photo.Asset.FullPath) == $"/select,\"{photo.Asset.FullPath}\"" && window.OpenInExplorerButton.IsVisible
            && model.RevealAssetCommand.CanExecute(photo) && model.RevealFileCommand.CanExecute(null), "Open in Explorer should be available for the open file.");
        var beforeHash = SHA256.HashData(File.ReadAllBytes(photo.Asset.FullPath));
        var beforeExports = Directory.GetFiles(model.ExportDirectory).Length;
        model.CropSelection = new PixelCrop(10, 20, 80, 60);
        var cropped = model.BuildClipboardImage();
        Require(cropped.PixelWidth == 80 && cropped.PixelHeight == 60, "Clipboard crop dimensions are incorrect.");
        // Copy saves the crop as a file too, and offers the picture, a PNG and the file, so it pastes into Explorer as well as into apps.
        var (copied, copiedPath) = await model.ExportForClipboardAsync();
        var saved = ImageLoader.Load(copiedPath).Image;
        Require(File.Exists(copiedPath) && saved.PixelWidth == 80 && saved.PixelHeight == 60 && Path.GetFileName(copiedPath).Contains("_crop_80x60")
            && ((IDataTransfer)copied).Contains(DataFormat.Bitmap) && ((IDataTransfer)copied).Contains(MainViewModel.PngFormat) && ((IDataTransfer)copied).Contains(DataFormat.File),
            "Copy should save the crop as a PNG and put the picture, the PNG and the saved file on the clipboard: " + copiedPath);
        var source = Pixels.Bytes(model.PreviewImage!, out var sourceStride);
        var actual = Pixels.Bytes(cropped, out var stride);
        var expected = Enumerable.Range(20, 60).SelectMany(row => source.Skip(row * sourceStride + 10 * 4).Take(stride)).ToArray();
        Require(expected.SequenceEqual(actual), "Crop copying changed or shifted the source pixels.");
        Require(beforeHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(photo.Asset.FullPath))) && beforeExports + 1 == Directory.GetFiles(model.ExportDirectory).Length,
            "Cropping and copying must leave the original untouched; Copy saves exactly one new file.");
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
        var collectionPath = model.SelectedCollection!.FilePath;
        var store = new CollectionStore();
        Require(store.Load(collectionPath).Paths.Length == 1 && model.FileCollections.Count == 1 && model.IsInTargetCollection && model.StageButtonLabel.Contains("Review staging"),
            "A collection made from the Tags tab should start with the open file, list it under In collections and tick the header button: " + model.StageButtonLabel);
        model.StageSelectedCommand.Execute(null);
        await WaitUntilAsync(() => model.FileCollections.Count == 0, token);
        Require(store.Load(collectionPath).Paths.Length == 0 && !model.IsInTargetCollection, "S on a file already in the collection should take it out.");
        model.StageSelectedCommand.Execute(null);
        Require(store.Load(collectionPath).Paths.Length == 1 && model.FileCollections.Count == 1 && model.Collections.Single(item => item.FilePath == collectionPath).Count == 1,
            "S again should put it back, once, and the collection list should count it.");
        model.InspectorTab = 1;
        Render(window, Path.Combine(dataDirectory, "workspace-collections.png"));
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
        Require(window.VideoTimeline.IsVisible && window.PlaybackButton.IsVisible, "Video timeline and playback controls should be visible.");
        model.StageSelectedCommand.Execute(null);
        // Related files and collections open beside the folder, not instead of it: the folder stays in the workspace.
        var shown = model.LibraryView;
        await model.FindRelatedCommand.ExecuteAsync(null);
        Require(shown.Count == 1 && ((AssetViewModel)shown.GetItemAt(0)).Asset.FullPath == photo.Asset.FullPath && model.WorkspaceFolders.Count == 2,
            "Shared tags should find related media across sources, as one more entry in the workspace.");
        await model.OpenCollectionCommand.ExecuteAsync(null);
        Require(shown.Count == 2 && shown.Cast<AssetViewModel>().All(item => item.IsFavorite), "Collection browsing lost media or cross-root favorites.");
        Require(model.WorkspaceFolders.Any(folder => !folder.IsVirtual && folder.Path == mediaDirectory), "Showing a collection must keep the folder open.");
        model.SelectedAsset = shown.Cast<AssetViewModel>().Single(item => item.Asset.Kind == MediaKind.Photo);
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        Require(model.SelectedTags.Contains("review"), "Tags should follow a file into a collection.");
        await model.RemoveFromCollectionCommand.ExecuteAsync(null);
        Require(shown.Count == 1 && File.Exists(photo.Asset.FullPath), "Removing a staged item must not delete its media.");
        var collection = store.Load(collectionPath);
        store.Save(collectionPath, CollectionStore.Add(collection, [Path.Combine(mediaDirectory, "missing.png")]));
        await model.OpenCollectionCommand.ExecuteAsync(null);
        Require(shown.Count == 1 && model.CurrentFolder?.State.Contains("1 missing", StringComparison.Ordinal) == true, "Missing collection paths should be reported and preserved: " + model.CurrentFolder?.State);
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
        var path = Path.Combine(dataDirectory, "rotated-exif.jpg");
        File.WriteAllBytes(path, JpegWithExif(Pixels.Encode(image, SKEncodedImageFormat.Jpeg, 92), 6, "Smoke test camera"));
        var preview = await Task.Run(() => ImageLoader.Load(path), token);
        Require(preview.Image.PixelWidth == 360 && preview.Image.PixelHeight == 640, "JPEG EXIF orientation must be applied to the preview.");
        Require(preview.Metadata.GetValueOrDefault("Camera model") == "Smoke test camera", "Available EXIF camera metadata must be surfaced.");
        var thumbnail = await Task.Run(() => ImageLoader.Thumbnail(path, 220), token);
        Require(thumbnail.PixelHeight > thumbnail.PixelWidth, "Filmstrip thumbnail orientation must match the preview.");
        var crop = PictureOps.Crop(preview.Image, new PixelCrop(0, 0, 10, 10));
        var png = await Task.Run(() => ImageLoader.Encode(crop), token);
        Require(png.Length > 0, "Oriented crop must be usable outside its decoding thread.");
        var tallPath = Path.Combine(dataDirectory, "extremely-tall.png");
        using (var tall = new SKBitmap(1, 4096))
        using (var data = tall.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(tallPath, data.ToArray());
        var tallThumbnail = ImageLoader.Thumbnail(tallPath, 220);
        Require(tallThumbnail.PixelWidth <= 220 && tallThumbnail.PixelHeight <= 220, "Extreme image aspect ratios must not exceed thumbnail memory bounds.");
    }

    /// <summary>Puts an EXIF block (Model, Orientation) into a JPEG, the way a camera writes it.</summary>
    private static byte[] JpegWithExif(byte[] jpeg, ushort orientation, string model)
    {
        var text = System.Text.Encoding.ASCII.GetBytes(model + "\0");
        // Little-endian TIFF header, then one IFD with two entries in tag order: 0x0110 Model (ASCII, stored after the IFD)
        // and 0x0112 Orientation (SHORT, inline). The IFD is 2 + 2 * 12 + 4 = 30 bytes, so the text starts at 8 + 30.
        byte[] tiff = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 2, 0,
            0x10, 0x01, 2, 0, (byte)text.Length, 0, 0, 0, 38, 0, 0, 0,
            0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0,
            0, 0, 0, 0, .. text];
        byte[] header = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0];
        var length = 2 + header.Length + tiff.Length;
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. header, .. tiff];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    private static async Task CheckFilmstripAsync(MainViewModel model, MainWindow window, AssetViewModel photo, string dataDirectory, CancellationToken token)
    {
        model.ClearFiltersCommand.Execute(null);
        model.SelectedAsset = null;
        model.Assets.Clear();
        model.Assets.AddRange(Enumerable.Range(1, 5000).Reverse().Select(index => new AssetViewModel(photo.Asset with { RelativePath = $"frame{index}.png" }, false)));
        model.SortMethod = "Name (natural)";
        var view = model.LibraryView;
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
        CheckFollowMarker(model, window, dataDirectory);
        model.ShowSources = false;
        model.MainTab = 1;
        Layout(window, 1080, 700);
        var content = (Visual)window.Content!;
        // The dock under the picture keeps one height for every kind of file, so at the smallest window a photo gets a little less height than it used to.
        Require(window.PreviewSurface.Bounds.Width > 600 && window.PreviewSurface.Bounds.Height > 185, $"Compact layout should reclaim space when Sources is collapsed (picture {window.PreviewSurface.Bounds.Width:0} x {window.PreviewSurface.Bounds.Height:0}, filmstrip {window.FilmstripPanel.Bounds.Height:0}, dock {window.PreviewDock.Bounds.Height:0}, header {window.CenterHeader.Bounds.Height:0}).");
        // Text buttons share one height; icon buttons, the play button, folder cards and the small inline ones are sized on purpose.
        string[] ownSize = ["icon", "quiet", "folderCard", "play", "row", "link", "star", "small"];
        var heights = VisualChildren(content).OfType<Button>()
            .Where(button => button.Bounds.Height > 0 && Shown(button) && button.Command is not null && !ownSize.Any(button.Classes.Contains))
            .Select(button => (button, button.Bounds.Height)).ToList();
        foreach (var (button, buttonHeight) in heights)
            Require(Math.Abs(buttonHeight - heights[0].Height) < 0.1, $"Action button heights should be consistent: {button.Name} \"{button.Content}\" is {buttonHeight:0.#} tall, not {heights[0].Height:0.#} like {heights[0].button.Name}.");

        var loader = new ThumbnailLoader();
        var engine = new MediaEngine(new ToolPaths("nonexistent-ffmpeg", "nonexistent-ffprobe"), Path.Combine(dataDirectory, "thumbnail-test"));
        for (var index = 0; index < 104; index++)
        {
            var thumbnail = await loader.LoadAsync(photo.Asset with { ModifiedTicks = index }, engine, token);
            Require(thumbnail is { PixelWidth: <= 220, PixelHeight: <= 220 }, "PNG thumbnails must use native downscaled decoding without FFmpeg.");
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
