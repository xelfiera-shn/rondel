namespace Rondel.Core.Context;

public enum ContextKind
{
    /// <summary>Not something Rondel handles - the click is passed through.</summary>
    None,
    DesktopEmpty,
    ExplorerEmpty,
    FolderItem,
}

/// <summary>Result of classifying the point under the cursor.</summary>
/// <param name="TargetDir">Folder the actions work on (null only for <see cref="ContextKind.None"/>).</param>
/// <param name="DisplayName">Name shown in the menu center.</param>
/// <param name="Reason">Short explanation for the log.</param>
public sealed record TriggerContext(ContextKind Kind, string? TargetDir, string? DisplayName, string Reason)
{
    /// <summary>Debug details (UI Automation ancestor chain etc.), only collected when debug logging is on.</summary>
    public IReadOnlyList<string> Trace { get; init; } = [];

    public static TriggerContext None(string reason) => new(ContextKind.None, null, null, reason);
}
