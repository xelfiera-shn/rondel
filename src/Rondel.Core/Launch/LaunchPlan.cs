namespace Rondel.Core.Launch;

/// <summary>Everything needed to start an agent in a terminal; platform launchers only execute it.</summary>
/// <param name="Directory">Folder the agent starts in.</param>
/// <param name="AgentName">Shown in the window title and in messages.</param>
/// <param name="Command">The agent's command as configured (see <see cref="CommandLine.Split"/>).</param>
/// <param name="Arguments">Literal arguments appended after the command, already expanded.</param>
public sealed record LaunchPlan(string Directory, string AgentName, string Command, IReadOnlyList<string> Arguments)
{
    /// <summary>Run "git init" in <see cref="Directory"/> before the agent starts (a new project); without git, say so and go on.</summary>
    public bool InitGit { get; init; }

    /// <summary>
    /// The id Rondel follows the session by (<see cref="Activity.ActiveAgent.Id"/>), or null when it is not followed. The
    /// script puts it into the agent's environment; the launcher names the terminal window after it.
    /// </summary>
    public string? ActivityId { get; init; }

    public string Title
    {
        get
        {
            string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));
            return $"{AgentName} · {(folder.Length > 0 ? folder : Directory)}";
        }
    }
}
