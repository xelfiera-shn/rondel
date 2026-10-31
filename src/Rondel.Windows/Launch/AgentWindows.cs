using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Rondel.Core.Logging;
using Rondel.Windows.Interop;

namespace Rondel.Windows.Launch;

/// <summary>
/// The terminal window of each followed agent, so a notification or the menu can bring it to the front. A window is
/// found when it appears after the launch: the one top-level terminal window that was not there before.
/// </summary>
/// <remarks>
/// Windows Terminal windows are named after the session (<see cref="WindowName"/>) and brought to the front by
/// asking Windows Terminal itself (<c>wt -w name focus-tab</c>), which works even when Rondel may not take the
/// foreground. That command opens a new, empty window when no window has the name, so it is used only for a window
/// that is known to be open. The windows are remembered in a small file, so a restarted Rondel still finds them.
/// </remarks>
/// <param name="stateFile">Where the windows are remembered; null keeps them in memory only.</param>
internal sealed class AgentWindows(string? stateFile)
{
    const string TerminalClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    const string ConsoleClass = "ConsoleWindowClass";
    static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(8);

    readonly ConcurrentDictionary<string, nint> _windows = Load(stateFile);
    readonly Lock _saveLock = new();

    /// <summary>The Windows Terminal window name of a followed session.</summary>
    public static string WindowName(string id) => "rondel-" + id;

    /// <summary>The terminal windows that are open now; taken right before a launch.</summary>
    public static HashSet<nint> Snapshot()
    {
        var windows = new HashSet<nint>();
        foreach (string windowClass in (string[])[TerminalClass, ConsoleClass])
        {
            nint hwnd = 0;
            while ((hwnd = Native.FindWindowExW(0, hwnd, windowClass, null)) != 0)
            {
                if (Native.IsWindowVisible(hwnd))
                    windows.Add(hwnd);
            }
        }
        return windows;
    }

    /// <summary>Looks, in the background, for the window the launch of session <paramref name="id"/> opens.</summary>
    /// <param name="before">The terminal windows open before the launch (<see cref="Snapshot"/>).</param>
    public void Find(string id, HashSet<nint> before) => _ = Task.Run(async () =>
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < FindTimeout)
        {
            await Task.Delay(100).ConfigureAwait(false);
            var opened = Snapshot();
            opened.ExceptWith(before);
            opened.ExceptWith(_windows.Values);
            if (opened.Count == 1)
            {
                _windows[id] = opened.First();
                Save();
                Log.Debug($"Agent {id}: window {opened.First():X} ({Native.GetClassName(opened.First())}) after {watch.ElapsedMilliseconds} ms");
                return;
            }
            // Several new windows: another launch's window may not be claimed yet, so keep looking.
        }
        // A console window that Windows hands to a tab of an open Windows Terminal window has no window of its own.
        Log.Info($"Agent {id}: its terminal window was not found; it cannot be brought to the front");
    });

    /// <summary>The session's window is the active window, so the user is already looking at it.</summary>
    public bool IsForeground(string id) => _windows.TryGetValue(id, out nint hwnd) && Native.GetForegroundWindow() == hwnd;

    /// <summary>Brings the session's window to the front.</summary>
    /// <returns>False when its window is not known or has been closed.</returns>
    public bool Focus(string id)
    {
        if (!_windows.TryGetValue(id, out nint hwnd) || !IsOpen(hwnd))
            return false;
        if (Native.GetClassName(hwnd) == TerminalClass && CommandResolver.FindWindowsTerminal() is { } wt)
        {
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            var start = new ProcessStartInfo(wt) { UseShellExecute = false };
            foreach (string argument in (string[])["-w", WindowName(id), "focus-tab", "-t", "0"])
                start.ArgumentList.Add(argument);
            using var _ = Process.Start(start);
            return true;
        }
        if (Native.IsIconic(hwnd))
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
        return Native.SetForegroundWindow(hwnd);
    }

    /// <summary>Sessions whose window has been closed (without the agent saying so, e.g. when it crashed).</summary>
    public IReadOnlyList<string> Closed() => [.. _windows.Where(w => !IsOpen(w.Value)).Select(w => w.Key)];

    public void Forget(string id)
    {
        if (_windows.TryRemove(id, out _))
            Save();
    }

    /// <summary>Still a terminal window: a closed window's handle may be given to another window later.</summary>
    static bool IsOpen(nint hwnd) => Native.IsWindow(hwnd) && Native.GetClassName(hwnd) is TerminalClass or ConsoleClass;

    void Save()
    {
        if (stateFile is null)
            return;
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
                File.WriteAllLines(stateFile, _windows.Select(w => $"{w.Key} {(long)w.Value:X}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Remembering the agents' windows failed: {ex.Message}");
            }
        }
    }

    static ConcurrentDictionary<string, nint> Load(string? stateFile)
    {
        var windows = new ConcurrentDictionary<string, nint>(StringComparer.Ordinal);
        try
        {
            if (stateFile is null || !File.Exists(stateFile))
                return windows;
            foreach (string line in File.ReadAllLines(stateFile))
            {
                string[] parts = line.Split(' ');
                if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hwnd)
                    && IsOpen((nint)hwnd))
                    windows[parts[0]] = (nint)hwnd;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Reading the agents' windows failed: {ex.Message}");
        }
        return windows;
    }
}
