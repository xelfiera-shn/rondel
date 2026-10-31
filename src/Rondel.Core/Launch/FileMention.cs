namespace Rondel.Core.Launch;

public static class FileMention
{
    /// <summary>
    /// Fills an agent's file mention template (<see cref="Agents.AgentProfile.FileMention"/>): "{file}" becomes the
    /// file's name, in double quotes unless it consists only of letters, digits and ._-(). Claude Code reads
    /// @"Proje Notları.md" as one path; an unquoted name ends at the first space.
    /// </summary>
    public static string Format(string template, string fileName) =>
        template.Replace("{file}", NeedsQuotes(fileName) ? $"\"{fileName}\"" : fileName, StringComparison.Ordinal);

    static bool NeedsQuotes(string name) =>
        name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '(' or ')'));
}
