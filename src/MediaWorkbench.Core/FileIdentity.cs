using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MediaWorkbench.Core;

/// <summary>
/// Who a file is, as opposed to where it is. On NTFS and ReFS every file and folder has a 128-bit ID that stays the same when it
/// is renamed or moved anywhere on the same drive, and that a copy does not share; with the drive's serial number it names exactly
/// one file. Moving to another drive is a copy and a delete to Windows, so it gets a new ID; the content fingerprint covers that.
/// FAT32 and exFAT have no lasting IDs, so there this returns null and only the fingerprint is used.
/// </summary>
public static partial class FileIdentity
{
    /// <summary>"volume:id" in hexadecimal, or null when the file system has no lasting IDs or the file cannot be opened.</summary>
    public static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows() || !HasLastingIds(path))
            return null;
        // No read or write access is asked for: only the ID is wanted, so a file open elsewhere never blocks it.
        using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
            return null;
        return $"{info.VolumeSerialNumber:x16}:{info.FileIdHigh:x16}{info.FileIdLow:x16}";
    }

    /// <summary>The drive part of an identity, so candidates on other drives can be skipped without opening them.</summary>
    public static string? VolumeOf(string? identity) => identity?.Split(':')[0];

    private static readonly Dictionary<string, bool> LastingIds = new(StringComparer.OrdinalIgnoreCase);

    private static bool HasLastingIds(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
        lock (LastingIds)
        {
            if (LastingIds.TryGetValue(root, out var known))
                return known;
            bool lasting;
            try { lasting = new DriveInfo(root).DriveFormat is "NTFS" or "ReFS"; }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { lasting = false; }
            LastingIds[root] = lasting;
            return lasting;
        }
    }

    private const int FileIdInfoClass = 18;
    private const FileAttributes BackupSemantics = (FileAttributes)0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, FileAttributes flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
}

/// <summary>What is in a file: SHA-256 of every byte. Two files with the same fingerprint are copies of each other.</summary>
public static class ContentFingerprint
{
    public static string Compute(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

/// <summary>
/// A look-alike fingerprint: 64 bits saying, for a 9 × 8 grey version of the picture, whether each pixel is brighter than the one
/// to its right. Resizing, re-saving, small edits and colour changes barely move it; a different picture changes about half the bits.
/// </summary>
public static class VisualHash
{
    public const int Width = 9;
    public const int Height = 8;

    /// <param name="grey">Width × Height brightness values, row by row.</param>
    public static ulong FromGrey(ReadOnlySpan<byte> grey)
    {
        if (grey.Length != Width * Height)
            throw new ArgumentException($"Expected {Width * Height} grey values.", nameof(grey));
        ulong bits = 0;
        var bit = 0;
        for (var row = 0; row < Height; row++)
            for (var column = 0; column < Width - 1; column++, bit++)
                if (grey[row * Width + column] > grey[row * Width + column + 1])
                    bits |= 1UL << bit;
        return bits;
    }

    /// <summary>How many of the 64 bits differ: 0 is the same picture, up to about 10 is very probably a version of it.</summary>
    public static int Distance(ulong left, ulong right) => System.Numerics.BitOperations.PopCount(left ^ right);
}
