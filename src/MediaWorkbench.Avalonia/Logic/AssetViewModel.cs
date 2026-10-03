using Avalonia.Media.Imaging;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia;

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
    public IBrush KindBrush => Asset.Kind switch { MediaKind.Video => VideoBrush, MediaKind.Audio => AudioBrush, _ => PhotoBrush };
    /// <summary>The file type on a thumbnail, as its extension (MP4, JPG, WAV), in the colour of its kind.</summary>
    public string KindBadge => Path.GetExtension(Asset.RelativePath).TrimStart('.').ToUpperInvariant();
    public bool HasKindBadge => KindBadge.Length > 0;
    private static readonly SolidColorBrush PhotoBrush = Tokens.Brush("PhotoBrush", Color.FromRgb(0xA9, 0x93, 0xFF));
    private static readonly SolidColorBrush VideoBrush = Tokens.Brush("VideoBrush", Color.FromRgb(0x5F, 0xD0, 0xFF));
    private static readonly SolidColorBrush AudioBrush = Tokens.Brush("AudioBrush", Color.FromRgb(0x4F, 0xE0, 0xA0));
    public bool ThumbnailRequested { get; set; }
    /// <summary>Picked in the filmstrip (Ctrl+click), for tagging several files at once.</summary>
    [ObservableProperty] private bool isPicked;
    /// <summary>While the pointer rests on a video's thumbnail: one of five pictures from its start to its end, in turn.</summary>
    [ObservableProperty] private Bitmap? hoverFrame;
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
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? thumbnail;

    /// <summary>The thumbnail is fading in (or shown); until then the kind's placeholder shows.</summary>
    public bool HasThumbnail => Thumbnail is not null;
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
