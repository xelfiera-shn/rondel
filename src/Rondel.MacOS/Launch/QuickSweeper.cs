using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Flow;
using Rondel.Core.Logging;
using Rondel.MacOS.Interop;

namespace Rondel.MacOS.Launch;

/// <summary>
/// Quick Conversation: what Claude Code keeps of a session there is deleted for good once it is over. On macOS it
/// sweeps at its start, before each new Quick Conversation, and from the first one on every two minutes, rather than
/// when it hears that one ended, as Windows does. A session Claude Code still runs, or one
/// written to in the last minute, is kept until a later sweep. Only Claude Code's list of the sessions it runs
/// (~/.claude/sessions) tells one that still runs from one that ended, so without that list nothing is swept.
/// </summary>
internal sealed class QuickSweeper : IDisposable
{
    static readonly TimeSpan Every = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Where Claude Code keeps its temporary folders on macOS (a session's scratchpad among them); the core knows only
    /// the Windows one. A CLAUDE_CODE_TMPDIR set in the shell does not reach Rondel, so a folder under it stays.
    /// </summary>
    static readonly string TempDirectory = $"/tmp/claude-{Native.UserId()}";

    readonly string _directory;
    readonly object _lock = new();
    Timer? _timer;
    bool _toldNoRegistry;

    public QuickSweeper(MenuFlow flow)
    {
        _directory = flow.QuickDirectory;
        flow.QuickConversationStarting += () =>
        {
            Sweep();
            _timer ??= new Timer(_ => Sweep(), null, Every, Every);
        };
    }

    /// <summary>Sweeps what Quick Conversations left while Rondel was not running.</summary>
    public void Start() => Sweep();

    /// <summary>Deletes, off the main thread, what Quick Conversations that no longer run left behind.</summary>
    void Sweep() => Task.Run(() =>
    {
        lock (_lock)
        {
            try
            {
                var storage = new ClaudeCodeStorage(tempDirectory: TempDirectory);
                if (!Directory.Exists(Path.Combine(storage.ConfigDirectory, "sessions")))
                {
                    if (!_toldNoRegistry)
                        Log.Info("Claude Code lists no running sessions; Quick Conversations are not swept");
                    _toldNoRegistry = true;
                    return;
                }
                _toldNoRegistry = false;
                storage.Sweep(_directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Warn("Sweeping the Quick Conversation folder failed", ex);
            }
        }
    });

    public void Dispose() => _timer?.Dispose();
}
