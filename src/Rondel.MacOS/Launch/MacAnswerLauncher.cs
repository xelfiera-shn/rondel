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
/// <remarks>
/// The profiles run before the command, with no terminal. So that none of them reads the question, the shell starts with
/// /dev/null as its input and the question on descriptor 3, which only the agent is given. A <c>cd</c> in them is
/// undone (the agent must run in the Quick folder, where its transcript is swept and found again), and a line break
/// comes first, so what a profile prints without one is not glued to the first answer event.
/// </remarks>
internal sealed class MacAnswerLauncher : IAnswerLauncher
{
    public IAnswerProcess? Start(LaunchPlan plan)
    {
        string shell = PosixShellScript.ChooseShell(MacTerminalLauncher.LoginShell());
        // The command as the user wrote it (it may be an alias or carry arguments of its own), then the folder and the
        // plan's arguments as positional parameters, so none of them is read by the shell.
        string command = "cd -- \"$1\" || exit 1; shift; printf '\\n'; " + plan.Command + " \"$@\" <&3 3<&-";
        // /bin/sh moves the question's pipe to descriptor 3 and gives the login shell /dev/null instead.
        var start = AnswerProcess.StartInfo(plan, "/bin/sh",
            ["-c", "exec 3<&0 </dev/null; exec \"$0\" \"$@\"", shell, "-l", "-i", "-c", command, "rondel-answer", plan.Directory, .. plan.Arguments]);
        var process = Process.Start(start);
        if (process is null)
            return null;
        Log.Info($"Started {Path.GetFileName(shell)} for an answer on the card (pid {process.Id})");
        return new AnswerProcess(process);
    }
}
