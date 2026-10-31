using System.Text;
using Rondel.Core.Localization;

namespace Rondel.Core.Launch;

/// <summary>
/// Builds the script that runs a <see cref="LaunchPlan"/> on macOS: written to a .command file that
/// Terminal.app or iTerm2 opens. Every value is a single-quoted literal, so quotes, '$', backticks,
/// '$(…)', ';', line breaks and Turkish characters arrive unchanged.
/// </summary>
/// <remarks>
/// The agent runs inside the user's login shell as an interactive shell, so PATH from the login files and
/// aliases from the rc files work; afterwards that shell becomes a login shell in the project folder, so the
/// window stays usable. It is one process all the way (exec): a child interactive shell takes the terminal's
/// foreground process group, and the script's last shell was then stopped ("suspended (tty output)").
/// </remarks>
public static class PosixShellScript
{
    /// <summary>Shell used when the user's login shell cannot run the script's POSIX commands (fish, for example).</summary>
    public const string FallbackShell = "/bin/zsh";

    /// <summary>Shells whose -c understands POSIX syntax ("$@", ||, if … fi).</summary>
    static readonly HashSet<string> PosixShells = new(StringComparer.Ordinal) { "zsh", "bash", "sh", "ksh", "dash" };

    /// <returns><paramref name="loginShell"/> when it is a POSIX shell, else <see cref="FallbackShell"/>.</returns>
    public static string ChooseShell(string? loginShell) =>
        !string.IsNullOrWhiteSpace(loginShell) && Path.IsPathRooted(loginShell) && PosixShells.Contains(Path.GetFileName(loginShell))
            ? loginShell
            : FallbackShell;

    /// <param name="shell">The shell the agent runs in (see <see cref="ChooseShell"/>).</param>
    public static string Build(LaunchPlan plan, string shell)
    {
        var (name, extra) = CommandLine.Split(plan.Command);
        string loginShell = $"exec {Quote(shell)} -l";

        // Runs in the user's shell, so it must be plain POSIX for zsh and bash alike.
        var agent = new StringBuilder();
        if (plan.InitGit)
        {
            // A new project gets its repository before the agent starts; without git it starts anyway.
            agent.Append($"if command -v git >/dev/null 2>&1; then git init; else {Print(Loc.Get("Launch.NoGit"), Yellow)}; fi; ");
        }
        // Left unquoted when it is a plain word, so an alias of that name is expanded (zsh does not expand a
        // quoted word). Anything after the name is the user's own shell text from settings ("--model opus").
        string command = IsPlainWord(name) ? name : Quote(name);
        agent.Append($"if command -v {command} >/dev/null 2>&1; then {$"{command} {extra}".TrimEnd()} \"$@\"; ")
            .Append($"else printf '\\n' >&2; {Print(Loc.Format("Launch.NotFound", name), Red)}; ")
            .Append($"{Print(Loc.Format("Launch.NotFoundHintMac", plan.AgentName), Gray)}; fi; ")
            .Append(loginShell);

        var s = new StringBuilder();
        s.Append("#!/bin/zsh\n");
        s.Append("rm -f -- \"$0\"\n");
        s.Append($"cd -- {Quote(plan.Directory)} 2>/dev/null || {{ {Print(Loc.Format("Launch.NoFolder", plan.Directory), Red)}; {loginShell}; }}\n");
        s.Append($"printf '\\033]0;%s\\007' {Quote(plan.Title)}\n");
        s.Append($"exec {Quote(shell)} -l -i -c {Quote(agent.ToString())} rondel");
        foreach (string argument in plan.Arguments)
            s.Append(' ').Append(Quote(argument));
        s.Append('\n');
        return s.ToString();
    }

    const string Red = "31", Yellow = "33", Gray = "90";

    /// <summary>A line on standard error in an ANSI color.</summary>
    static string Print(string message, string color) => $"printf '\\033[{color}m%s\\033[0m\\n' {Quote(message)} >&2";

    /// <summary>A command name the shell reads as one word without quotes: letters, digits and ._+-/~ (a leading ~ is the home folder).</summary>
    internal static bool IsPlainWord(string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-' or '/' or '~')
        && name.IndexOf('~', 1) < 0;

    /// <summary>POSIX single-quoted literal: nothing is expanded inside it; a ' ends it, is escaped, and it starts again.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
