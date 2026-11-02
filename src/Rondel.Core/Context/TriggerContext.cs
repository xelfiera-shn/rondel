namespace Rondel.Core.Context;

public enum ContextKind
{
    /// <summary>Not something Rondel handles - the click is passed through.</summary>
    None,
    DesktopEmpty,
    ExplorerEmpty,
    FolderItem,

    /// <summary>A file: the actions work in its folder, and some mention the file (<see cref="TriggerContext.FilePath"/>).</summary>
    FileItem,
}

/// <summary>Result of classifying the point under the cursor.</summary>
/// <param name="TargetDir">Folder the actions work on (null only for <see cref="ContextKind.None"/>).</param>
/// <param name="DisplayName">Name shown in the menu center.</param>
/// <param name="Reason">Short explanation for the log.</param>
public sealed record TriggerContext(ContextKind Kind, string? TargetDir, string? DisplayName, string Reason)
{
    /// <summary>The file under the cursor, for <see cref="ContextKind.FileItem"/>; <see cref="TargetDir"/> is its folder.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// When the file under the cursor is one of several selected files: all of them (<see cref="FilePath"/> among
    /// them), in the file manager's order; see <see cref="FileSelection.Around"/>. Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> SelectedFiles { get; init; } = [];

    /// <summary>The files the menu is about: the selection the file under the cursor belongs to, or that file alone.</summary>
    public IReadOnlyList<string> Files => SelectedFiles.Count > 1 ? SelectedFiles : FilePath is { } file ? [file] : [];

    /// <summary>Debug details (UI Automation ancestor chain etc.), only collected when debug logging is on.</summary>
    public IReadOnlyList<string> Trace { get; init; } = [];

    public static TriggerContext None(string reason) => new(ContextKind.None, null, null, reason);
}
