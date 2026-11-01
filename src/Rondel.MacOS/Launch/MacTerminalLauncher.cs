using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Platform;
using Rondel.Core.Settings;
using Rondel.MacOS.Interop;

namespace Rondel.MacOS.Launch;

/// <summary>
/// Runs a <see cref="LaunchPlan"/> in Terminal.app or iTerm2. The whole plan travels in one script
/// (<see cref="PosixShellScript"/>) written to a .command file that deletes itself when it starts; the terminal is
/// asked to open it with open(1), so no AppleScript, no quoting for it and no Automation permission are needed.
/// </summary>
internal sealed partial class MacTerminalLauncher : ITerminalLauncher
{
    const string ITermId = "com.googlecode.iterm2";

    /// <summary>
    /// Where the .command files are written. Both terminals print the file's path as the window's first line, so it
    /// is short; only the user can read it.
    /// </summary>
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "Rondel");

    public void Launch(LaunchPlan plan, AppSettings settings)
    {
        string shell = PosixShellScript.ChooseShell(LoginShell());
        string terminal = TerminalFor(settings.Terminal);
        Directory.CreateDirectory(Folder);
        File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string file = Path.Combine(Folder, $"rondel-{Guid.NewGuid():N}"[..15] + ".command");
        File.WriteAllText(file, PosixShellScript.Build(plan, shell), new UTF8Encoding(false));
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, RedirectStandardError = true };
        foreach (string argument in new[] { "-a", terminal, file })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new LaunchException(Loc.Format("Launch.TerminalFailed", terminal));
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        if (process.ExitCode != 0)
        {
            File.Delete(file); // the script never ran, so it could not delete itself
            Log.Warn($"open -a {terminal} failed ({process.ExitCode}): {error.Trim()}");
            throw new LaunchException(Loc.Format("Launch.TerminalFailed", terminal));
        }
        Log.Info($"Launched {terminal} + {Path.GetFileName(shell)} in '{plan.Directory}' with {plan.Arguments.Count} argument(s)");
    }

    /// <summary>Terminal.app unless iTerm2 is chosen and installed; the Windows choices count as Automatic.</summary>
    static string TerminalFor(TerminalKind kind)
    {
        if (kind != TerminalKind.ITerm)
            return "Terminal";
        if (Workspace.IsInstalled(ITermId))
            return "iTerm";
        Log.Warn("iTerm2 is not installed; using Terminal");
        return "Terminal";
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial uint getuid();

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial nint getpwuid(uint uid);

    /// <summary>The user's login shell from the user database (what Terminal starts), or null.</summary>
    static string? LoginShell()
    {
        nint entry = getpwuid(getuid());
        // struct passwd on macOS: name, passwd, uid, gid, change, class, gecos, dir, shell, expire.
        return entry == 0 ? Environment.GetEnvironmentVariable("SHELL") : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entry, 56));
    }
}
