namespace Rondel.Core.Platform;

/// <summary>
/// Checks the name of a new project folder before anything is created, so problems show inline while
/// the user types. Each platform applies its own file system's rules.
/// </summary>
public interface IFolderNameValidator
{
    /// <returns>A localized error, or null when <paramref name="name"/> can be created in <paramref name="parent"/>.</returns>
    string? Validate(string parent, string name);
}
