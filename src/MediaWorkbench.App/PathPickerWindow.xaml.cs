using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MediaWorkbench.App;

public enum PathPickerMode { Folder, OpenFavorites, SaveFavorites, OpenCollection }

public partial class PathPickerWindow : Window
{
    private readonly PathPickerMode mode;
    private string currentDirectory = "";
    public string? SelectedPath { get; private set; }

    public PathPickerWindow(PathPickerMode mode, string title, string initialDirectory)
    {
        this.mode = mode;
        InitializeComponent();
        Title = title;
        Instruction.Text = title;
        Choose.Content = mode switch { PathPickerMode.Folder => "Use this folder", PathPickerMode.OpenFavorites => "Open favorites", PathPickerMode.OpenCollection => "Open collection", _ => "Save favorites" };
        SaveOptions.Visibility = mode == PathPickerMode.SaveFavorites ? Visibility.Visible : Visibility.Collapsed;
        Navigate(Directory.Exists(initialDirectory) ? initialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public static string? Select(PathPickerMode mode, string title, string initialDirectory)
    {
        var window = new PathPickerWindow(mode, title, initialDirectory) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.SelectedPath : null;
    }

    internal void Navigate(string path)
    {
        try
        {
            path = Path.GetFullPath(path.Trim());
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
            var directory = new DirectoryInfo(path);
            var entries = directory.EnumerateDirectories("*", options)
                .Select(folder => new PathEntry(folder.FullName, folder.Name, true))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (mode != PathPickerMode.Folder)
                entries.AddRange(directory.EnumerateFiles("*.json", options)
                    .Select(file => new PathEntry(file.FullName, file.Name, false))
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase));
            Entries.ItemsSource = entries;
            currentDirectory = path;
            Address.Text = path;
            Overwrite.IsChecked = false;
            Feedback.Text = mode == PathPickerMode.Folder
                ? "Double-click a folder to browse, or select a folder and choose it."
                : "Showing folders and JSON files.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Feedback.Text = exception.Message;
        }
    }

    private void GoToAddress(object sender, RoutedEventArgs args) => Navigate(Address.Text);

    private void AddressKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter)
            return;
        Navigate(Address.Text);
        args.Handled = true;
    }

    private void GoUp(object sender, RoutedEventArgs args)
    {
        if (!string.IsNullOrEmpty(currentDirectory) && Directory.GetParent(currentDirectory) is { } parent)
            Navigate(parent.FullName);
        else
            ShowDrives(sender, args);
    }

    private void ShowDrives(object sender, RoutedEventArgs args)
    {
        Entries.ItemsSource = DriveInfo.GetDrives().Select(drive => new PathEntry(drive.Name, drive.Name, true)).ToArray();
        currentDirectory = "";
        Address.Text = "";
        Feedback.Text = "Choose a drive, or enter a folder/UNC path and click Go.";
    }

    private void OpenEntry(object sender, MouseButtonEventArgs args)
    {
        if (args.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Entries, source) is ListBoxItem)
            OpenSelectedEntry();
    }

    private void EntryKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter)
            return;
        OpenSelectedEntry();
        args.Handled = true;
    }

    private void OpenSelectedEntry()
    {
        if (Entries.SelectedItem is not PathEntry entry)
            return;
        if (entry.IsDirectory)
            Navigate(entry.FullPath);
        else if (mode is PathPickerMode.OpenFavorites or PathPickerMode.OpenCollection)
            ChoosePath(this, new RoutedEventArgs());
    }

    private void EntrySelected(object sender, SelectionChangedEventArgs args)
    {
        if (mode == PathPickerMode.SaveFavorites && Entries.SelectedItem is PathEntry { IsDirectory: false } entry)
            FileName.Text = entry.Name;
        if (Overwrite is not null)
            Overwrite.IsChecked = false;
    }

    private void FileNameChanged(object sender, TextChangedEventArgs args)
    {
        if (Overwrite is not null)
            Overwrite.IsChecked = false;
    }

    internal string ResolveSelection()
    {
        if (!string.Equals(Address.Text, currentDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Click Go to open the typed folder before selecting it.");
        if (mode == PathPickerMode.Folder)
        {
            var folder = (Entries.SelectedItem as PathEntry)?.FullPath ?? currentDirectory;
            if (!Directory.Exists(folder))
                throw new IOException("Choose an existing folder.");
            return folder;
        }
        if (mode is PathPickerMode.OpenFavorites or PathPickerMode.OpenCollection)
        {
            if (Entries.SelectedItem is not PathEntry { IsDirectory: false } entry || !File.Exists(entry.FullPath))
                throw new IOException("Select an existing JSON file.");
            return entry.FullPath;
        }
        if (!Directory.Exists(currentDirectory))
            throw new IOException("Open a destination folder first.");
        var filename = FileName.Text.Trim();
        if (filename.Length == 0 || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("Enter a filename without folder separators or invalid characters.");
        if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            filename += ".json";
        var target = Path.Combine(currentDirectory, filename);
        if (File.Exists(target) && Overwrite.IsChecked != true)
            throw new IOException("This file exists. Choose another name, or explicitly enable replacement.");
        return target;
    }

    private void ChoosePath(object sender, RoutedEventArgs args)
    {
        try
        {
            SelectedPath = ResolveSelection();
            DialogResult = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Feedback.Text = exception.Message;
        }
    }

    private sealed record PathEntry(string FullPath, string Name, bool IsDirectory)
    {
        public string Kind => IsDirectory ? "Folder" : "JSON";
    }
}
