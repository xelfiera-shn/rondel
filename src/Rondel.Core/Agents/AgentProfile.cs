using System.Diagnostics.CodeAnalysis;

namespace Rondel.Core.Agents;

/// <summary>
/// One coding agent: the command that starts it and the arguments for each capability it supports.
/// Profiles live in settings.json, so another agent can be added without a code change.
/// </summary>
public sealed class AgentProfile
{
    public string Id { get; set; } = "";

    /// <summary>Shown in the UI, e.g. "Claude Code".</summary>
    public string Name { get; set; } = "";

    /// <summary>Base command: a program name, an alias such as "cc", optionally followed by the user's own arguments.</summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Capability name → argument templates. "{prompt}" and "{id}" are filled in at launch.
    /// A capability that is missing here is not supported, and its menu slices are hidden.
    /// </summary>
    public Dictionary<string, List<string>> Capabilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Session store used for the hints under Continue and Resume (see <see cref="Sessions.SessionStores"/>), or null.</summary>
    public string? Sessions { get; set; }

    /// <summary>
    /// How a prompt refers to a file, "{file}" being its name (the agent starts in the file's folder), e.g. "@{file}".
    /// Null or empty: the agent cannot mention a file, and actions that would are hidden.
    /// </summary>
    public string? FileMention { get; set; }

    /// <summary>
    /// Arguments that make the agent report what it does to Rondel, "{hooks}" being Rondel's hooks settings file; they
    /// come before the others. Null or empty: Rondel cannot follow the agent, and starts it as before.
    /// </summary>
    public List<string>? ActivityArgs { get; set; }

    public bool TryGetArguments(AgentCapability capability, [NotNullWhen(true)] out List<string>? templates) =>
        Capabilities.TryGetValue(capability.ToString(), out templates) && templates is not null;

    public bool Supports(AgentCapability capability) => TryGetArguments(capability, out _);
}
