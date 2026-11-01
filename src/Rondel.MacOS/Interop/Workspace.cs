using System.Collections.Concurrent;
using System.Diagnostics;
using Rondel.Core.Logging;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Interop;

/// <summary>What AppKit's NSWorkspace, NSRunningApplication and NSURL answer about apps and files.</summary>
internal static class Workspace
{
    public const string FinderId = "com.apple.finder";
    public const string DockId = "com.apple.dock";

    /// <summary>System Settings → Privacy &amp; Security → Accessibility / Automation.</summary>
    public const string AccessibilitySettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";
    public const string AutomationSettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation";

    static readonly ConcurrentDictionary<long, string> BundleIds = new();

    /// <summary>The bundle identifier of a running process ("com.apple.finder"), or "" when it has none.</summary>
    public static string BundleId(long pid)
    {
        if (BundleIds.TryGetValue(pid, out string? cached))
            return cached;
        using var pool = new AutoreleasePool();
        nint app = SendInt32(Class("NSRunningApplication"), Sel("runningApplicationWithProcessIdentifier:"), (int)pid);
        string id = app == 0 ? "" : String(Send(app, Sel("bundleIdentifier"))) ?? "";
        // A process id is reused only after its process ended; Finder and the Dock keep theirs for the session.
        if (app != 0)
            BundleIds[pid] = id;
        return id;
    }

    /// <summary>A folder that Finder shows as a single item (an .app, a .photoslibrary…).</summary>
    public static bool IsPackage(string path)
    {
        using var pool = new AutoreleasePool();
        nint workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
        nint nsPath = CFString(path);
        bool package = SendBool(workspace, Sel("isFilePackageAtPath:"), nsPath);
        CFRelease(nsPath);
        return package;
    }

    /// <summary>What a Finder alias points to; any other path is returned as it is.</summary>
    public static string ResolveAlias(string path)
    {
        using var pool = new AutoreleasePool();
        nint nsPath = CFString(path);
        nint url = Send(Class("NSURL"), Sel("fileURLWithPath:"), nsPath);
        CFRelease(nsPath);
        nint resolved = url == 0 ? 0 : Send(Class("NSURL"), Sel("URLByResolvingAliasFileAtURL:options:error:"), url, 0, 0);
        return resolved == 0 ? path : String(Send(resolved, Sel("path"))) ?? path;
    }

    /// <summary>Opens a URL, file or folder the way Finder would (open(1)).</summary>
    public static void Open(string target, string? application = null)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            if (application is not null)
            {
                start.ArgumentList.Add("-a");
                start.ArgumentList.Add(application);
            }
            start.ArgumentList.Add(target);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Opening '{target}' failed", ex);
        }
    }
}
