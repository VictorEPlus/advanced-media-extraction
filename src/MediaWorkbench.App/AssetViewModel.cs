using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

public sealed partial class AssetViewModel(MediaAsset asset, bool favorite) : ObservableObject
{
    public MediaAsset Asset { get; } = asset;
    public string Name => Asset.Name;
    public string Details => $"{Asset.Kind} - {Asset.RelativePath}";
    public string FavoriteLabel => IsFavorite ? "★" : "☆";
    public string Placeholder => Asset.Kind == MediaKind.Audio ? "♫" : Asset.Kind == MediaKind.Video ? "▶" : "▧";
    /// <summary>The corner badge on a thumbnail: one glyph per kind, in the colour the folder graph already uses for that kind.</summary>
    public string KindGlyph => Asset.Kind switch { MediaKind.Video => "▶", MediaKind.Audio => "♫", _ => "▣" };
    public string KindLabel => Asset.Kind.ToString();
    public Brush KindBrush => Asset.Kind switch { MediaKind.Video => VideoBrush, MediaKind.Audio => AudioBrush, _ => PhotoBrush };
    private static readonly SolidColorBrush PhotoBrush = Tokens.Brush("ChartPhotoBrush", Color.FromRgb(0x5A, 0x8C, 0xFF));
    private static readonly SolidColorBrush VideoBrush = Tokens.Brush("ChartVideoBrush", Color.FromRgb(0xE8, 0x5C, 0x2A));
    private static readonly SolidColorBrush AudioBrush = Tokens.Brush("ChartAudioBrush", Color.FromRgb(0x00, 0xAC, 0x75));
    public bool ThumbnailRequested { get; set; }
    /// <summary>Normalized folder this file lives in, relative to the library root (or the full folder for collections and tag searches).</summary>
    public string FolderKey { get; init; } = "";
    /// <summary>The workspace folder, collection or tag search this entry belongs to.</summary>
    public WorkspaceFolder? Owner { get; init; }

    public string TagSummary => string.Join(", ", Tags);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TagSummary))]
    private string[] tags = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteLabel))]
    private bool isFavorite = favorite;

    [ObservableProperty]
    private ImageSource? thumbnail;
}

public sealed partial class ExportJobViewModel(string title, CancellationToken lifetime) : ObservableObject
{
    public string Title { get; } = title;
    public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    public bool IsActive => !IsFinished;
    public bool HasOutput => !string.IsNullOrEmpty(OutputPath);

    [ObservableProperty]
    private string status = "Queued";

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool isFinished;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string? outputPath;
}
