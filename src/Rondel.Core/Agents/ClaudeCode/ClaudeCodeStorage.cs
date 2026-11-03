using System.Diagnostics;
using System.Text.Json;
using Rondel.Core.Logging;

namespace Rondel.Core.Agents.ClaudeCode;

/// <summary>A saved Claude Code session, as the Sessions window lists it.</summary>
/// <param name="Title">The session's title or first prompt; null when the transcript has none yet.</param>
/// <param name="Bytes">Everything it takes on disk: the transcript and the files kept for it.</param>
/// <param name="Running">A Claude Code works in it, or wrote to it a moment ago: it is not deleted.</param>
public sealed record StoredSession(string Id, string? Title, DateTime LastActivityUtc, long Bytes, bool Running);

/// <summary>A folder of ~/.claude/projects: the project it is for, its sessions (newest first) and its auto memory.</summary>
/// <param name="Key">The folder's name: Claude Code's encoding of the project's path.</param>
/// <param name="Directory">The project's path as its sessions recorded it; null when none says.</param>
/// <param name="MemoryBytes">Claude's auto memory for the project (its memory folder); 0 when there is none.</param>
public sealed record StoredProject(string Key, string? Directory, bool DirectoryExists, long MemoryBytes, IReadOnlyList<StoredSession> Sessions)
{
    public long Bytes => Sessions.Sum(s => s.Bytes);

    public DateTime? LastActivityUtc => Sessions.Count == 0 ? null : Sessions.Max(s => s.LastActivityUtc);
}

/// <summary>What to delete in one project: some of its sessions, and its auto memory or not.</summary>
public sealed record CleanupRequest(string ProjectKey, IReadOnlyList<string> SessionIds, bool Memory);

/// <param name="Skipped">Sessions left alone because a Claude Code works in them by now.</param>
public sealed record CleanupResult(int Sessions, int Memories, long Bytes, IReadOnlyList<string> Skipped);

/// <summary>
/// Claude Code's saved sessions on disk, for listing and deleting them. A session is its transcript
/// (projects/&lt;project&gt;/&lt;id&gt;.jsonl) and what Claude Code keeps for it elsewhere: transcripts it set aside, a folder of
/// subagent transcripts and tool results, file history, environment, debug logs, images, uploads, task list and its
/// temporary folder with the scratchpad. Only those known paths are touched, and a project's auto memory only when
/// asked for. It is another program's internal layout: what is not found is simply not there.
/// </summary>
public sealed class ClaudeCodeStorage
{
    /// <summary>A transcript written to this recently belongs to a session in use, even if no Claude Code says so.</summary>
    public static readonly TimeSpan InUse = TimeSpan.FromMinutes(2);

    /// <summary>How long Claude Code keeps sessions when its settings do not say (cleanupPeriodDays).</summary>
    public const int DefaultCleanupDays = 30;

    /// <summary>Folders of ~/.claude that hold one entry per session, named after it.</summary>
    static readonly string[] PerSession = ["file-history", "session-env", "image-cache", "uploads", "dev-mods", "tasks"];

    static readonly EnumerationOptions Everything = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    readonly ClaudeCodeSessionStore _store;

    /// <param name="configDirectory">Claude Code's folder; ~/.claude (or CLAUDE_CONFIG_DIR) by default.</param>
    /// <param name="tempDirectory">Where Claude Code keeps each session's temporary folder; null: the usual place on Windows.</param>
    public ClaudeCodeStorage(string? configDirectory = null, string? tempDirectory = null)
    {
        ConfigDirectory = configDirectory ?? ClaudeCodeSessionStore.DefaultConfigDirectory();
        TempDirectory = tempDirectory ?? DefaultTempDirectory();
        _store = new ClaudeCodeSessionStore(ProjectsDirectory, ignoredRoots: []);
    }

    public string ConfigDirectory { get; }

    /// <summary>Null when it is not known (outside Windows): temporary folders are then left to the system.</summary>
    public string? TempDirectory { get; }

    string ProjectsDirectory => Path.Combine(ConfigDirectory, "projects");

    /// <summary>
    /// After how many days Claude Code deletes sessions by itself, as the user's settings say. Managed settings can set
    /// another period; this is what the user's own file says.
    /// </summary>
    public int CleanupPeriodDays()
    {
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(ConfigDirectory, "settings.json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (settings.RootElement.ValueKind == JsonValueKind.Object
                && settings.RootElement.TryGetProperty("cleanupPeriodDays", out var days)
                && days.TryGetInt32(out int value) && value >= 1)
                return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return DefaultCleanupDays;
    }

