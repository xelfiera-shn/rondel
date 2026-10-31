using System.Diagnostics;
using System.Text;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Platform;
using Rondel.Core.Settings;
using Rondel.Windows.Interop;

namespace Rondel.Windows.Launch;

/// <summary>
/// Runs a <see cref="LaunchPlan"/> in Windows Terminal, or in a console window when Windows Terminal
/// is missing or not wanted. The whole plan travels in one PowerShell script passed with
/// -EncodedCommand (Base64 UTF-16LE). The folder is never put on wt's command line (';' is wt's
/// command separator); it is the process working directory and the script's Set-Location.
/// </summary>
internal sealed class WindowsTerminalLauncher : ITerminalLauncher
{
    /// <summary>CreateProcess allows 32767 characters; keep a margin.</summary>
    const int MaxCommandLine = 32000;

    public void Launch(LaunchPlan plan, AppSettings settings)
    {
        string shell = ResolveShell(settings.Shell);
        string? terminal = settings.Terminal == TerminalKind.Console ? null : CommandResolver.FindWindowsTerminal();
        if (terminal is null && settings.Terminal == TerminalKind.WindowsTerminal)
            Log.Warn("Windows Terminal is not installed; using a console window");

        string script = PowerShellScript.Build(plan);
        string shellArguments = "-NoLogo -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = terminal is null
            ? new ProcessStartInfo(shell, shellArguments)
            : new ProcessStartInfo(terminal, $"-d . \"{shell}\" {shellArguments}");
        if (start.Arguments.Length > MaxCommandLine)
            throw new LaunchException(Loc.Get("Launch.TooLong"));

        start.UseShellExecute = false;
        // CreateProcess cannot start in a directory longer than MAX_PATH; the script's Set-Location still goes there.
        start.WorkingDirectory = plan.Directory.Length < 248 && Directory.Exists(plan.Directory)
            ? plan.Directory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        start.Environment["PATH"] = CommandResolver.FreshPath();

        // Let the terminal come to the front even if an already-running Windows Terminal opens the tab.
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        using var process = Process.Start(start);
        Log.Info($"Launched {(terminal is null ? Path.GetFileName(shell) : "wt.exe + " + Path.GetFileName(shell))} in '{plan.Directory}'"
            + $" with {plan.Arguments.Count} argument(s), script {script.Length} chars");
    }

    public static string ResolveShell(ShellKind kind)
    {
        if (kind != ShellKind.WindowsPowerShell && CommandResolver.FindPwsh() is { } pwsh)
            return pwsh;
        if (kind == ShellKind.Pwsh)
            Log.Warn("PowerShell 7 is not installed; using Windows PowerShell");
        return CommandResolver.WindowsPowerShell;
    }
}
