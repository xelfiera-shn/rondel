using System.Collections.Concurrent;
using System.Text;
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
/// session's recorded cwd is checked too. Any failure means "no hints" for the menu, never an error.
/// </summary>
public sealed partial class ClaudeCodeSessionStore : ISessionStore
{
    public const string Id = "claude-code";

    const int MaxEncodedLength = 200;
    const int MaxHeaderChars = 64 * 1024;
    const int TitleScanBytes = 256 * 1024;
    const int MaxParsedLine = 1024 * 1024;
    const int MaxTitleLength = 80;

    readonly string? _projectsDirectory;
    readonly IReadOnlyList<string> _ignoredRoots;

    /// <summary>cwd recorded in each session file; it never changes for a given file.</summary>
    readonly ConcurrentDictionary<string, string> _cwdByFile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Title per session file, valid while the file keeps its length.</summary>
    readonly ConcurrentDictionary<string, (long Length, string? Title)> _titleByFile = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="projectsDirectory">Overrides the location of the "projects" folder (tests).</param>
    /// <param name="ignoredRoots">
    /// Folders whose sessions never count as recent projects; by default the temp folder, where tools
    /// run short-lived sessions.
    /// </param>
    public ClaudeCodeSessionStore(string? projectsDirectory = null, IReadOnlyList<string>? ignoredRoots = null)
    {
        _projectsDirectory = projectsDirectory;
        _ignoredRoots = [.. (ignoredRoots ?? [Path.GetTempPath()]).Select(Normalize)];
    }

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
        if (SessionFiles(directory) is not { } files)
            return null;
        DateTime? last = files.Count == 0 ? null : files.Max(f => f.LastWriteTimeUtc);
        Log.Debug($"Sessions for '{directory}': {files.Count}, last {last:u}");
        return new SessionInfo(files.Count, last);
    }

    public IReadOnlyList<SessionEntry> Recent(string directory, int max)
    {
        if (SessionFiles(directory) is not { } files)
            return [];
        return files
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(max)
            .Select(f => new SessionEntry(Path.GetFileNameWithoutExtension(f.Name), ReadTitle(f), f.LastWriteTimeUtc))
            .ToList();
    }

    public IReadOnlyList<FolderActivity> RecentFolders(int max)
    {
        try
        {
            string projects = ProjectsDirectory;
            if (!Directory.Exists(projects))
                return [];
            // Session folders, newest first by their newest file. Only as many are opened as it takes to fill
            // the list: once it is full, a folder whose newest session is older than the last entry cannot get in.
            var folders = new DirectoryInfo(projects).EnumerateDirectories()
                .Select(folder => folder.GetFiles("*.jsonl", SearchOption.TopDirectoryOnly))
                .Where(files => files.Length > 0)
                .Select(files => (Files: files, Newest: files.Max(f => f.LastWriteTimeUtc)))
                .OrderByDescending(folder => folder.Newest)
                .ToList();

            var result = new List<FolderActivity>();
            foreach (var folder in folders)
            {
                if (result.Count >= max && folder.Newest < result[max - 1].LastActivityUtc)
                    break;
                // One session folder usually holds one working directory, but the lossy encoding can mix several.
                foreach (var group in folder.Files.GroupBy(f => ReadCwd(f) is { } cwd ? Normalize(cwd) : "", StringComparer.OrdinalIgnoreCase))
                {
                    if (group.Key.Length == 0 || IsIgnored(group.Key) || !Directory.Exists(group.Key))
                        continue;
                    var newest = group.MaxBy(f => f.LastWriteTimeUtc)!;
                    Merge(result, new FolderActivity(group.Key, newest.LastWriteTimeUtc, group.Count(), Path.GetFileNameWithoutExtension(newest.Name)));
                }
                result.Sort((a, b) => b.LastActivityUtc.CompareTo(a.LastActivityUtc));
            }
            return result.Take(max).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Log.Debug($"Listing recent folders failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>The same folder can appear under two session folders (e.g. "C:\X" and "c:\x"): count it once.</summary>
    static void Merge(List<FolderActivity> result, FolderActivity folder)
    {
        int existing = result.FindIndex(f => string.Equals(f.Directory, folder.Directory, StringComparison.OrdinalIgnoreCase));
        if (existing < 0)
        {
            result.Add(folder);
            return;
        }
        var other = result[existing];
        var newer = other.LastActivityUtc >= folder.LastActivityUtc ? other : folder;
        result[existing] = newer with { SessionCount = other.SessionCount + folder.SessionCount };
    }

    bool IsIgnored(string directory) =>
        _ignoredRoots.Any(root => directory.Equals(root, StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <returns>The folder's session files, or null when the store is missing or unreadable (unknown, not "none").</returns>
    List<FileInfo>? SessionFiles(string directory)
    {
        try
        {
            string projects = ProjectsDirectory;
            if (!Directory.Exists(projects))
                return null; // Claude Code never ran here, or keeps its data elsewhere

            string target = Normalize(directory);
            string encoded = Encode(target);
            IEnumerable<string> folders = encoded.Length <= MaxEncodedLength
                ? [Path.Combine(projects, encoded)]
                : Directory.EnumerateDirectories(projects, encoded[..MaxEncodedLength] + "-*");

            var files = new List<FileInfo>();
            foreach (string folder in folders.Where(Directory.Exists))
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(file);
                    string? cwd = ReadCwd(info);
                    if (cwd is null || string.Equals(Normalize(cwd), target, StringComparison.OrdinalIgnoreCase))
                        files.Add(info); // a file without a cwd yet is a session that just started
                }
            }
            return files;
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

    /// <summary>
    /// A title for the session picker: a title the user gave, else Claude Code's generated title (the
    /// newest one wins), else an old-style summary, else the first prompt. Only the beginning and the
    /// end of the file are read, because transcripts grow to many megabytes.
    /// </summary>
    string? ReadTitle(FileInfo file)
    {
        if (_titleByFile.TryGetValue(file.FullName, out var cached) && cached.Length == file.Length)
            return cached.Title;
        string? title = null;
        try
        {
            string? custom = null, generated = null, summary = null, firstPrompt = null;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ScanTitles(ReadLines(stream, 0, TitleScanBytes, skipPartialFirst: false), ref custom, ref generated, ref summary, ref firstPrompt);
            if (stream.Length > TitleScanBytes)
            {
                long tailStart = Math.Max(TitleScanBytes, stream.Length - TitleScanBytes);
                ScanTitles(ReadLines(stream, tailStart, TitleScanBytes, skipPartialFirst: true), ref custom, ref generated, ref summary, ref firstPrompt);
            }
            title = Shorten(custom ?? generated ?? summary ?? firstPrompt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Reading the title of '{file.Name}' failed: {ex.Message}");
        }
        _titleByFile[file.FullName] = (file.Length, title);
        return title;
    }

    static IEnumerable<string> ReadLines(FileStream stream, long start, int count, bool skipPartialFirst)
    {
        stream.Position = start;
        var buffer = new byte[(int)Math.Min(count, stream.Length - start)];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n');
        // The first line of a tail chunk and the last line of any chunk may be cut off.
        return lines.Skip(skipPartialFirst ? 1 : 0).Take(lines.Length - (skipPartialFirst ? 1 : 0) - 1);
    }

    static void ScanTitles(IEnumerable<string> lines, ref string? custom, ref string? generated, ref string? summary, ref string? firstPrompt)
    {
        foreach (string line in lines)
        {
            // Cheap text check first; only candidate lines are parsed.
            if (line.Length > MaxParsedLine || !TypePattern().IsMatch(line))
                continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "custom-title":
                        custom = StringField(root, "customTitle") ?? custom;
                        break;
                    case "ai-title":
                        generated = StringField(root, "aiTitle") ?? generated;
                        break;
                    case "summary":
                        summary ??= StringField(root, "summary");
                        break;
                    case "user" when firstPrompt is null:
                        firstPrompt = PromptText(root);
                        break;
                }
            }
            catch (JsonException)
            {
                // A line being written right now, or a format change: skip it.
            }
        }
    }

    static string? StringField(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Text the user typed; command output, reminders and meta lines are not prompts.</summary>
    static string? PromptText(JsonElement root)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
            return null;
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
            return null;
        string? text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => content.EnumerateArray()
                .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out _))
                .Select(b => b.GetProperty("text").GetString())
                .FirstOrDefault(),
            _ => null,
        };
        text = text?.Trim();
        return string.IsNullOrEmpty(text) || text.StartsWith('<') ? null : text;
    }

    static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string single = WhitespacePattern().Replace(text.Trim(), " ");
        return single.Length <= MaxTitleLength ? single : single[..(MaxTitleLength - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex("\"cwd\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex CwdPattern();

    [GeneratedRegex("\"type\"\\s*:\\s*\"(custom-title|ai-title|summary|user)\"")]
    private static partial Regex TypePattern();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespacePattern();
}
