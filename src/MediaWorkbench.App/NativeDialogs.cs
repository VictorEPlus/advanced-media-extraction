using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MediaWorkbench.App;

/// <summary>
/// The built-in Windows Explorer dialogs for choosing folders and JSON files. These replace the app's former custom picker:
/// they give quick access, network locations, search and the address bar people already know.
/// </summary>
internal static class NativeDialogs
{
    private const string JsonFilter = "JSON files (*.json)|*.json|All files (*.*)|*.*";

    public static string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (ExistingDirectory(initialDirectory) is { } start)
            dialog.InitialDirectory = start;
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public static string? OpenJson(string title, string? initialDirectory)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = JsonFilter, CheckFileExists = true, Multiselect = false };
        if (ExistingDirectory(initialDirectory) is { } start)
            dialog.InitialDirectory = start;
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    /// <summary>Windows asks before replacing an existing file, so an overwrite is always an explicit choice.</summary>
    public static string? SaveJson(string title, string? initialDirectory, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = JsonFilter, DefaultExt = ".json", AddExtension = true, OverwritePrompt = true, FileName = defaultFileName };
        if (ExistingDirectory(initialDirectory) is { } start)
            dialog.InitialDirectory = start;
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } window ? window : null;

    private static string? ExistingDirectory(string? path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
}
