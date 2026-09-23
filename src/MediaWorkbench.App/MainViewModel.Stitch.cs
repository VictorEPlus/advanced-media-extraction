using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

/// <summary>One picture waiting to be combined: the pixels themselves, plus where they came from.</summary>
public sealed partial class StitchEntry(string name, string source, BitmapSource picture) : ObservableObject
{
    public string Name { get; } = name;
    public string Source { get; } = source;
    public BitmapSource Picture { get; } = picture;
    public StitchItem Item => new(Picture.PixelWidth, Picture.PixelHeight);
    public string SizeText => $"{Picture.PixelWidth:N0} × {Picture.PixelHeight:N0}";
    public string ToolTipText => $"{Source}\n{SizeText}";
}

// Stitch: a few pictures combined into one. The pictures are held as decoded pixels, so what is added stays as it was even
// if the file is moved or the preview goes elsewhere, and the preview on screen is the export drawn smaller.
public sealed partial class MainViewModel
{
    /// <summary>Enough for contact sheets and comparisons without holding an unreasonable number of full-size pictures in memory.</summary>
    public const int StitchLimit = 40;

    public ObservableCollection<StitchEntry> StitchItems { get; } = [];
    public string[] StitchArrangements { get; } = ["Side by side", "Stacked", "Grid"];

    [ObservableProperty] private string stitchArrangement = "Side by side";
    [ObservableProperty] private int stitchGap;
    [ObservableProperty] private int stitchColumns = 3;
    [ObservableProperty] private bool stitchMatchSizes = true;
    [ObservableProperty] private bool stitchAsJpeg;
    [ObservableProperty] private string stitchBackdrop = "Dark";
    [ObservableProperty] private StitchEntry? selectedStitchItem;
    [ObservableProperty] private BitmapSource? stitchPreview;

    public string[] StitchBackdrops { get; } = ["Dark", "Black", "White", "Transparent"];
    public bool HasStitchItems => StitchItems.Count > 0;
    public bool ShowStitchEmpty => StitchItems.Count == 0;
    public bool CanStitch => StitchItems.Count > 1;
    public bool IsGridStitch => StitchArrangement == "Grid";
    public string StitchCountLabel => StitchItems.Count switch
    {
        0 => "No pictures added yet",
        1 => "1 picture added — add another to combine them",
        var count => $"{count:N0} pictures"
    };
    public string StitchSizeLabel => StitchPlanNow() is { IsEmpty: false } plan ? $"{plan.Width:N0} × {plan.Height:N0} px" : "";
    public string StitchExportLabel => StitchAsJpeg ? "Export JPEG" : "Export PNG";

    partial void OnStitchArrangementChanged(string value) { OnPropertyChanged(nameof(IsGridStitch)); RefreshStitch(); }
    partial void OnStitchGapChanged(int value) => RefreshStitch();
    partial void OnStitchColumnsChanged(int value) => RefreshStitch();
    partial void OnStitchMatchSizesChanged(bool value) => RefreshStitch();
    partial void OnStitchBackdropChanged(string value) => RefreshStitch();
    partial void OnStitchAsJpegChanged(bool value) { OnPropertyChanged(nameof(StitchExportLabel)); RefreshStitch(); }

    private StitchDirection Direction => StitchArrangement switch
    {
        "Stacked" => StitchDirection.Column,
        "Grid" => StitchDirection.Grid,
        _ => StitchDirection.Row
    };

    /// <summary>Transparent only survives PNG, so a JPEG falls back to the dark backdrop rather than saving black corners by surprise.</summary>
    private Color Backdrop => StitchBackdrop switch
    {
        "Black" => Colors.Black,
        "White" => Colors.White,
        "Transparent" => StitchAsJpeg ? MonitorColor : Colors.Transparent,
        _ => MonitorColor
    };

    private static Color MonitorColor => Tokens.Brush("MonitorBrush", Color.FromRgb(0x0B, 0x0A, 0x14)).Color;

    private StitchPlan StitchPlanNow() =>
        StitchLayout.Compute(StitchItems.Select(entry => entry.Item).ToList(), Direction, StitchGap, StitchColumns, StitchMatchSizes);

