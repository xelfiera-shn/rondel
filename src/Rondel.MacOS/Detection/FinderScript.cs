using System.Globalization;
using Avalonia.Threading;
using Rondel.MacOS.Interop;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Detection;

/// <summary>
/// Asks Finder (Apple Events) which folder the Finder window at a point shows, and which items are selected. Needs
/// the Automation permission for Finder, which is asked at start or from the menu bar, never here: without it these
/// return null at once. NSAppleScript is not thread-safe, so the scripts run on the main thread.
/// </summary>
internal static class FinderScript
{
    /// <summary>
    /// The folder the Finder window at the point shows: the windows' bounds first, in one request, then the target of
    /// the frontmost window that contains the point.
    /// </summary>
    /// <returns>The folder, or null with <paramref name="reason"/> saying why not.</returns>
    public static string? FolderOfWindowAt(CGPoint point, out string reason)
    {
        if (!Allowed(out reason))
            return null;

        string x = point.X.ToString("F0", CultureInfo.InvariantCulture), y = point.Y.ToString("F0", CultureInfo.InvariantCulture);
        // Finder lists its windows front to back; bounds are {left, top, right, bottom} in the same points as the pointer.
        // The script runs on the main thread, and AppleScript would wait two minutes for a hung Finder: one second
        // keeps it within the gesture's own limit.
        string source = $$"""
            with timeout of 1 second
                tell application "Finder"
                    set allBounds to bounds of every Finder window
                    repeat with i from 1 to count of allBounds
                        set b to item i of allBounds
                        if {{x}} ≥ item 1 of b and {{x}} < item 3 of b and {{y}} ≥ item 2 of b and {{y}} < item 4 of b then
                            try
                                return URL of target of Finder window i
                            on error
                                return "virtual"
                            end try
                        end if
                    end repeat
                    return "none"
                end tell
            end timeout
            """;
        string? answer = RunOnMain(source, out string? error);
        if (answer is null)
        {
            reason = $"Finder did not answer: {error}";
            return null;
        }
        if (!answer.StartsWith("file://", StringComparison.Ordinal))
        {
            reason = answer == "virtual" ? "the window shows a virtual location" : "no Finder window at the point";
            return null;
        }
        reason = $"window shows {answer}";
        return new Uri(answer).LocalPath;
    }

    /// <summary>
    /// The POSIX paths of Finder's selection: the frontmost Finder window's, or the desktop's while the desktop has the
    /// focus, in Finder's order (folders end with a slash). Holding does not reach Finder, so it is still the selection
    /// the user made.
    /// </summary>
    /// <returns>The paths, or null with <paramref name="reason"/> saying why not, also when more than
    /// <paramref name="max"/> items are selected.</returns>
    public static IReadOnlyList<string>? Selection(int max, out string reason)
    {
        if (!Allowed(out reason))
            return null;
        // One request for the whole selection; turning the aliases into paths needs no more of Finder.
        string source = $$"""
            with timeout of 1 second
                tell application "Finder" to set chosen to selection as alias list
            end timeout
            if (count of chosen) > {{max}} then return "too many"
            set out to "selection" & linefeed
            repeat with f in chosen
                set out to out & POSIX path of f & linefeed
            end repeat
            return out
            """;
        string? answer = RunOnMain(source, out string? error);
        if (answer is null)
        {
            reason = $"Finder did not answer: {error}";
            return null;
        }
        if (answer == "too many")
        {
            reason = $"more than {max} items are selected";
            return null;
        }
        var paths = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        reason = $"{paths.Count} item(s) selected";
        return paths;
    }

    static bool Allowed(out string reason)
    {
        int permission = AutomationPermission(Workspace.FinderId, ask: false);
        reason = permission == AutomationAllowed ? ""
            : permission == AutomationDenied ? "no Automation permission for Finder (denied)"
            : $"no Automation permission for Finder yet ({permission})";
        return permission == AutomationAllowed;
    }

    static string? RunOnMain(string source, out string? error)
    {
        string? answer = null, failure = null;
        if (IsMainThread() != 0)
            answer = Run(source, out failure);
        else
            Dispatcher.UIThread.Invoke(() => answer = Run(source, out failure));
        error = failure;
        return answer;
    }

    /// <summary>On the main thread.</summary>
    static unsafe string? Run(string source, out string? error)
    {
        using var pool = new AutoreleasePool();
        nint text = CFString(source);
        nint script = Send(Send(Class("NSAppleScript"), Sel("alloc")), Sel("initWithSource:"), text);
        CFRelease(text);
        nint errorInfo = 0;
        nint result = Send(script, Sel("executeAndReturnError:"), (nint)(&errorInfo));
        string? answer = result == 0 ? null : String(Send(result, Sel("stringValue")));
        error = result == 0 ? (errorInfo == 0 ? "unknown error" : String(Send(errorInfo, Sel("description")))) : null;
        Send(script, Sel("release"));
        return answer;
    }
}
