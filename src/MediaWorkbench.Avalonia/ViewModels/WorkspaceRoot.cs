using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia.ViewModels;

/// <summary>One folder open in the workspace, with every media file found in it.</summary>
public sealed class WorkspaceRoot(string path, string label)
{
    public string Path { get; } = path;
    /// <summary>Unique among the workspace folders; begins the tree key of everything inside.</summary>
    public string Label { get; } = label;
    public string DisplayName { get; set; } = label;
    /// <summary>Every media file in the folder, by full path, so thumbnails stay with their files across rescans and filters.</summary>
    public Dictionary<string, MediaItemViewModel> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    public FolderNodeViewModel? Node { get; set; }
    public CancellationTokenSource? Scan { get; set; }

    public string FolderKeyOf(MediaAsset asset) => Workspace.FolderKey(Label, System.IO.Path.GetDirectoryName(asset.RelativePath));
}