    /// <summary>Redraws the combined picture. Small enough to do on every change: the preview is at most a screen wide.</summary>
    private void RefreshStitch()
    {
        var plan = StitchPlanNow();
        StitchPreview = plan.IsEmpty ? null : StitchRenderer.Render(plan, StitchItems.Select(entry => (BitmapSource?)entry.Picture).ToList(), Backdrop, StitchRenderer.PreviewScale(plan));
        foreach (var property in new[] { nameof(HasStitchItems), nameof(ShowStitchEmpty), nameof(CanStitch), nameof(StitchCountLabel), nameof(StitchSizeLabel) })
            OnPropertyChanged(property);
    }

    /// <summary>
    /// Adds a picture to the stitch. The selected file contributes the picture actually on screen, so a chosen video frame
    /// is what gets added; any other file contributes its own first picture, at full size.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AddToStitchAsync(AssetViewModel? item)
    {
        item ??= SelectedAsset;
        if (item is null)
        {
            Notify(NotificationKind.Info, "Select a photo or a video frame first.");
            return;
        }
        if (item.Asset.Kind == MediaKind.Audio)
        {
            Notify(NotificationKind.Info, $"{item.Name} is sound, so there is nothing to stitch.");
            return;
        }
        if (StitchItems.Count >= StitchLimit)
        {
            Notify(NotificationKind.Info, $"The stitch already holds {StitchLimit} pictures. Remove one first.");
            return;
        }
        try
        {
            var picture = item == loadedAsset && PreviewImage is BitmapSource shown && !ShowPlayback
                ? shown
                : DecodeImage(await engine.GetFrameAsync(item.Asset, 0, lifetime.Token));
            var name = item == loadedAsset && IsVideo && DisplayedFrame >= 0 ? $"{item.Name} frame {DisplayedFrame:N0}" : item.Name;
            StitchItems.Add(new StitchEntry(name, item.Asset.FullPath, picture));
            SelectedStitchItem = StitchItems[^1];
            RefreshStitch();
            Status = $"Added {name} to the stitch. {StitchCountLabel}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError(exception); }
    }

    [RelayCommand]
    private void RemoveFromStitch(StitchEntry? entry)
    {
        entry ??= SelectedStitchItem;
        if (entry is null)
            return;
        var at = StitchItems.IndexOf(entry);
        if (at < 0)
            return;
        StitchItems.RemoveAt(at);
        SelectedStitchItem = StitchItems.Count == 0 ? null : StitchItems[Math.Min(at, StitchItems.Count - 1)];
        RefreshStitch();
    }

    [RelayCommand]
    private void MoveStitchItem(string? direction)
    {
        if (SelectedStitchItem is not { } entry)
            return;
        var at = StitchItems.IndexOf(entry);
        var to = direction == "back" ? at - 1 : at + 1;
        if (at < 0 || to < 0 || to >= StitchItems.Count)
            return;
        StitchItems.Move(at, to);
        SelectedStitchItem = entry;
        RefreshStitch();
    }

    [RelayCommand]
    private void ClearStitch()
    {
        StitchItems.Clear();
        SelectedStitchItem = null;
        RefreshStitch();
        Status = "Cleared the stitch.";
    }

    [RelayCommand]
    private void ExportStitch() => Guard(() =>
    {
        var plan = StitchPlanNow();
        if (plan.IsEmpty || StitchItems.Count < 2)
            throw new InvalidOperationException("Add at least two pictures before exporting a stitch.");
        var stem = Path.GetFileNameWithoutExtension(StitchItems[0].Name) + $"_stitch_{StitchItems.Count}";
        var extension = StitchAsJpeg ? ".jpg" : ".png";
        var asJpeg = StitchAsJpeg;
        var directory = settings.ExportDirectory;
        // Drawn here, at full size, on the thread that owns the pictures; the result is frozen, so only the encoding and the
        // writing are left to do away from the interface.
        var combined = StitchRenderer.Render(plan, StitchItems.Select(entry => (BitmapSource?)entry.Picture).ToList(), Backdrop)
            ?? throw new InvalidOperationException("The combined picture came out empty.");
        QueueExport($"Stitch of {StitchItems.Count:N0} pictures", (_, token) => Task.Run(() =>
        {
            var bytes = StitchRenderer.Encode(combined, asJpeg);
            using var output = OutputReservation.Create(directory, stem, extension);
            File.WriteAllBytes(output.Path, bytes);
            output.Complete();
            return output.Path;
        }, token));
    });
}
