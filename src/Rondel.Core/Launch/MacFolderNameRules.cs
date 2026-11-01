using System.Text;
using Rondel.Core.Localization;

namespace Rondel.Core.Launch;

/// <summary>
/// macOS naming rules for a new project folder (APFS and Finder), checked before anything is created so
/// problems show inline. The counterpart of <see cref="FolderNameRules"/>; the platform adds the write check.
/// </summary>
public static class MacFolderNameRules
{
    /// <summary>APFS allows 255 bytes of UTF-8 per name.</summary>
    public const int MaxNameBytes = 255;

    /// <returns>A localized error, or null when <paramref name="name"/> can be used in <paramref name="parent"/>.</returns>
    public static string? Validate(string parent, string name)
    {
        if (name.Length == 0)
            return Loc.Get("Validation.Empty");
        if (name is "." or "..")
            return Loc.Get("Validation.DotNames");

        // '/' separates folders; Finder shows ':' as '/', and the file system stores '/' typed in Finder as ':'.
        var invalid = name.Where(c => c is '/' or ':' || char.IsControl(c)).Distinct().ToList();
        if (invalid.Count > 0)
            return Loc.Format("Validation.InvalidCharsMac", string.Join(" ", invalid.Select(c => char.IsControl(c) ? $"U+{(int)c:X4}" : c.ToString())));

        if (name.StartsWith('.'))
            return Loc.Get("Validation.LeadingDot");
        if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
            return Loc.Format("Validation.NameTooLongBytes", MaxNameBytes);

        string full = Path.Combine(parent, name);
        if (Directory.Exists(full) || File.Exists(full))
            return Loc.Format("Validation.Exists", name);
        return null;
    }
}
