namespace Rondel.Core.Settings;

/// <summary>Following the agents Rondel starts: what they do, shown in the menu, and notifications.</summary>
public sealed class ActiveAgentsOptions
{
    /// <summary>
    /// Start agents so that they report what they do to Rondel (an agent's <see cref="Agents.AgentProfile.ActivityArgs"/>),
    /// each in a terminal window of its own that Rondel can bring to the front.
    /// </summary>
    public bool Follow { get; set; } = true;

    /// <summary>Notify when an agent waits for a permission or an answer.</summary>
    public bool NotifyAsks { get; set; } = true;

    /// <summary>Notify when an agent finishes its turn, or stops with an error.</summary>
    public bool NotifyFinished { get; set; } = true;
}
