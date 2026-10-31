namespace Rondel.Core.Sessions;

/// <param name="Count">Sessions recorded for the folder.</param>
/// <param name="LastActivityUtc">Last write to any of them, if there is one.</param>
public sealed record SessionInfo(int Count, DateTime? LastActivityUtc);

/// <param name="Id">What the agent's ResumeById capability expects.</param>
/// <param name="Title">The agent's title for the session, or its first prompt; null when neither is known.</param>
public sealed record SessionEntry(string Id, string? Title, DateTime LastActivityUtc);

/// <summary>Session information shown under an action.</summary>
public enum SessionHint { None, LastSession, SessionCount }

/// <summary>Reads an agent's local session data. Best effort: it is another program's internal format.</summary>
public interface ISessionStore
{
    /// <returns>Session statistics for the folder, or null when they cannot be determined.</returns>
    SessionInfo? Lookup(string directory);

    /// <returns>The folder's most recent sessions, newest first (empty when unknown).</returns>
    IReadOnlyList<SessionEntry> Recent(string directory, int max);
}
