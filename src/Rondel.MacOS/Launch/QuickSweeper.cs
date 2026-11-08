using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Flow;
using Rondel.Core.Logging;
using Rondel.MacOS.Interop;

namespace Rondel.MacOS.Launch;

/// <summary>
/// Quick Conversation: what Claude Code keeps of a session there is deleted for good once it is over. Rondel does not
/// follow agents on macOS yet (C-24), so it never hears that one ended, as Windows does: it sweeps at its start, before
/// each new Quick Conversation, and from the first one on every two minutes. A session Claude Code still runs, or one
/// written to in the last minute, is kept until a later sweep. Only Claude Code's list of the sessions it runs
/// (~/.claude/sessions) tells one that still runs from one that ended, so without that list nothing is swept.
/// An answer on an open card is kept too, and so are the folder's files while one is open; a card closed with its answer
/// ends its session at once. One continued in a terminal is kept by the flow until Claude Code lists it as running.
/// </summary>
internal sealed class QuickSweeper : IDisposable
{
    static readonly TimeSpan Every = TimeSpan.FromMinutes(2);

    /// <summary>A session that has just ended may still be closing (Claude Code writes as it exits): swept once more after this.</summary>
    static readonly TimeSpan SweepAgain = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Where Claude Code keeps its temporary folders on macOS (a session's scratchpad among them); the core knows only
    /// the Windows one. A CLAUDE_CODE_TMPDIR set in the shell does not reach Rondel, so a folder under it stays.
    /// </summary>
    static readonly string TempDirectory = $"/tmp/claude-{Native.UserId()}";

    readonly MenuFlow _flow;
    readonly string _directory;
    readonly HashSet<string> _ended = new(StringComparer.OrdinalIgnoreCase);
    readonly object _lock = new();
    Timer? _timer;
    bool _toldNoRegistry;

    public QuickSweeper(MenuFlow flow)
    {
        _flow = flow;
        _directory = flow.QuickDirectory;
        flow.QuickConversationStarting += () =>
        {
            Sweep();
            _timer ??= new Timer(_ => Sweep(), null, Every, Every);
        };
        // A card closed with its answer: that session is over, even though no terminal ran it.
        flow.QuickSessionEnded += session =>
        {
            Ended(session);
            Task.Delay(SweepAgain).ContinueWith(_ => Ended(session), TaskScheduler.Default);
        };
    }

    void Ended(string session)
    {
        lock (_lock)
            _ended.Add(session);
        Sweep();
    }

    /// <summary>
    /// An answer went on in a terminal: Claude Code lists that session as running once it has started there, so the flow
    /// need not keep it after a while (on Windows, until Rondel hears that it ended).
    /// </summary>
    public void Continued(string session) =>
        Task.Delay(Every).ContinueWith(_ => _flow.ForgetQuickSession(session), TaskScheduler.Default);

    /// <summary>Sweeps what Quick Conversations left while Rondel was not running.</summary>
    public void Start() => Sweep();

    /// <summary>Deletes, off the main thread, what Quick Conversations that no longer run left behind.</summary>
    void Sweep()
    {
        var keep = _flow.QuickSessions;
        bool inUse = _flow.QuickAnswerOpen;
        Task.Run(() => SweepNow(keep, inUse));
    }

    void SweepNow(IReadOnlyCollection<string> keep, bool inUse)
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
                var result = storage.Sweep(_directory, [.. _ended], keep, inUse);
                _ended.IntersectWith(result.Skipped); // one still closing is swept the next time
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Warn("Sweeping the Quick Conversation folder failed", ex);
            }
        }
    }

    public void Dispose() => _timer?.Dispose();
}
