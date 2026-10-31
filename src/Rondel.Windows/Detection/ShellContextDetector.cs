using Rondel.Core.Context;
using Rondel.Core.Logging;
using Rondel.Core.Settings;
using Rondel.Windows.Interop;
using Rondel.Windows.Shell;

namespace Rondel.Windows.Detection;

/// <summary>
/// Classifies the point under the cursor:
/// <list type="bullet">
/// <item>UIA decides item vs. empty area vs. anything else (headers, panes, bars → None).</item>
/// <item>Shell.Application turns that into a folder: the active Explorer tab's folder for empty
/// space, or the item matched by display name (Belgeler/Documents both work).</item>
/// </list>
/// Virtual locations and non-folder items give None, so the click is passed through.
/// Runs on the STA detection thread.
/// </summary>
internal sealed class ShellContextDetector(Func<AppSettings> settings) : IContextDetector
{
    readonly ShellNamespace _shell = new();
    UiaProbe? _uia;

    public TriggerContext Detect(POINT point, nint rootWindow)
    {
        var trace = Log.DebugEnabled ? new List<string>() : null;
        TriggerContext result;
        try
        {
            result = DetectCore(point, rootWindow, trace);
        }
        catch (Exception ex) when (ShellNamespace.IsComFailure(ex))
        {
            Log.Warn("Shell/UIA query failed", ex);
            result = TriggerContext.None($"shell error: {ex.Message}");
        }
        return trace is null ? result : result with { Trace = trace };
    }

    TriggerContext DetectCore(POINT point, nint root, List<string>? trace)
    {
        if (root == 0)
            return TriggerContext.None("no window under the cursor");
        string rootClass = Native.GetClassName(root);
        bool desktop = rootClass is "Progman" or "WorkerW";
        if (!desktop && rootClass != "CabinetWClass")
            return TriggerContext.None($"window class '{rootClass}' is neither the desktop nor Explorer");

        nint hit = Native.WindowFromPoint(point);
        trace?.Add($"Window under cursor: 0x{hit:X} '{Native.GetClassName(hit)}' in 0x{root:X} '{rootClass}'");
        if (Native.FindAncestor(hit, root, "SHELLDLL_DefView") == 0)
            return TriggerContext.None("not inside a shell items view (SHELLDLL_DefView)");

        _uia ??= new UiaProbe();
        var hitInfo = _uia.Probe(point, root, hit, trace);
        trace?.Add($"UIA result: {hitInfo.Kind} - {hitInfo.Detail}");

        switch (hitInfo.Kind)
        {
            case UiaHitKind.Blocked or UiaHitKind.Outside or UiaHitKind.Failed:
                return TriggerContext.None(hitInfo.Detail);

            case UiaHitKind.Empty when desktop:
                string projects = settings().ResolveProjectsRoot();
                return new TriggerContext(ContextKind.DesktopEmpty, projects, DisplayNameOf(projects), "desktop, empty area");

            case UiaHitKind.Item when string.IsNullOrEmpty(hitInfo.ItemName):
                return TriggerContext.None("item without a name");
        }

        ShellFolder? container;
        if (desktop)
        {
            container = _shell.GetDesktop();
        }
        else
        {
            nint tab = Native.FindAncestor(hit, root, "ShellTabWindowClass");
            container = _shell.GetExplorerFolder(root, tab, trace);
            if (container is null)
                return TriggerContext.None("this Explorer window/tab was not found among the shell windows");
        }
        trace?.Add($"Container: '{container.DisplayName}' path='{container.Path}' filesystem={container.IsFileSystem}");

        if (hitInfo.Kind == UiaHitKind.Empty)
        {
            return container.IsRealDirectory
                ? new TriggerContext(ContextKind.ExplorerEmpty, container.Path, container.DisplayName, "Explorer, empty area")
                : TriggerContext.None($"virtual location '{container.Path}'");
        }

        // The desktop is a merged namespace (user + Public Desktop), so it is always scanned by display name.
        var folder = _shell.ResolveFolderItem(container, hitInfo.ItemName!, hitInfo.ItemType, tryParseName: !desktop, trace, out string reason);
        return folder is { } f
            ? new TriggerContext(ContextKind.FolderItem, f.Path, f.Name, reason)
            : TriggerContext.None(reason);
    }

    static string DisplayNameOf(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;
}
