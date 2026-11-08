using System.Diagnostics;
using Rondel.Core.Launch;
using Rondel.Core.Logging;
using Rondel.Core.Platform;

namespace Rondel.MacOS.Launch;

/// <summary>
/// Starts the agent without a terminal for a Quick Conversation's answer on its card: in the user's login and
/// interactive shell, as the terminal runs it, so the <c>PATH</c> from <c>~/.zprofile</c> and an alias from
/// <c>~/.zshrc</c> count (apps started from Finder do not inherit the terminal's <c>PATH</c>). What a profile prints
/// is not an answer event and is left out. A command the shell does not find fails on the card, whose button then opens
/// the conversation in a terminal.
/// </summary>
internal sealed class MacAnswerLauncher : IAnswerLauncher
{
    public IAnswerProcess? Start(LaunchPlan plan)
    {
        string shell = PosixShellScript.ChooseShell(MacTerminalLauncher.LoginShell());
        // The command as the user wrote it (it may be an alias or carry arguments of its own), then the plan's
        // arguments as positional parameters, so none of them is read by the shell.
        var start = AnswerProcess.StartInfo(plan, shell, ["-l", "-i", "-c", plan.Command + " \"$@\"", "rondel-answer", .. plan.Arguments]);
        var process = Process.Start(start);
        if (process is null)
            return null;
        Log.Info($"Started {Path.GetFileName(shell)} for an answer on the card (pid {process.Id})");
        return new AnswerProcess(process);
    }
}
