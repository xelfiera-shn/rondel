using Avalonia.Threading;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Interop;

/// <summary>Runs an AppleScript (Apple Events to Finder, Terminal, iTerm2). NSAppleScript is not thread-safe, so it runs on the main thread.</summary>
internal static class AppleScript
{
    /// <returns>The script's result as text, or null with <paramref name="error"/> saying why.</returns>
    public static string? Run(string source, out string? error)
    {
        string? answer = null, failure = null;
        if (IsMainThread() != 0)
            answer = RunHere(source, out failure);
        else
            Dispatcher.UIThread.Invoke(() => answer = RunHere(source, out failure));
        error = failure;
        return answer;
    }

    static unsafe string? RunHere(string source, out string? error)
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
