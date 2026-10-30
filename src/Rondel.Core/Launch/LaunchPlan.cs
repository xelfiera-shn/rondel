namespace Rondel.Core.Launch;

/// <summary>Everything needed to start an agent in a terminal; platform launchers only execute it.</summary>
/// <param name="Directory">Folder the agent starts in.</param>
/// <param name="AgentName">Shown in the window title and in messages.</param>
/// <param name="Command">The agent's command as configured (see <see cref="CommandLine.Split"/>).</param>
/// <param name="Arguments">Literal arguments appended after the command, already expanded.</param>
public sealed record LaunchPlan(string Directory, string AgentName, string Command, IReadOnlyList<string> Arguments)
{
    public string Title
    {
        get
        {
            string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));
            return $"{AgentName} · {(folder.Length > 0 ? folder : Directory)}";
        }
    }
}
