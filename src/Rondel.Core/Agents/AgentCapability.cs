namespace Rondel.Core.Agents;

/// <summary>Something an agent can be asked to do. Menu actions refer to these, not to command lines.</summary>
public enum AgentCapability
{
    /// <summary>Start a new session in the folder.</summary>
    Open,

    /// <summary>Continue the most recent session in the folder.</summary>
    Continue,

    /// <summary>Let the user pick one of the folder's sessions.</summary>
    Resume,

    /// <summary>Resume a specific session ("{id}").</summary>
    ResumeById,

    /// <summary>Start a new session with an initial prompt ("{prompt}").</summary>
    Prompt,

    /// <summary>
    /// Start in plan mode: on its own a new session, and for a new project placed in front of the other
    /// arguments (with a prompt, for example).
    /// </summary>
    Plan,
}
