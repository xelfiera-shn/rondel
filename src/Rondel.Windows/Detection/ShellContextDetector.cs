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
/// space, or the item matched by display name (Belgeler/Documents both work), a folder or a file.</item>
/// </list>
/// A file gives its folder plus the file, and the other selected files when it is one of them. Virtual locations
/// and items that are neither folders nor files give None, so the click is passed through.
/// Runs on the STA detection thread.
/// </summary>
internal sealed class ShellContextDetector(Func<AppSettings> settings) : IContextDetector
{
    /// <summary>Above this many selected items the selection is not read (every item is a cross-process call).</summary>
    const int MaxSelectedItems = 300;

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
        var target = _shell.ResolveItem(container, hitInfo.ItemName!, hitInfo.ItemType, tryParseName: !desktop, trace, out string reason);
        return target switch
        {
            { IsDirectory: true } folder => new TriggerContext(ContextKind.FolderItem, folder.Path, folder.Name, reason),
            // The file's own name, with its extension even where Explorer hides it.
            { } file => new TriggerContext(ContextKind.FileItem, Path.GetDirectoryName(file.Path), Path.GetFileName(file.Path), reason)
            {
                FilePath = file.Path,
                SelectedFiles = SelectedFilesAround(file.Path, desktop ? null : container, trace),
            },
            null => TriggerContext.None(reason),
        };
    }

    /// <summary>
    /// The selected files when <paramref name="file"/> is one of several (<see cref="FileSelection.Around"/>).
    /// Holding does not reach Explorer, so the selection is still the one the user made.
    /// </summary>
    /// <param name="explorer">The Explorer tab's folder; null on the desktop.</param>
    IReadOnlyList<string> SelectedFilesAround(string file, ShellFolder? explorer, List<string>? trace)
    {
        try
        {
            if ((explorer is null ? _shell.GetDesktopView() : explorer.View) is not { } view)
                return [];
            if (ShellNamespace.GetSelectedPaths(view, MaxSelectedItems) is not { } selected)
            {
                Log.Info($"More than {MaxSelectedItems} items are selected; the menu is about the held file only");
                return [];
            }
            var files = FileSelection.Around(file, selected, File.Exists);
            trace?.Add($"Selection: {selected.Count} item(s), {files.Count} file(s) asked about together");
            return files;
        }
        catch (Exception ex) when (ShellNamespace.IsComFailure(ex))
        {
            Log.Warn("Reading the selection failed; the menu is about the held file only", ex);
            return [];
        }
    }

    static string DisplayNameOf(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;
}
