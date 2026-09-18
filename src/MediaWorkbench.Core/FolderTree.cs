namespace MediaWorkbench.Core;

/// <summary>A folder that contains media directly or in its descendants. Counts on the node include every descendant.</summary>
public sealed class FolderNode(string name, string path)
{
    public string Name { get; set; } = name;
    /// <summary>Normalized folder key: segments joined with a backslash, empty for the root.</summary>
    public string Path { get; } = path;
    public List<FolderNode> Children { get; } = [];
    public int DirectPhotos { get; set; }
    public int DirectVideos { get; set; }
    public int DirectAudio { get; set; }
    public long DirectBytes { get; set; }
    public int Photos { get; set; }
    public int Videos { get; set; }
    public int Audio { get; set; }
    public long Bytes { get; set; }
    /// <summary>Number of descendant folders (after chain collapsing) that hold media somewhere beneath them.</summary>
    public int FolderCount { get; set; }
    public int Total => Photos + Videos + Audio;
    public int DirectTotal => DirectPhotos + DirectVideos + DirectAudio;
}

public static class FolderTree
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Canonical folder key used for both tree paths and filtering, so UNC, drive and relative folders compare consistently.</summary>
    public static string Normalize(string? folder) =>
        string.Join('\\', (folder ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>True when <paramref name="folderKey"/> is the filter folder or lies beneath it. An empty filter matches everything.</summary>
    public static bool Contains(string filter, string folderKey) =>
        filter.Length == 0
        || folderKey.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || (folderKey.Length > filter.Length && folderKey[filter.Length] == '\\' && folderKey.StartsWith(filter, StringComparison.OrdinalIgnoreCase));

    public static FolderNode Build(IEnumerable<(string Folder, MediaAsset Asset)> items, string rootName)
    {
        var root = new FolderNode(rootName, "");
        var index = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        foreach (var (folder, asset) in items)
        {
            var node = root;
            var path = "";
            foreach (var segment in Normalize(folder).Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                path = path.Length == 0 ? segment : path + "\\" + segment;
                if (!index.TryGetValue(path, out var child))
                {
                    child = new FolderNode(segment, path);
                    index[path] = child;
                    node.Children.Add(child);
                }
                node = child;
            }
            switch (asset.Kind)
            {
                case MediaKind.Photo: node.DirectPhotos++; break;
                case MediaKind.Video: node.DirectVideos++; break;
                default: node.DirectAudio++; break;
            }
            node.DirectBytes += asset.Length;
        }
        Collapse(root);
        Aggregate(root);
        return root;
    }

    public static FolderNode? Find(FolderNode root, string path)
    {
        if (root.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return root;
        foreach (var child in root.Children)
            if (Contains(child.Path, path) && Find(child, path) is { } match) return match;
        return null;
    }

    /// <summary>Merges chains of folders that only lead to a single subfolder and hold no media themselves, e.g. C: \ Users \ me becomes one row.</summary>
    private static void Collapse(FolderNode node)
    {
        for (var index = 0; index < node.Children.Count; index++)
        {
            var child = node.Children[index];
            while (child.Children.Count == 1 && child.DirectTotal == 0)
            {
                var only = child.Children[0];
                only.Name = child.Name + "\\" + only.Name;
                child = only;
            }
            node.Children[index] = child;
            Collapse(child);
        }
    }

    private static void Aggregate(FolderNode node)
    {
        node.Photos = node.DirectPhotos;
        node.Videos = node.DirectVideos;
        node.Audio = node.DirectAudio;
        node.Bytes = node.DirectBytes;
        node.FolderCount = 0;
        foreach (var child in node.Children)
        {
            Aggregate(child);
            node.Photos += child.Photos;
            node.Videos += child.Videos;
            node.Audio += child.Audio;
            node.Bytes += child.Bytes;
            node.FolderCount += 1 + child.FolderCount;
        }
        node.Children.Sort((left, right) => NaturalOrder.Compare(left.Name, right.Name));
    }
}
