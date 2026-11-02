namespace Rondel.Core.Context;

/// <summary>Which selected files a menu held on a file is about (Ask About These Files).</summary>
public static class FileSelection
{
    /// <summary>
    /// The files to ask about when <paramref name="held"/> was held while <paramref name="selected"/> were selected:
    /// the selected files in its folder, in the selection's order. The agent starts in that folder and mentions them
    /// by name, so folders and items elsewhere (the Public Desktop on the desktop) are left out. Empty when
    /// <paramref name="held"/> is not selected or no other file is: then the menu is about it alone.
    /// </summary>
    /// <param name="isFile">Whether a path is an existing file (not a folder).</param>
    public static IReadOnlyList<string> Around(string held, IReadOnlyList<string> selected, Func<string, bool> isFile)
    {
        if (!selected.Contains(held, StringComparer.OrdinalIgnoreCase))
            return [];
        string? folder = Path.GetDirectoryName(held);
        var files = selected
            .Where(path => string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase) && isFile(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return files.Count > 1 ? files : [];
    }
}
