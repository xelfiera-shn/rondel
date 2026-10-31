namespace Rondel.Core.Projects;

/// <summary>
/// Starter templates for new projects: every folder inside <see cref="Directory"/> is a template, and its
/// contents are copied into a new project. Several can be combined (a language's files plus a field's
/// instructions); when two have the same file, the later one wins.
/// </summary>
public sealed class ProjectTemplates(string directory)
{
    /// <summary>Templates in the settings folder (%APPDATA%\Rondel\Templates on Windows).</summary>
    public static ProjectTemplates Default { get; } = new(Path.Combine(AppInfo.SettingsDirectory, "Templates"));

    public string Directory { get; } = directory;

    /// <summary>Template names in a stable order (the order they are offered and copied); empty when there are none.</summary>
    public IReadOnlyList<string> List()
    {
        try
        {
            return new DirectoryInfo(Directory).EnumerateDirectories()
                .Where(d => !d.Name.StartsWith('.'))
                .Select(d => d.Name)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Copies the named templates into <paramref name="target"/> in the order given; a later template overwrites
    /// files of an earlier one. A template's own .git folder is left out. Unknown names are skipped.
    /// </summary>
    /// <exception cref="IOException">A file could not be copied.</exception>
    /// <exception cref="UnauthorizedAccessException">A file could not be read or written.</exception>
    public void CopyInto(string target, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            var source = new DirectoryInfo(Path.Combine(Directory, name));
            if (source.Exists && !name.Contains(Path.DirectorySeparatorChar) && !name.Contains(Path.AltDirectorySeparatorChar))
                CopyFolder(source, target);
        }
    }

    static void CopyFolder(DirectoryInfo source, string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (var file in source.EnumerateFiles())
            file.CopyTo(Path.Combine(target, file.Name), overwrite: true);
        foreach (var folder in source.EnumerateDirectories())
        {
            // Links could point anywhere, even back into the template.
            if (!string.Equals(folder.Name, ".git", StringComparison.OrdinalIgnoreCase) && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                CopyFolder(folder, Path.Combine(target, folder.Name));
        }
    }
}
