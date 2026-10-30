using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rondel.Core.Logging;
using Rondel.Core.Sessions;

namespace Rondel.Core.Agents.ClaudeCode;

/// <summary>
/// Best-effort reader of Claude Code's session store. Claude Code keeps each session as
/// ~/.claude/projects/&lt;encoded folder&gt;/&lt;id&gt;.jsonl, where the encoding replaces every character
/// other than A-Z, a-z, 0-9 with '-' and names longer than 200 characters are cut to 200 plus
/// "-&lt;hash&gt;". The encoding is lossy ("a.b" and "a-b" collide, as do all Turkish letters), so each
/// session's recorded cwd is checked too. Any failure returns null and the menu simply shows no hints.
/// </summary>
public sealed partial class ClaudeCodeSessionStore : ISessionStore
{
    public const string Id = "claude-code";

    const int MaxEncodedLength = 200;
    const int MaxHeaderChars = 64 * 1024;

    readonly string? _projectsDirectory;

    /// <summary>cwd recorded in each session file; it never changes for a given file.</summary>
    readonly ConcurrentDictionary<string, string> _cwdByFile = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="projectsDirectory">Overrides the location of the "projects" folder (tests).</param>
    public ClaudeCodeSessionStore(string? projectsDirectory = null) => _projectsDirectory = projectsDirectory;

    public string ProjectsDirectory => _projectsDirectory ?? DefaultProjectsDirectory();

    /// <summary>Claude Code's folder-name encoding for a working directory.</summary>
    public static string Encode(string directory)
    {
        var chars = directory.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]))
                chars[i] = '-';
        }
        return new string(chars);
    }

    public SessionInfo? Lookup(string directory)
    {
        try
        {
            string projects = ProjectsDirectory;
            if (!Directory.Exists(projects))
                return null; // Claude Code never ran here, or keeps its data elsewhere: unknown, not "none"

            string target = Normalize(directory);
            string encoded = Encode(target);
            IEnumerable<string> folders = encoded.Length <= MaxEncodedLength
                ? [Path.Combine(projects, encoded)]
                : Directory.EnumerateDirectories(projects, encoded[..MaxEncodedLength] + "-*");

            int count = 0;
            DateTime? last = null;
            foreach (string folder in folders.Where(Directory.Exists))
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(file);
                    string? cwd = ReadCwd(info);
                    if (cwd is not null && !string.Equals(Normalize(cwd), target, StringComparison.OrdinalIgnoreCase))
                        continue; // another folder whose name encodes the same way
                    count++;
                    if (last is null || info.LastWriteTimeUtc > last)
                        last = info.LastWriteTimeUtc;
                }
            }
            Log.Debug($"Sessions for '{target}' (folder '{encoded}'): {count}, last {last:u}");
            return new SessionInfo(count, last);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Log.Debug($"Session lookup for '{directory}' failed: {ex.Message}");
            return null;
        }
    }

    static string DefaultProjectsDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : Environment.ExpandEnvironmentVariables(configured.Trim());
        return Path.Combine(root, "projects");
    }

    static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>The first "cwd" recorded in a session file (only the first few lines are read).</summary>
    string? ReadCwd(FileInfo file)
    {
        if (_cwdByFile.TryGetValue(file.FullName, out string? cached))
            return cached;
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        int read = 0;
        while (read < MaxHeaderChars && reader.ReadLine() is { } line)
        {
            read += line.Length;
            var match = CwdPattern().Match(line);
            if (!match.Success)
                continue;
            string cwd = JsonSerializer.Deserialize<string>($"\"{match.Groups[1].Value}\"") ?? "";
            _cwdByFile[file.FullName] = cwd;
            return cwd;
        }
        return null; // not written yet (brand-new session) - don't cache
    }

    [GeneratedRegex("\"cwd\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex CwdPattern();
}
