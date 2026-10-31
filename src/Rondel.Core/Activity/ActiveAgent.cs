namespace Rondel.Core.Activity;

/// <summary>What a followed agent is doing.</summary>
public enum AgentState
{
    /// <summary>Working on a prompt.</summary>
    Working,

    /// <summary>Stopped in the middle of its work for the user: a permission, or a question.</summary>
    Waiting,

    /// <summary>Done with its turn; the next prompt is the user's.</summary>
    Ready,

    /// <summary>Stopped by an error, such as a failed request to the model.</summary>
    Failed,
}

/// <summary>Why a change is worth a notification.</summary>
public enum AgentNotice
{
    /// <summary>The agent waits for a permission or an answer.</summary>
    Asks,

    /// <summary>The agent finished its turn.</summary>
    Finished,

    /// <summary>The agent stopped with an error.</summary>
    Failed,
}

/// <summary>A session Rondel started and follows through the agent's hooks.</summary>
/// <param name="Id">
/// Rondel's id for the session: set in its environment (<see cref="AgentActivity.EnvironmentVariable"/>), sent back
/// with every event, and part of its terminal window's name.
/// </param>
/// <param name="Directory">The folder the agent works in.</param>
/// <param name="AgentName">The agent's name, or null for a session Rondel picked up again after a restart.</param>
public sealed record ActiveAgent(string Id, string Directory, string? AgentName)
{
    public AgentState State { get; init; }

    /// <summary>When the agent entered <see cref="State"/> (UTC).</summary>
    public DateTime Since { get; init; }

    /// <summary>The agent's own session id, once one of its events has told it.</summary>
    public string? SessionId { get; init; }

    /// <summary>While <see cref="AgentState.Waiting"/> for a permission: the tool the agent wants to use.</summary>
    public string? Tool { get; init; }

    /// <summary>The question the agent asks, the start of its last reply, or the error; null while it works.</summary>
    public string? Message { get; init; }

    /// <summary>The folder's name, shown in the menu and in notifications.</summary>
    public string Name
    {
        get
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));
            return name.Length > 0 ? name : Directory;
        }
    }

    /// <summary>The current wait has been announced, so a later reminder of the same wait is not.</summary>
    internal bool Announced { get; init; }
}

/// <summary>A change in what a followed agent does.</summary>
/// <param name="Agent">The agent after the change.</param>
/// <param name="Notice">Set when the change is worth a notification.</param>
/// <param name="Ended">The session ended; the agent is no longer followed.</param>
public sealed record AgentChange(ActiveAgent Agent, AgentNotice? Notice, bool Ended);
