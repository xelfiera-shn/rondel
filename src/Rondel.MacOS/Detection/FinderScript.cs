using System.Globalization;
using Avalonia.Threading;
using Rondel.MacOS.Interop;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Detection;

/// <summary>
/// Asks Finder (Apple Events) which folder the Finder window at a point shows: its bounds first, in one request,
/// then the target of the frontmost window that contains the point. Needs the Automation permission for Finder,
/// which is asked at start or from the menu bar, never here: without it this returns null at once.
/// NSAppleScript is not thread-safe, so the script runs on the main thread.
/// </summary>
internal static class FinderScript
{
    /// <returns>The folder, or null with <paramref name="reason"/> saying why not.</returns>
    public static string? FolderOfWindowAt(CGPoint point, out string reason)
    {
        int permission = AutomationPermission(Workspace.FinderId, ask: false);
        if (permission != AutomationAllowed)
        {
            reason = permission == AutomationDenied
                ? "no Automation permission for Finder (denied)"
                : $"no Automation permission for Finder yet ({permission})";
            return null;
        }

        string x = point.X.ToString("F0", CultureInfo.InvariantCulture), y = point.Y.ToString("F0", CultureInfo.InvariantCulture);
        // Finder lists its windows front to back; bounds are {left, top, right, bottom} in the same points as the pointer.
        string source = $$"""
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
            """;
        string? answer = null, error = null;
        if (IsMainThread() != 0)
            answer = Run(source, out error);
        else
            Dispatcher.UIThread.Invoke(() => answer = Run(source, out error));

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
