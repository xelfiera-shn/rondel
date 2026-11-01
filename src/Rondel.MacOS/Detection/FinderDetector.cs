using Rondel.Core.Context;
using Rondel.Core.Logging;
using Rondel.Core.Settings;
using Rondel.MacOS.Interop;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Detection;

/// <summary>
/// Classifies the point under the pointer, in three steps (see docs/macos.md, "Folder detection in Finder"):
/// <list type="number">
/// <item>The window list says whose window is on top there: a Finder window, the desktop, or something else (None).</item>
/// <item>Accessibility finds the item: an element with a file URL is a folder or a file; otherwise it is an empty area.</item>
/// <item>For the empty area of a Finder window, Finder itself tells which folder the window shows (Apple Events),
/// because Finder leaves the window's AXDocument empty.</item>
/// </list>
/// Decisions use bundle identifiers, roles and identifiers, never displayed (localized) text.
/// Runs on the detection thread; Apple Events go through the main thread.
/// </summary>
internal sealed class FinderDetector(Func<AppSettings> settings)
{
    static readonly nint KeyLayer = CFString("kCGWindowLayer"), KeyBounds = CFString("kCGWindowBounds"),
        KeyOwnerPid = CFString("kCGWindowOwnerPID"), KeyAlpha = CFString("kCGWindowAlpha");

    /// <summary>The desktop's windows (Finder's icons, the wallpaper) are far below every app window.</summary>
    const long DesktopLayerBelow = -1_000_000_000;

    public TriggerContext Detect(CGPoint point)
    {
        var trace = Log.DebugEnabled ? new List<string>() : null;
        using var pool = new AutoreleasePool();
        TriggerContext result;
        try
        {
            result = DetectCore(point, trace);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            Log.Warn("Detection failed", ex);
            result = TriggerContext.None($"detection error: {ex.Message}");
        }
        return trace is null ? result : result with { Trace = trace };
    }

    TriggerContext DetectCore(CGPoint point, List<string>? trace)
    {
        var place = PlaceUnder(point, trace);
        if (place == Place.Other)
            return TriggerContext.None("not a Finder window or the desktop");

        var chain = AccessibilityChain.At(point, out int pid, out string? error);
        if (error is not null)
            return TriggerContext.None(error);
        trace?.Add($"Accessibility (pid {pid}, {Workspace.BundleId(pid)}):");
        trace?.AddRange(chain.Select((node, i) => $"  {i,2} {node}"));

        int window = chain.FindIndex(n => n.Role == "AXWindow");
        // The innermost element below the window that names a file or folder.
        string? item = chain.Take(window >= 0 ? window : chain.Count).FirstOrDefault(n => n.Url is not null)?.Url;
        if (item is not null)
            return ForItem(item);

        if (place == Place.Desktop)
        {
            string projects = settings().ResolveProjectsRoot();
            return new TriggerContext(ContextKind.DesktopEmpty, projects, DisplayNameOf(projects), "desktop, empty area");
        }
        if (window < 0 || chain[window].Identifier != "FinderWindow")
            return TriggerContext.None("not inside a Finder window's items");

        var folder = FinderScript.FolderOfWindowAt(point, out string reason);
        trace?.Add($"Apple Events: {reason}");
        return folder is not null && Directory.Exists(folder)
            ? new TriggerContext(ContextKind.ExplorerEmpty, Trim(folder), DisplayNameOf(folder), "Finder window, empty area")
            : TriggerContext.None(reason);
    }

    static TriggerContext ForItem(string url)
    {
        string path = Trim(Workspace.ResolveAlias(url));
        if (Directory.Exists(path) && !Workspace.IsPackage(path))
            return new TriggerContext(ContextKind.FolderItem, path, DisplayNameOf(path), "folder");
        // A file, or a package Finder shows as one (an .app): the file menu for the folder it is in.
        if (File.Exists(path) || Directory.Exists(path))
        {
            return new TriggerContext(ContextKind.FileItem, Path.GetDirectoryName(path), Path.GetFileName(path),
                Directory.Exists(path) ? "package" : "file")
            {
                FilePath = path,
            };
        }
        return TriggerContext.None($"'{path}' does not exist");
    }

    enum Place { Other, FinderWindow, Desktop }

    /// <summary>The topmost window under the point, from the window server's list (front to back).</summary>
    static Place PlaceUnder(CGPoint point, List<string>? trace)
    {
        nint list = CGWindowListCopyWindowInfo(1 /* on screen only */, 0);
        try
        {
            for (long i = 0; i < CFArrayGetCount(list); i++)
            {
                nint info = CFArrayGetValueAtIndex(list, i);
                if (!CGRectMakeWithDictionaryRepresentation(CFDictionaryGetValue(info, KeyBounds), out var bounds) || !bounds.Contains(point))
                    continue;
                long pid = Number(info, KeyOwnerPid);
                long layer = Number(info, KeyLayer);
                string owner = Workspace.BundleId(pid);
                trace?.Add($"Window {owner} (pid {pid}), layer {layer}, {bounds}");
                if (pid == Environment.ProcessId || Double(info, KeyAlpha, 1) == 0)
                    continue;
                // The Dock keeps a transparent window over the whole screen above every app; clicks pass through
                // it. The Dock itself is a strip, never larger than 300 points both ways.
                if (owner == Workspace.DockId && bounds.Width > 300 && bounds.Height > 300)
                    continue;
                if (layer < DesktopLayerBelow)
                    return Place.Desktop;
                return owner == Workspace.FinderId && layer == 0 ? Place.FinderWindow : Place.Other;
            }
            return Place.Other;
        }
        finally
        {
            CFRelease(list);
        }
    }

    static string Trim(string path) => path.Length > 1 ? Path.TrimEndingDirectorySeparator(path) : path;

    static string DisplayNameOf(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;
}
