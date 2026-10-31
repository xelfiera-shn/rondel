using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace Rondel.Windows.Shell;

/// <summary>A folder as Explorer shows it: localized display name plus the filesystem path, if any.</summary>
internal sealed record ShellFolder(dynamic Folder, string DisplayName, string Path, bool IsFileSystem)
{
    /// <summary>Real, existing directory (not This PC, Home, Libraries, Recycle Bin, network roots, ZIP contents, search results...).</summary>
    public bool IsRealDirectory => IsFileSystem && Directory.Exists(Path);

    public bool IsRecycleBin => Path.StartsWith("::{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Shell.Application automation (Shell32 via IDispatch). Must be used from the STA detection thread.
/// </summary>
internal sealed class ShellNamespace
{
    /// <summary>Above this many items we stop scanning a folder for a display name (every item is a cross-process call).</summary>
    const int MaxScannedItems = 2000;

    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    dynamic? _shell;

    dynamic Shell => _shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;

    /// <summary>
    /// The folder shown in the Explorer window <paramref name="topLevel"/>. On Windows 11 several tabs
    /// share one top-level window; <paramref name="tabWindow"/> (the ShellTabWindowClass under the
    /// cursor) picks the right one via IServiceProvider → IShellBrowser → GetWindow.
    /// </summary>
    public ShellFolder? GetExplorerFolder(nint topLevel, nint tabWindow, List<string>? trace)
    {
        dynamic windows = Shell.Windows();
        int count = windows.Count;
        var candidates = new List<object>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                object? window = windows.Item(i);
                if (window is null)
                    continue;
                long hwnd = Convert.ToInt64((object)((dynamic)window).HWND);
                if (hwnd == topLevel)
                    candidates.Add(window);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                // A window closing while we enumerate; skip it.
            }
        }
        trace?.Add($"Shell windows: {count}, sharing this top-level window: {candidates.Count}");
        if (candidates.Count == 0)
            return null;

        object chosen = candidates[0];
        if (candidates.Count > 1)
        {
            object? active = tabWindow == 0 ? null : candidates.FirstOrDefault(c => GetTabWindow(c) == tabWindow);
            if (active is null)
            {
                trace?.Add($"No shell window matches tab 0x{tabWindow:X}");
                return null; // guessing could open the wrong folder
            }
            chosen = active;
        }

        object folder = ((dynamic)chosen).Document.Folder;
        return Describe(folder);
    }

    /// <summary>The merged desktop namespace (user desktop + Public Desktop + virtual items).</summary>
    public ShellFolder GetDesktop() => Describe((object)Shell.NameSpace(0));

    /// <summary>
    /// Resolves the item named <paramref name="displayName"/> in <paramref name="container"/> to a
    /// directory: a real folder (not a ZIP, which also reports IsFolder) or a shortcut to one.
    /// </summary>
    /// <returns>The directory and its display name, or null with the reason in <paramref name="reason"/>.</returns>
    public (string Path, string Name)? ResolveFolderItem(ShellFolder container, string displayName, string? itemType,
        bool tryParseName, List<string>? trace, out string reason)
    {
        if (container.IsRecycleBin)
        {
            reason = "item in the Recycle Bin";
            return null;
        }

        var candidates = new List<object>();

        // Fast path: the display name usually equals the parsing name for folders.
        if (tryParseName)
        {
            try
            {
                object? parsed = container.Folder.ParseName(displayName);
                if (parsed is not null && NameOf(parsed) == displayName)
                    candidates.Add(parsed);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
            }
        }

        // Localized names ("Belgeler" for Documents), hidden extensions, or a type mismatch: scan by display name.
        if (candidates.Count == 0 || (!string.IsNullOrEmpty(itemType) && TypeOf(candidates[0]) != itemType))
        {
            candidates.Clear();
            dynamic items = container.Folder.Items();
            int count = items.Count;
            if (count > MaxScannedItems)
            {
                reason = $"'{displayName}' did not parse directly and the folder has {count} items (> {MaxScannedItems})";
                return null;
            }
            for (int i = 0; i < count; i++)
            {
                try
                {
                    object? item = items.Item(i);
                    if (item is not null && NameOf(item) == displayName)
                        candidates.Add(item);
                }
                catch (Exception ex) when (IsComFailure(ex))
                {
                }
            }
            trace?.Add($"Scanned {count} items for '{displayName}': {candidates.Count} match(es)");
        }

        // Several items can share a display name when extensions are hidden ("notes" folder and notes.txt).
        // UIA's ItemType and the shell's Type are the same localized string, so they tell them apart.
        if (candidates.Count > 1 && !string.IsNullOrEmpty(itemType))
        {
            var sameType = candidates.Where(c => TypeOf(c) == itemType).ToList();
            if (sameType.Count > 0)
                candidates = sameType;
        }

        if (candidates.Count == 0)
        {
            reason = $"no shell item named '{displayName}'";
            return null;
        }

        var resolved = candidates.Select(c => ToDirectory(c, trace)).ToList();
        if (resolved.Any(r => r is null) && resolved.Any(r => r is not null))
        {
            reason = $"'{displayName}' is ambiguous (a folder and a non-folder share the name)";
            return null;
        }
        if (resolved[0] is not { } directory)
        {
            reason = $"'{displayName}' is not a folder";
            return null;
        }
        reason = "folder item";
        return (directory, displayName);
    }

    static string? ToDirectory(object shellItem, List<string>? trace)
    {
        dynamic item = shellItem;
        try
        {
            if ((bool)item.IsLink)
            {
                string target = item.GetLink.Path;
                trace?.Add($"Shortcut → '{target}'");
                return Directory.Exists(target) ? target : null;
            }
            string path = item.Path;
            bool isFolder = item.IsFolder, isFileSystem = item.IsFileSystem;
            trace?.Add($"Item path='{path}' folder={isFolder} filesystem={isFileSystem}");
            // ZIP files report IsFolder too; Directory.Exists rules them out.
            return isFolder && isFileSystem && Directory.Exists(path) ? path : null;
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            trace?.Add($"Item could not be inspected: {ex.Message}");
            return null;
        }
    }

    static ShellFolder Describe(object folder)
    {
        dynamic self = ((dynamic)folder).Self;
        return new ShellFolder(folder, (string)self.Name, (string)self.Path, (bool)self.IsFileSystem);
    }

    static string NameOf(object item) => (string)((dynamic)item).Name ?? "";

    static string TypeOf(object item)
    {
        try
        {
            return (string)((dynamic)item).Type ?? "";
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return "";
        }
    }

    static nint GetTabWindow(object shellWindow)
    {
        if (shellWindow is not IComServiceProvider provider)
            return 0;
        var service = SID_STopLevelBrowser;
        var iid = IID_IShellBrowser;
        if (provider.QueryService(ref service, ref iid, out nint browser) != 0 || browser == 0)
            return 0;
        try
        {
            // IShellBrowser derives from IOleWindow; GetWindow returns this tab's ShellTabWindowClass.
            var window = (IOleWindow)Marshal.GetObjectForIUnknown(browser);
            return window.GetWindow(out nint hwnd) == 0 ? hwnd : 0;
        }
        finally
        {
            Marshal.Release(browser);
        }
    }

    public static bool IsComFailure(Exception ex) =>
        ex is COMException or RuntimeBinderException or InvalidCastException or InvalidComObjectException;
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid guidService, ref Guid riid, out nint ppvObject);
}

[ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleWindow
{
    [PreserveSig]
    int GetWindow(out nint phwnd);

    [PreserveSig]
    int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
}
