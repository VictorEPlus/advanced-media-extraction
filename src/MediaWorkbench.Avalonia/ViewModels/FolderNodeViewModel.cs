using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaWorkbench.Core;

namespace MediaWorkbench.Avalonia.ViewModels;

/// <summary>
/// A folder in the sidebar tree: a workspace folder at the top, its subfolders under it. Children are made the first time the
/// folder is opened, so a library of thousands of folders costs nothing until it is explored.
/// </summary>
public sealed partial class FolderNodeViewModel : ObservableObject
{
    private FolderNode node;
    private bool childrenBuilt;

    public FolderNodeViewModel(FolderNode node, WorkspaceRoot root, bool isRoot)
    {
        this.node = node;
        Root = root;
        IsRoot = isRoot;
        // A placeholder child makes the expander arrow show without building the real children yet.
        if (node.Children.Count > 0) Children.Add(Placeholder);
    }

    private static readonly FolderNodeViewModel Placeholder = new();
    private FolderNodeViewModel()
    {
        node = new FolderNode("", "");
        Root = null!;
    }

    public WorkspaceRoot Root { get; }
    public bool IsRoot { get; }
    /// <summary>The tree key: the workspace folder's label, then the subfolders.</summary>
    public string Key => node.Path;
    public string Name => IsRoot ? Root.DisplayName : node.Name;
    public string CountText => node.Total.ToString("N0");
    public int Total => node.Total;
    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    [ObservableProperty] private bool isExpanded;
    /// <summary>Shown beside a workspace folder while it is being read.</summary>
    [ObservableProperty] private bool isBusy;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) BuildChildren();
    }

    private void BuildChildren()
    {
        if (childrenBuilt) return;
        childrenBuilt = true;
        Children.Clear();
        foreach (var child in node.Children.OrderBy(child => child.Name, Comparer<string>.Create(NaturalOrder.Compare)))
            Children.Add(new FolderNodeViewModel(child, Root, false));
    }

    /// <summary>The same folder with new counts after a rescan; open folders stay open.</summary>
    internal void Update(FolderNode updated)
    {
        node = updated;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Name));
        var openKeys = IsExpanded ? OpenKeys().ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        childrenBuilt = false;
        Children.Clear();
        if (node.Children.Count > 0) Children.Add(Placeholder);
        if (IsExpanded)
        {
            BuildChildren();
            foreach (var child in Children)
                child.Reopen(openKeys);
        }
    }

    private IEnumerable<string> OpenKeys()
    {
        foreach (var child in Children.Where(child => child != Placeholder && child.IsExpanded))
        {
            yield return child.Key;
            foreach (var key in child.OpenKeys()) yield return key;
        }
    }

    private void Reopen(HashSet<string> keys)
    {
        if (!keys.Contains(Key)) return;
        IsExpanded = true;
        foreach (var child in Children) child.Reopen(keys);
    }

    internal void RefreshName() => OnPropertyChanged(nameof(Name));
}
