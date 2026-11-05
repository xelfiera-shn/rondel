using System.Diagnostics;
using Rondel.Core.Launch;
using Rondel.Core.Logging;
using Rondel.Core.Platform;

namespace Rondel.Windows.Launch;

/// <summary>
/// Starts the agent without a terminal for a Quick Conversation's answer on its card: its program found on PATH as a
/// freshly opened shell would find it, run with no window. An alias or function of the user's PowerShell profile cannot
/// be run that way, and neither can arguments written in PowerShell's own syntax: then the conversation starts in a
/// terminal as before.
/// </summary>
internal sealed class AnswerLauncher : IAnswerLauncher
{
    /// <summary>What can be started without a shell: programs, and batch files such as npm's shims.</summary>
    static readonly string[] Runnable = [".exe", ".com", ".cmd", ".bat"];

    /// <summary>Characters that make the user's own arguments PowerShell rather than plain words.</summary>
    static readonly char[] ShellSyntax = ['$', '`', ';', '|', '&', '(', ')', '{', '}', '@', '<', '>', '"', '\'', '%', '^'];

    public IAnswerProcess? Start(LaunchPlan plan)
    {
        var (name, extra) = CommandLine.Split(plan.Command);
        string? program = CommandResolver.Find(name);
        if (program is null || !Runnable.Contains(Path.GetExtension(program), StringComparer.OrdinalIgnoreCase))
        {
            Log.Info($"'{name}' is not a program on PATH ({program ?? "not found"}); the Quick Conversation starts in a terminal");
            return null;
        }
        if (extra.IndexOfAny(ShellSyntax) >= 0)
        {
            Log.Info($"The command's own arguments '{extra}' need PowerShell; the Quick Conversation starts in a terminal");
            return null;
        }
        var start = AnswerProcess.StartInfo(plan, program, [.. extra.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), .. plan.Arguments]);
        // As in a new terminal: tools installed after Rondel started (node, for npm's shim) are found too.
        start.Environment["PATH"] = CommandResolver.FreshPath();
        var process = Process.Start(start);
        if (process is null)
            return null;
        Log.Info($"Started {Path.GetFileName(program)} for an answer on the card (pid {process.Id})");
        return new AnswerProcess(process);
    }
}
