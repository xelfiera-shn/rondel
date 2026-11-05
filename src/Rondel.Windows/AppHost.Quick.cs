using Rondel.Core.Activity;
using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Logging;

namespace Rondel.Windows;

/// <summary>
/// Quick Conversation: what Claude Code keeps of a session there is deleted for good once it ends, and again at each
/// start of Rondel and of a new Quick Conversation, for what one left while Rondel was not running.
/// </summary>
internal sealed partial class AppHost
{
    /// <summary>A session that has just ended may still be closing: it is swept once more after this.</summary>
    static readonly TimeSpan SweepAgain = TimeSpan.FromSeconds(15);

    readonly object _sweepLock = new();

    /// <summary>Quick Conversation sessions whose end was reported and that are not swept yet.</summary>
    readonly HashSet<string> _quickEnded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The log said once that Claude Code keeps no list of its running sessions.</summary>
    bool _toldNoSessionList;

    void StartQuickConversations()
    {
        _flow.QuickConversationStarting += SweepQuick;
        // A card closed with its answer: that session is over, even though no terminal ran it.
        _flow.QuickSessionEnded += session =>
        {
            lock (_sweepLock)
                _quickEnded.Add(session);
            SweepQuick();
        };
        SweepQuick();
    }

    /// <summary>Sweeps once an agent in the Quick Conversation folder ends, and a little later again.</summary>
    void TrackQuick(AgentChange change)
    {
        if (!change.Ended || !IsQuick(change.Agent.Directory))
            return;
        if (change.Agent.SessionId is { } session)
        {
            lock (_sweepLock)
                _quickEnded.Add(session);
            _flow.ForgetQuickSession(session); // one an answer on the card continued
        }
        SweepQuick();
        Task.Delay(SweepAgain).ContinueWith(_ => SweepQuick(), TaskScheduler.Default);
    }

    bool IsQuick(string? directory) =>
        directory is not null
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), Path.TrimEndingDirectorySeparator(_flow.QuickDirectory),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes, off the UI thread, what Quick Conversations that no longer run left behind, in Claude Code's folder and
    /// in their own. The ones still followed are kept (and the folder's files with them), and so is anything that has
    /// just appeared, which may be a Quick Conversation starting right now.
    /// </summary>
    void SweepQuick()
    {
        var followed = _activity.Agents.Where(a => IsQuick(a.Directory)).ToList();
        // Answers on an open card, and those continued in a terminal, are kept too; while a card answers, so are the
        // folder's files (the screenshot it is about).
        var running = followed.Select(a => a.SessionId).OfType<string>().Concat(_flow.QuickSessions).ToList();
        bool inUse = followed.Count > 0 || _flow.QuickAnswerOpen;
        Task.Run(() =>
        {
            lock (_sweepLock)
            {
                try
                {
                    // A Quick Conversation Rondel does not follow (following is off, or it started before Rondel) is told
                    // from one that ended only by Claude Code's list of the sessions it runs: without it nothing is swept.
                    var storage = new ClaudeCodeStorage();
                    if (!Directory.Exists(Path.Combine(storage.ConfigDirectory, "sessions")))
                    {
                        if (!_toldNoSessionList)
                            Log.Info("Claude Code lists no running sessions; Quick Conversations are not swept");
                        _toldNoSessionList = true;
                        return;
                    }
                    _toldNoSessionList = false;
                    var result = storage.Sweep(_flow.QuickDirectory, [.. _quickEnded], running, inUse);
                    _quickEnded.IntersectWith(result.Skipped); // one still closing is swept the next time
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Log.Warn("Sweeping the Quick Conversation folder failed", ex);
                }
            }
        });
    }
}
