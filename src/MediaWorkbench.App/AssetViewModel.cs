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
    public bool ThumbnailRequested { get; set; }

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
