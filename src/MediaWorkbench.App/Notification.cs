using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaWorkbench.App;

public enum NotificationKind { Info, Success, Error }

/// <summary>A non-modal message shown over the preview. Errors stay until dismissed; other kinds fade after a few seconds.</summary>
public sealed partial class Notification(NotificationKind kind, string message, string? actionLabel = null, Action? action = null) : ObservableObject
{
    public NotificationKind Kind { get; } = kind;
    public string Message { get; } = message;
    public string? ActionLabel { get; } = actionLabel;
    public Action? Action { get; } = action;
    public bool HasAction => Action is not null && !string.IsNullOrWhiteSpace(ActionLabel);
    public bool IsError => Kind == NotificationKind.Error;
    public DateTimeOffset Created { get; } = DateTimeOffset.Now;
}

public sealed record RecentEntry(string Name, string Path);
