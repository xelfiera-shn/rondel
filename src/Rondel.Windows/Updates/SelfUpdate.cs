using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Rondel.Core.Logging;
using Rondel.Core.Updates;

namespace Rondel.Windows.Updates;

/// <summary>
/// Puts a verified new Rondel.exe in place of the running one and restarts. A running exe cannot be overwritten or
/// deleted, but it can be renamed: the old file becomes "Rondel.exe.old" and the new one takes its path, so the
/// Start-with-Windows entry and shortcuts keep working. The new one starts with <see cref="AfterUpdateSwitch"/>, says
/// that it runs, and waits for the old one to exit before it takes over; the old one exits once it hears that, or
/// puts itself back when the new one does not start. The next start deletes the old file.
/// </summary>
internal static class SelfUpdate
{
    public const string AfterUpdateSwitch = "--after-update";

    const string StartedEventName = @"Local\Rondel.UpdateStarted";

    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    static string OldFile(string exe) => exe + ".old";

    /// <summary>Whether Rondel may replace its exe; not, for example, in Program Files without administrator rights.</summary>
    public static bool CanReplace(string exe)
    {
        string probe = Path.Combine(Path.GetDirectoryName(exe)!, $".rondel-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces <paramref name="exe"/> with <paramref name="newExe"/> and starts it. Blocks until the new version says
    /// it runs (call it off the UI thread); the caller then exits. False when the old version was put back.
    /// </summary>
    public static bool Install(string exe, string newExe, AppVersion from)
    {
        string old = OldFile(exe);
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, StartedEventName);
        started.Reset();
        try
        {
            Delete(old);
            File.Move(exe, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Moving the running '{exe}' aside failed", ex);
            return false;
        }
        try
        {
            Retry(() => File.Move(newExe, exe)); // an antivirus may still be reading the new file
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Putting the new version at '{exe}' failed", ex);
            Restore(exe, old);
            return false;
        }

        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            start.ArgumentList.Add(AfterUpdateSwitch);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(from.ToString());
            process = Process.Start(start);
            if (process is not null && started.WaitOne(StartTimeout))
            {
                Log.Info($"The new version runs (process {process.Id}); exiting");
                return true;
            }
            Log.Warn("The new version did not start in time; putting the old one back");
            if (process is { HasExited: false })
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("Starting the new version failed; putting the old one back", ex);
        }
        finally
        {
            process?.Dispose();
        }
        Restore(exe, old);
        return false;
    }

    /// <summary>
    /// In the new version, before the single-instance check: tells the old version that this one runs, and waits for
    /// it to exit. The version updated from, or null when Rondel was not started by an update.
    /// </summary>
    public static AppVersion? AfterUpdate(string[] args)
    {
        if (args is not [AfterUpdateSwitch, var processId, var fromText, ..])
            return null;
        try
        {
            using var started = EventWaitHandle.OpenExisting(StartedEventName);
            started.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        if (int.TryParse(processId, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
        {
            try
            {
                using var old = Process.GetProcessById(id);
                old.WaitForExit(ExitTimeout);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        return AppVersion.TryParse(fromText, out var from) ? from : null;
    }

    /// <summary>Deletes the file an update left behind, once the version it holds no longer runs.</summary>
    public static void RemoveOld(string exe)
    {
        string old = OldFile(exe);
        if (!File.Exists(old))
            return;
        try
        {
            File.Delete(old);
            Log.Info($"Deleted the previous version '{old}'");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Deleting the previous version '{old}' failed; trying again at the next start", ex);
        }
    }

    static void Restore(string exe, string old)
    {
        try
        {
            if (File.Exists(exe))
                Retry(() => File.Delete(exe));
            File.Move(old, exe);
            Log.Info($"'{exe}' is the previous version again");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Putting the previous version back at '{exe}' failed; it is at '{old}'", ex);
        }
    }

    static void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(500);
            }
        }
    }
}
