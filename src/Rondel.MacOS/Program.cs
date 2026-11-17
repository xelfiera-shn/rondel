using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Rondel.Core;
using Rondel.Core.Activity;
using Rondel.Core.Logging;

// Rondel.app runs only on macOS; the project builds everywhere so CI checks it, and this tells the analyzers.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("macos")]

namespace Rondel.MacOS;

/// <summary>
/// The entry point. As the status line of a session it started (<see cref="StatusLineTap"/>), Rondel does that and exits
/// before anything of the app loads: Claude Code runs it after every reply, and the user's own status line waits for it.
/// </summary>
static class Program
{
    /// <summary>Held while Rondel runs, so a second copy (started outside Finder) leaves at once.</summary>
    static FileStream? s_instanceLock;

    [STAThread]
    static int Main(string[] args)
    {
        if (args is [StatusLineTap.Argument, ..])
            return StatusLineTap.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), UsageStore.Default);
        if (DevTools.TryRun(args))
            return 0;
        if (!TryLockInstance())
            return 0;

        Log.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);
            if (e.IsTerminating)
                Log.Stop(); // the process ends right after this: the line is written first, not left in the queue
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            // Avalonia's own default font (Inter) draws Ğ İ Ü Ö without their marks; the system font draws them.
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>
    /// Finder and open(1) never start a second copy of an app; this covers starting the executable directly.
    /// </summary>
    static bool TryLockInstance()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.SettingsDirectory);
            s_instanceLock = new FileStream(Path.Combine(AppInfo.SettingsDirectory, ".instance"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
