namespace MediaWorkbench.Core;

/// <summary>
/// Several folders open at once. Each workspace folder has a short label that begins the folder key of everything inside it, so
/// one folder tree, one filter and one filmstrip serve all of them and moving between them never rescans anything.
/// </summary>
public static class Workspace
{
    /// <summary>Labels for folders added in this order. See <see cref="Label"/>.</summary>
    public static IReadOnlyList<string> Labels(IReadOnlyList<string> roots)
    {
        var labels = new List<string>(roots.Count);
        foreach (var root in roots)
            labels.Add(Label(root, labels));
        return labels;
    }

    /// <summary>
    /// The label for a folder joining the workspace: its own name, or, when that is taken, the name with its parent folder in
    /// brackets ("Images (Work)"), and a number only if even that is taken. Labels already given never change, because every
    /// folder key inside that folder begins with its label. Backslashes never appear in a label.
    /// </summary>
    public static string Label(string root, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var name = NameOf(root);
        if (used.Add(name))
            return name;
        var label = ParentName(root) is { Length: > 0 } parent ? $"{name} ({parent})" : name;
        var candidate = label;
        for (var number = 2; used.Contains(candidate); number++)
            candidate = $"{label} {number}";
        return candidate;
    }

    /// <summary>The folder key of a file: the workspace folder's label, then the subfolders it is in.</summary>
    public static string FolderKey(string label, string? relativeFolder) =>
        FolderTree.Normalize(string.IsNullOrEmpty(relativeFolder) ? label : label + "\\" + relativeFolder);

    /// <summary>
    /// What changed between the index saved for a folder and a fresh scan of it: files that are new, files whose size or date
    /// changed, and relative paths that are gone. Compared by relative path, ignoring case as Windows does.
    /// </summary>
    public static (List<MediaAsset> Added, List<MediaAsset> Changed, List<string> Removed) Diff(IEnumerable<MediaAsset> indexed, IEnumerable<MediaAsset> scanned)
    {
        var before = new Dictionary<string, MediaAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in indexed)
            before[asset.RelativePath] = asset;
        var added = new List<MediaAsset>();
        var changed = new List<MediaAsset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in scanned)
        {
            seen.Add(asset.RelativePath);
            if (!before.TryGetValue(asset.RelativePath, out var old))
                added.Add(asset);
            else if (old.Length != asset.Length || old.ModifiedTicks != asset.ModifiedTicks || old.Kind != asset.Kind)
                changed.Add(asset);
        }
        var removed = before.Keys.Where(path => !seen.Contains(path)).ToList();
        return (added, changed, removed);
    }

    private static string NameOf(string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        var name = Path.GetFileName(trimmed);
        // A drive root has no folder name: "D:\" becomes "D".
        return (name.Length > 0 ? name : trimmed.TrimEnd('\\', '/').TrimEnd(':')).Replace('\\', ' ').Trim() is { Length: > 0 } clean ? clean : "Folder";
    }

    private static string ParentName(string root) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) is { } parent ? NameOf(parent) : "";
}
