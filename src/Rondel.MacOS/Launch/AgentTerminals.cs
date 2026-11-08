using System.Diagnostics;
using Rondel.Core.Logging;
using Rondel.MacOS.Interop;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Launch;

/// <summary>
/// The terminal of each followed agent, so a notification or the menu can bring it to the front. The launch script
/// writes its tty into a file named after the session (<see cref="TtyLine"/>); Terminal and iTerm2 then select the tab
/// on that tty and bring its window to the front through AppleScript. That needs the Automation permission for the
/// terminal, which macOS asks for the first time an agent is brought to the front.
/// </summary>
/// <remarks>
/// macOS does not let one process read another's environment, so the session cannot be found from Claude Code's own
/// process. A tab is closed when no process uses its tty any more. The terminal app is written next to the tty, so a
/// restarted Rondel, which follows a session again at its next event, still finds its tab.
/// </remarks>
internal sealed class AgentTerminals
{
    const string TerminalId = "com.apple.Terminal";

    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "Rondel", "agents");

    readonly Lock _gate = new();

    /// <summary>The terminal app (bundle id) each followed session was started in.</summary>
    readonly Dictionary<string, string> _terminals = [];

    /// <summary>Takes away what sessions that were never followed again left, a week after they started.</summary>
    public AgentTerminals()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;
            foreach (var file in new DirectoryInfo(Folder).EnumerateFiles())
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromDays(7))
                    file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Cleaning {Folder} failed: {ex.Message}");
        }
    }

    /// <summary>The line the launch script runs first: it writes the script's tty into the session's file.</summary>
    public static string TtyLine(string activityId)
    {
        Directory.CreateDirectory(Folder);
        return $"tty > '{TtyFile(activityId)}' 2>/dev/null\n";
    }

    static string TtyFile(string activityId) => Path.Combine(Folder, activityId + ".tty");

    static string TerminalFile(string activityId) => Path.Combine(Folder, activityId + ".terminal");

    /// <summary>A followed session was started in <paramref name="terminal"/> ("Terminal" or "iTerm").</summary>
    public void Started(string activityId, string terminal)
    {
        string id = terminal == "iTerm" ? MacTerminalLauncher.ITermId : TerminalId;
        lock (_gate)
            _terminals[activityId] = id;
        try
        {
            File.WriteAllText(TerminalFile(activityId), id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Remembering the terminal of {activityId} failed: {ex.Message}");
        }
    }

    public void Forget(string activityId)
    {
        lock (_gate)
            _terminals.Remove(activityId);
        try
        {
            File.Delete(TtyFile(activityId));
            File.Delete(TerminalFile(activityId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Removing the tty file of {activityId} failed: {ex.Message}");
        }
    }

    (string Terminal, string Tty)? Find(string activityId)
    {
        string? terminal;
        lock (_gate)
            _terminals.TryGetValue(activityId, out terminal);
        try
        {
            if (terminal is null && File.Exists(TerminalFile(activityId)))
            {
                // Started before Rondel restarted.
                terminal = File.ReadAllText(TerminalFile(activityId)).Trim();
                lock (_gate)
                    _terminals[activityId] = terminal;
            }
            if (terminal is null)
                return null;
            string tty = File.ReadAllText(TtyFile(activityId)).Trim();
            return tty.StartsWith("/dev/", StringComparison.Ordinal) ? (terminal, tty) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Brings the agent's tab to the front, asking for the Automation permission the first time. Blocks while macOS asks:
    /// call it off the main thread.
    /// </summary>
    /// <returns>False when its tab was not found (closed, or the permission was refused).</returns>
    public bool Focus(string activityId)
    {
        if (Find(activityId) is not var (terminal, tty))
        {
            Log.Info($"Agent {activityId}: its terminal is not known");
            return false;
        }
        int permission = AutomationPermission(terminal, ask: true);
        if (permission != AutomationAllowed)
        {
            Log.Warn($"Agent {activityId}: no Automation permission for {terminal} ({permission})");
            return false;
        }
        string? answer = AppleScript.Run(FocusScript(terminal, tty), out string? error);
        if (answer != "ok")
            Log.Info($"Agent {activityId}: no tab on {tty} in {terminal} ({answer ?? error})");
        return answer == "ok";
    }

    /// <summary>
    /// Whether the agent's tab is the one in front, so a notification about it would only repeat what is on the screen.
    /// Asks the terminal only when the permission is already given; otherwise false.
    /// </summary>
    public bool IsForeground(string activityId)
    {
        if (Find(activityId) is not var (terminal, tty) || Workspace.FrontmostBundleId() != terminal
            || AutomationPermission(terminal, ask: false) != AutomationAllowed)
        {
            return false;
        }
        string source = terminal == TerminalId
            ? "tell application id \"com.apple.Terminal\" to return tty of selected tab of front window"
            : $"tell application id \"{MacTerminalLauncher.ITermId}\" to return tty of current session of current window";
        return AppleScript.Run(source, out _) == tty;
    }

    /// <summary>Those of <paramref name="activityIds"/> whose tab was closed: no process uses its tty any more.</summary>
    public IReadOnlyList<string> Closed(IEnumerable<string> activityIds)
    {
        List<(string Id, string Tty)> known = [.. activityIds.Select(id => (id, Find(id)?.Tty)).Where(k => k.Item2 is not null).Select(k => (k.id, k.Item2!))];
        if (known.Count == 0)
            return [];
        var inUse = TtysInUse();
        return inUse is null ? [] : [.. known.Where(k => !inUse.Contains(k.Tty)).Select(k => k.Id)];
    }

    /// <summary>The ttys some process uses (from ps), as /dev paths; null when ps could not tell.</summary>
    static HashSet<string>? TtysInUse()
    {
        try
        {
            var start = new ProcessStartInfo("/bin/ps", ["-A", "-o", "tty="]) { UseShellExecute = false, RedirectStandardOutput = true };
            using var process = Process.Start(start);
            if (process is null)
                return null;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.StartsWith("tty", StringComparison.Ordinal)).Select(t => "/dev/" + t)];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn("Listing the terminals in use failed", ex);
            return null;
        }
    }

    /// <remarks>
    /// Windows and tabs are walked by their index: a loop over "windows" hands out references Terminal cannot read the
    /// tabs of ("Can't get every tab of item 1 of every window"). A window without tabs (Settings) is skipped.
    /// </remarks>
    static string FocusScript(string terminal, string tty) => terminal == TerminalId
        ? $$"""
            tell application id "com.apple.Terminal"
                repeat with i from 1 to count of windows
                    try
                        set w to window i
                        repeat with j from 1 to count of tabs of w
                            if tty of tab j of w is "{{tty}}" then
                                set selected of tab j of w to true
                                set index of w to 1
                                activate
                                return "ok"
                            end if
                        end repeat
                    end try
                end repeat
                return "none"
            end tell
            """
        : $$"""
            tell application id "{{MacTerminalLauncher.ITermId}}"
                repeat with i from 1 to count of windows
                    try
                        set w to window i
                        repeat with j from 1 to count of tabs of w
                            set t to tab j of w
                            repeat with k from 1 to count of sessions of t
                                set s to session k of t
                                if tty of s is "{{tty}}" then
                                    tell w to select
                                    tell t to select
                                    tell s to select
                                    activate
                                    return "ok"
                                end if
                            end repeat
                        end repeat
                    end try
                end repeat
                return "none"
            end tell
            """;
}
