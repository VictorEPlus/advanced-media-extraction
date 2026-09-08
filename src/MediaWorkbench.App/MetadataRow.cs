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

public sealed record CollectionItem(string Name, string FilePath)
{
    public override string ToString() => Name;
}