    /// <summary>Every project with sessions or memory, the most recently worked in first. Blocking file I/O.</summary>
    public IReadOnlyList<StoredProject> List()
    {
        var running = RunningSessions();
        var projects = new List<StoredProject>();
        try
        {
            if (!Directory.Exists(ProjectsDirectory))
                return [];
            foreach (var folder in new DirectoryInfo(ProjectsDirectory).EnumerateDirectories())
            {
                if (Read(folder, running) is { } project)
                    projects.Add(project);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Listing Claude Code's sessions in '{ProjectsDirectory}' failed", ex);
        }
        return projects
            .OrderByDescending(p => p.LastActivityUtc ?? DateTime.MinValue)
            .ToList();
    }

    StoredProject? Read(DirectoryInfo folder, HashSet<string> running)
    {
        var transcripts = folder.GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(file => IsSessionId(Path.GetFileNameWithoutExtension(file.Name)))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
        var now = DateTime.UtcNow;
        var sessions = transcripts.Select(file =>
        {
            string id = Path.GetFileNameWithoutExtension(file.Name);
            long bytes = PathsOf(folder.Name, id).Sum(SizeOf);
            return new StoredSession(id, _store.ReadTitle(file), file.LastWriteTimeUtc, bytes,
                running.Contains(id) || now - file.LastWriteTimeUtc < InUse);
        }).ToList();
        long memory = SizeOf(Path.Combine(folder.FullName, "memory"));
        if (sessions.Count == 0 && memory == 0)
            return null;
        string? directory = transcripts.Select(SafeCwd).FirstOrDefault(cwd => !string.IsNullOrEmpty(cwd));
        return new StoredProject(folder.Name, directory, directory is not null && Directory.Exists(directory), memory, sessions);
    }

    string? SafeCwd(FileInfo file)
    {
        try
        {
            return _store.ReadCwd(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes what <paramref name="requests"/> ask for: into the Recycle Bin through <paramref name="recycle"/>, or for
    /// good without it. A session a Claude Code works in by now is skipped. A project folder left empty goes too.
    /// </summary>
    /// <exception cref="IOException">The Recycle Bin refused the files (from <paramref name="recycle"/>).</exception>
    public CleanupResult Delete(IReadOnlyList<CleanupRequest> requests, Action<IReadOnlyList<string>>? recycle)
    {
        var running = RunningSessions();
        var now = DateTime.UtcNow;
        var paths = new List<string>();
        var skipped = new List<string>();
        var projects = new List<string>();
        int sessions = 0, memories = 0;
        foreach (var request in requests)
        {
            if (!IsFolderName(request.ProjectKey) || !Directory.Exists(Path.Combine(ProjectsDirectory, request.ProjectKey)))
                continue;
            projects.Add(request.ProjectKey);
            foreach (string id in request.SessionIds.Where(IsSessionId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var transcript = new FileInfo(Path.Combine(ProjectsDirectory, request.ProjectKey, id + ".jsonl"));
                if (running.Contains(id) || (transcript.Exists && now - transcript.LastWriteTimeUtc < InUse))
                {
                    skipped.Add(id);
                    continue;
                }
                var found = PathsOf(request.ProjectKey, id).Where(Exists).ToList();
                if (found.Count == 0)
                    continue;
                paths.AddRange(found);
                sessions++;
            }
            string memory = Path.Combine(ProjectsDirectory, request.ProjectKey, "memory");
            if (request.Memory && Directory.Exists(memory))
            {
                paths.Add(memory);
                memories++;
            }
        }

        long bytes = paths.Sum(SizeOf);
        if (paths.Count > 0)
        {
            Log.Info($"Deleting {sessions} session(s) and {memories} memory folder(s), {bytes} bytes, {(recycle is null ? "for good" : "into the Recycle Bin")}"
                + (skipped.Count > 0 ? $"; skipped {skipped.Count} in use" : ""));
            if (recycle is not null)
                recycle(paths);
            else
                paths.ForEach(DeleteForGood);
        }
        // Folders that held only what went: the project's own, and its temporary one.
        foreach (string key in projects)
        {
            RemoveIfEmpty(Path.Combine(ProjectsDirectory, key));
            if (TempDirectory is { } temp)
                RemoveIfEmpty(Path.Combine(temp, key));
        }
        return new CleanupResult(sessions, memories, bytes, skipped);
    }

    /// <summary>Every path that may belong to session <paramref name="id"/> of project <paramref name="key"/>, found or not.</summary>
    internal IEnumerable<string> PathsOf(string key, string id)
    {
        string project = Path.Combine(ProjectsDirectory, key);
        yield return Path.Combine(project, id + ".jsonl");
        foreach (string file in FilesStartingWith(project, id + "."))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith(id + ".orphaned-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(id + ".jsonl.superseded-", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
        yield return Path.Combine(project, id);
        foreach (string folder in PerSession)
            yield return Path.Combine(ConfigDirectory, folder, id);
        foreach (string file in FilesStartingWith(Path.Combine(ConfigDirectory, "debug"), id))
            yield return file;
        if (TempDirectory is { } temp)
            yield return Path.Combine(temp, key, id);
    }

    /// <summary>The ids of the sessions a Claude Code that still runs has open (~/.claude/sessions/&lt;pid&gt;.json).</summary>
    HashSet<string> RunningSessions()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string folder = Path.Combine(ConfigDirectory, "sessions");
        try
        {
            if (!Directory.Exists(folder))
                return ids;
            foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    using var state = JsonDocument.Parse(File.ReadAllText(file));
                    var root = state.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("sessionId", out var id) && id.GetString() is { } session
                        && root.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out int process) && IsAlive(process))
                        ids.Add(session);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return ids;
    }

    static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Claude Code names sessions with GUIDs; nothing else is taken for one, so no other file can be reached.</summary>
    static bool IsSessionId(string name) => Guid.TryParseExact(name, "D", out _);

    static bool IsFolderName(string name) =>
        name.Length > 0 && name != "." && name != ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    static IEnumerable<string> FilesStartingWith(string folder, string prefix)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, prefix + "*").ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;
            if (Directory.Exists(path))
                return new DirectoryInfo(path).EnumerateFiles("*", Everything).Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return 0;
    }

    static void DeleteForGood(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true); // links inside are removed, never followed
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Deleting '{path}' failed", ex);
        }
    }

    static void RemoveIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>%TEMP%\claude on Windows (or under CLAUDE_CODE_TMPDIR); elsewhere Claude Code names it per user, so it is left out.</summary>
    static string? DefaultTempDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        string? configured = Environment.GetEnvironmentVariable("CLAUDE_CODE_TMPDIR");
        return Path.Combine(string.IsNullOrWhiteSpace(configured) ? Path.GetTempPath() : configured.Trim(), "claude");
    }
}
