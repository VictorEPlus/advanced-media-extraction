using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace MediaWorkbench.Avalonia;

/// <summary>
/// The system's own dialogs for choosing folders and JSON files, through Avalonia's storage provider (on Windows these are the
/// Explorer dialogs, with quick access, network locations, search and the address bar).
/// </summary>
internal static class NativeDialogs
{
    private static readonly FilePickerFileType Json = new("JSON files") { Patterns = ["*.json"] };

    /// <summary>The window the dialogs belong to; set by the main window, or found through the app's lifetime.</summary>
    public static TopLevel? Owner { get; set; }

    private static IStorageProvider? Provider =>
        (Owner ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow)?.StorageProvider;

    public static async Task<string?> PickFolderAsync(string title, string? initialDirectory)
    {
        if (Provider is not { } provider) return null;
        var picked = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartAsync(provider, initialDirectory)
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> OpenJsonAsync(string title, string? initialDirectory)
    {
        if (Provider is not { } provider) return null;
        var picked = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [Json, FilePickerFileTypes.All],
            SuggestedStartLocation = await StartAsync(provider, initialDirectory)
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>The system asks before replacing an existing file, so an overwrite is always an explicit choice.</summary>
    public static async Task<string?> SaveJsonAsync(string title, string? initialDirectory, string defaultFileName)
    {
        if (Provider is not { } provider) return null;
        var picked = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName,
            DefaultExtension = "json",
            FileTypeChoices = [Json],
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await StartAsync(provider, initialDirectory)
        });
        return picked?.TryGetLocalPath();
    }

    /// <summary>The system clipboard, through the main window.</summary>
    public static global::Avalonia.Input.Platform.IClipboard? Clipboard =>
        (Owner ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow)?.Clipboard;

    /// <summary>A file on disk as a storage item, for putting on the clipboard.</summary>
    public static async Task<IStorageFile?> FileAsync(string path) => Provider is { } provider ? await provider.TryGetFileFromPathAsync(path) : null;

    private static async Task<IStorageFolder?> StartAsync(IStorageProvider provider, string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? await provider.TryGetFolderFromPathAsync(path) : null;
}
