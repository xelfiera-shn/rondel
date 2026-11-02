namespace Rondel.Core.Prompts;

/// <summary>
/// Saved prompts: every .md file inside <see cref="Directory"/> is a prompt, named after its file and sent as written.
/// When they are turned on (<see cref="Settings.SavedPromptsOptions"/>), the pinned ones sit on an arc around the Ask
/// slices and the prompt card lists them all.
/// </summary>
public sealed class SavedPrompts(string directory)
{
    public const string Extension = ".md";

    /// <summary>How many prompts can be pinned to the Ask slices' arc.</summary>
    public const int MaxPinned = 5;

    /// <summary>A larger file is not a prompt anyone wrote by hand: it is not read.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>Prompts in the settings folder (%APPDATA%\Rondel\Prompts on Windows).</summary>
    public static SavedPrompts Default { get; } = new(Path.Combine(AppInfo.SettingsDirectory, "Prompts"));

    public string Directory { get; } = directory;

    /// <summary>Prompt names in a stable order (the order they are offered); empty when there are none.</summary>
    public IReadOnlyList<string> List()
    {
        try
        {
            return new DirectoryInfo(Directory).EnumerateFiles("*" + Extension)
                .Where(file => !file.Name.StartsWith('.') && string.Equals(file.Extension, Extension, StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetFileNameWithoutExtension(file.Name))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The prompts of <paramref name="pinned"/> whose files are still there, in the order given and named as their files
    /// are, at most <see cref="MaxPinned"/>.
    /// </summary>
    public IReadOnlyList<string> Existing(IEnumerable<string> pinned)
    {
        var names = List();
        return pinned
            .Select(pin => names.FirstOrDefault(name => string.Equals(name, pin, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPinned)
            .ToList();
    }

    /// <summary>
    /// The prompt's text without the blank lines around it; null when its file is missing, cannot be read, is empty or is
    /// larger than <see cref="MaxBytes"/>.
    /// </summary>
    public string? Read(string name)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        var file = new FileInfo(Path.Combine(Directory, name + Extension));
        try
        {
            if (!file.Exists || file.Length > MaxBytes)
                return null;
            string text = File.ReadAllText(file.FullName).Trim();
            return text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
