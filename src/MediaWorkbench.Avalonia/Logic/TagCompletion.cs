namespace MediaWorkbench.Avalonia;

/// <summary>
/// Suggestions while typing tags in a comma-separated box: only the tag being typed (after the last comma) is matched against
/// the tags already in use, and picking one replaces just that part, ready for the next tag.
/// </summary>
public static class TagCompletion
{
    /// <summary>The tag being typed: whatever follows the last comma, trimmed.</summary>
    public static string Current(string? text) => text is null ? "" : text[(text.LastIndexOf(',') + 1)..].Trim();

    /// <summary>The tags already finished in the box, before the one being typed.</summary>
    public static string[] Finished(string? text)
    {
        var comma = text?.LastIndexOf(',') ?? -1;
        return comma < 0 ? [] : text![..comma].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Whether <paramref name="tag"/> should be offered for <paramref name="text"/>: it contains what is being typed and is not already in the box.</summary>
    public static bool Matches(string? text, string? tag)
    {
        var current = Current(text);
        return tag is not null && current.Length > 0 && tag.Contains(current, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tag, current, StringComparison.OrdinalIgnoreCase)
            && !Finished(text).Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The box's text once <paramref name="tag"/> is picked: the finished tags, the picked one, and a comma for the next.</summary>
    public static string Complete(string? text, string? tag) =>
        tag is null ? text ?? "" : string.Join(", ", [.. Finished(text), tag]) + ", ";
}
