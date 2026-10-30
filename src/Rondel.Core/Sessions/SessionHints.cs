using Rondel.Core.Localization;

namespace Rondel.Core.Sessions;

public static class SessionHints
{
    /// <summary>The small line under an action, and whether the action is drawn dimmed.</summary>
    /// <param name="sessions">Null when the store is unknown or unreadable: nothing is shown and nothing is dimmed.</param>
    public static (string? Text, bool Dimmed) Describe(SessionHint hint, SessionInfo? sessions)
    {
        if (sessions is null || hint == SessionHint.None)
            return (null, false);
        bool none = sessions.Count == 0;
        return hint switch
        {
            SessionHint.LastSession => (none || sessions.LastActivityUtc is null
                ? Loc.Get("Hint.NoSessions")
                : Loc.Format("Hint.Last", RelativeTime.Describe(sessions.LastActivityUtc.Value)), none),
            SessionHint.SessionCount => (none ? Loc.Get("Hint.NoSessions")
                : sessions.Count == 1 ? Loc.Get("Hint.OneSession")
                : Loc.Format("Hint.Sessions", sessions.Count), none),
            _ => (null, false),
        };
    }
}
