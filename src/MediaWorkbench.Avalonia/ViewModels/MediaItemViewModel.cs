using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia.ViewModels;

/// <summary>One file in the filmstrip.</summary>
public sealed partial class MediaItemViewModel(MediaAsset asset, string rootLabel, string folderKey) : ObservableObject
{
    private bool requested;
    private Bitmap? thumbnail;

    public MediaAsset Asset { get; } = asset;
    /// <summary>The workspace folder it belongs to.</summary>
    public string RootLabel { get; } = rootLabel;
    /// <summary>The folder it is in, as a tree key: the workspace folder's label, then its subfolders.</summary>
    public string FolderKey { get; } = folderKey;
    public string Name => Asset.Name;
    public bool IsVideo => Asset.Kind == MediaKind.Video;
    public bool IsAudio => Asset.Kind == MediaKind.Audio;
    public bool IsPhoto => Asset.Kind == MediaKind.Photo;
    public string KindLabel => Asset.Kind switch { MediaKind.Video => "VIDEO", MediaKind.Audio => "AUDIO", _ => "" };
    public bool HasKindLabel => Asset.Kind != MediaKind.Photo;

    /// <summary>Set by the shell: fetches the thumbnail the first time a card asks for it.</summary>
    internal Func<MediaItemViewModel, Task>? RequestThumbnail { get; set; }

    /// <summary>The first read starts the fetch, so only cards that are actually on screen ever load one.</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!requested && RequestThumbnail is { } request)
            {
                requested = true;
                _ = request(this);
            }
            return thumbnail;
        }
    }

    public bool HasThumbnail => thumbnail is not null;

    internal void SetThumbnail(Bitmap? value)
    {
        thumbnail = value;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
    }

    /// <summary>Gives the thumbnail back to free memory; it is fetched again if the card comes back into view.</summary>
    internal void ReleaseThumbnail()
    {
        requested = false;
        thumbnail = null;
        OnPropertyChanged(nameof(HasThumbnail));
    }
}
