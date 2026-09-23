using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaWorkbench.App;

public sealed partial class MetadataRow(string name, string value, bool canTag = true) : ObservableObject
{
    public string Name { get; } = name;
    public string Value { get; } = value;
    public bool CanTag { get; } = canTag && value.Length <= 140;
    public string Tag => $"{Name.ToLowerInvariant()}:{Value}";
    [ObservableProperty] private bool isSelected;
}

/// <summary>A collection in the lists; kept as the same object while its file count changes, so lists never refill.</summary>
public sealed partial class CollectionItem(string name, string filePath) : ObservableObject
{
    public string Name { get; } = name;
    public string FilePath { get; } = filePath;
    [ObservableProperty] private int count;
    public override string ToString() => Name;
}
