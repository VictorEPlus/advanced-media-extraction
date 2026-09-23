namespace MediaWorkbench.Core;

/// <summary>
/// Keeps tags with their files after a scan. A tagged file that is not where it was is looked for among the files the scan found:
/// first by its NTFS file ID, which a rename or a move on the same drive keeps and a copy never has; then by content fingerprint,
/// for a move to another drive. A file whose content is the same as a tagged file is a copy and gets its tags too. Tagged folders
/// that moved are found again by their folder ID. Only files of the same size are ever fingerprinted, so this stays cheap.
/// </summary>
public static class TagReconciler
{
    public sealed record Result(int Moved, int Copied, int FoldersMoved)
    {
        public bool Changed => Moved + Copied + FoldersMoved > 0;
    }

    public static Result Reconcile(TagStore store, string root, IReadOnlyCollection<MediaAsset> scanned, CancellationToken cancellationToken = default, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var files = store.ReadFiles();
        var taggedPaths = new HashSet<string>(files.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
        var orphans = files.Where(file => !exists(file.Path)).ToList();
        var untagged = scanned.Where(asset => !taggedPaths.Contains(asset.FullPath)).ToList();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moved = 0;
        var copied = 0;

        // Renamed or moved on the same drive: same ID, and a move keeps the size and the date, so only those are opened.
        foreach (var orphan in orphans.Where(file => file.Identity is not null).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var candidate in untagged)
            {
                if (claimed.Contains(candidate.FullPath) || candidate.Length != orphan.Size || candidate.ModifiedTicks != orphan.Modified)
                    continue;
                if (FileIdentity.Read(candidate.FullPath) != orphan.Identity)
                    continue;
                store.MoveFile(orphan.Id, candidate.FullPath);
                claimed.Add(candidate.FullPath);
                orphans.Remove(orphan);
                moved++;
                break;
            }
        }

        // Same content: a move to another drive (the original is gone) or a copy (it is still there).
        var bySize = files.Where(file => file.Fingerprint is not null).GroupBy(file => file.Size).ToDictionary(group => group.Key, group => group.ToList());
        foreach (var candidate in untagged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (claimed.Contains(candidate.FullPath) || !bySize.TryGetValue(candidate.Length, out var sameSize))
                continue;
            var fingerprint = FingerprintOf(store, candidate, cancellationToken);
            if (fingerprint is null)
                continue;
            var matches = sameSize.Where(file => file.Fingerprint == fingerprint).ToList();
            if (matches.Count == 0)
                continue;
            if (matches.FirstOrDefault(orphans.Contains) is { } gone)
            {
                store.MoveFile(gone.Id, candidate.FullPath);
                orphans.Remove(gone);
                moved++;
            }
            else
            {
                store.CopyTags(matches[0], candidate.FullPath);
                copied++;
            }
            claimed.Add(candidate.FullPath);
        }

        return new Result(moved, copied, ReconcileFolders(store, root, cancellationToken));
    }

    /// <summary>A tagged folder that is gone is looked for, by its ID, among the folders under the scanned root on the same drive.</summary>
    private static int ReconcileFolders(TagStore store, string root, CancellationToken cancellationToken)
    {
        var lost = store.ReadFolderRecords().Where(record => record.Identity is not null && !Directory.Exists(record.Folder)).ToList();
        if (lost.Count == 0 || FileIdentity.Read(root) is not { } rootIdentity)
            return 0;
        var volume = FileIdentity.VolumeOf(rootIdentity);
        lost = lost.Where(record => FileIdentity.VolumeOf(record.Identity) == volume).ToList();
        if (lost.Count == 0)
            return 0;
        var wanted = lost.ToDictionary(record => record.Identity!, record => record.Folder);
        var moved = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        foreach (var folder in Directory.EnumerateDirectories(root, "*", options).Prepend(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FileIdentity.Read(folder) is { } identity && wanted.Remove(identity, out var old))
            {
                store.MoveFolder(old, folder);
                moved++;
                if (wanted.Count == 0) break;
            }
        }
        return moved;
    }

    /// <summary>
    /// Fingerprints tagged files that do not have one yet, or whose size or date changed since. Run in the background: a large video
    /// takes a few seconds to read. Returns how many were done.
    /// </summary>
    public static int FillFingerprints(TagStore store, CancellationToken cancellationToken = default)
    {
        var done = 0;
        foreach (var file in store.ReadFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (_, size, modified) = TagStore.Stamp(file.Path);
            if (size == 0 && modified == 0)
                continue;
            if (file.Fingerprint is not null && file.Size == size && file.Modified == modified)
                continue;
            try
            {
                var fingerprint = ContentFingerprint.Compute(file.Path, cancellationToken);
                store.SetFingerprint(file.Id, size, modified, fingerprint);
                store.CacheFingerprint(file.Path, size, modified, fingerprint);
                done++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return done;
    }

    private static string? FingerprintOf(TagStore store, MediaAsset asset, CancellationToken cancellationToken)
    {
        if (store.CachedFingerprint(asset.FullPath, asset.Length, asset.ModifiedTicks) is { } cached)
            return cached;
        try
        {
            var fingerprint = ContentFingerprint.Compute(asset.FullPath, cancellationToken);
            store.CacheFingerprint(asset.FullPath, asset.Length, asset.ModifiedTicks, fingerprint);
            return fingerprint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Live folder tags: a file carries the tags of its own folder and of every folder above it.</summary>
public static class FolderTags
{
    public static IEnumerable<(string Tag, string Folder)> Inherited(string fileFolder, IReadOnlyDictionary<string, string[]> folderTags)
    {
        foreach (var (folder, tags) in folderTags)
        {
            if (!fileFolder.Equals(folder, StringComparison.OrdinalIgnoreCase)
                && !(fileFolder.Length > folder.Length && fileFolder.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && (fileFolder[folder.Length] == '\\' || folder.EndsWith('\\'))))
                continue;
            foreach (var tag in tags)
                yield return (tag, folder);
        }
    }
}
