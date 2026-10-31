using Rondel.Core.Localization;

namespace Rondel.Core.Launch;

/// <summary>
/// Windows naming rules for a new project folder, checked before anything is created so problems
/// show inline. The platform adds its own checks on top (long path support, write permission).
/// </summary>
public static class FolderNameRules
{
    public const int MaxNameLength = 255;

    /// <summary>CreateDirectory's limit without long path support (MAX_PATH minus room for an 8.3 name).</summary>
    public const int ClassicPathLimit = 247;

    public const int LongPathLimit = 32000;

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Path.GetInvalidFileNameChars() on Windows, fixed here so the rules do not depend on the OS running them.</summary>
    static readonly HashSet<char> InvalidChars = [.. "\"<>|:*?\\/", .. Enumerable.Range(0, 32).Select(i => (char)i)];

    /// <param name="maxPathLength"><see cref="ClassicPathLimit"/>, or <see cref="LongPathLimit"/> when long paths are enabled.</param>
    /// <returns>A localized error, or null when <paramref name="name"/> can be used in <paramref name="parent"/>.</returns>
    public static string? Validate(string parent, string name, int maxPathLength = ClassicPathLimit)
    {
        if (name.Length == 0)
            return Loc.Get("Validation.Empty");
        if (name is "." or "..")
            return Loc.Get("Validation.DotNames");

        var invalid = name.Where(InvalidChars.Contains).Distinct().ToList();
        if (invalid.Count > 0)
            return Loc.Format("Validation.InvalidChars", string.Join(" ", invalid.Select(c => c < ' ' ? $"U+{(int)c:X4}" : c.ToString())));

        if (name.EndsWith('.') || name.EndsWith(' '))
            return Loc.Get("Validation.TrailingDotSpace");
        if (name.Length > MaxNameLength)
            return Loc.Format("Validation.NameTooLong", MaxNameLength);

        // "nul.txt" and "CON " are just as reserved as "NUL" and "CON".
        string stem = name.Split('.')[0].TrimEnd(' ');
        if (Reserved.Contains(stem))
            return Loc.Format("Validation.Reserved", stem.ToUpperInvariant());

        string full = Path.Combine(parent, name);
        if (full.Length > maxPathLength)
            return Loc.Format("Validation.TooLong", full.Length, maxPathLength);

        if (Directory.Exists(full) || File.Exists(full))
            return Loc.Format("Validation.Exists", name);
        return null;
    }
}
