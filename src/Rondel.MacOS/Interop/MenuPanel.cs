using System.Runtime.InteropServices;
using Rondel.Core.Logging;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Interop;

/// <summary>
/// What makes Avalonia's window behave like the Spotlight panel: above full-screen apps on the current Space, and
/// taking the keyboard without activating Rondel, so Finder keeps its active title bar and menu bar.
/// <para>
/// The window level and Spaces behavior are public API. Taking the keyboard without activating relies on two
/// private NSWindow selectors (what NSPanel does for NSWindowStyleMaskNonactivatingPanel). When a macOS no longer
/// has them, the menu activates Rondel instead and gives the focus back to the previous app when it closes: it then
/// takes the active state from Finder while it is open, but works.
/// </para>
/// </summary>
internal static unsafe class MenuPanel
{
    const long PopUpMenuLevel = 101; // NSPopUpMenuWindowLevel
    // moveToActiveSpace | transient | ignoresCycle | fullScreenAuxiliary. Not canJoinAllSpaces: with it the open
    // menu followed the user to another Space and stayed there without the keyboard.
    const long Behavior = 2 | 8 | 64 | 256;

    static readonly HashSet<nint> Panels = [];
    static nint s_original;
    static nint s_previousApp;

    /// <returns>True when the window is a non-activating panel; false for the fallback.</returns>
    public static bool Configure(nint window)
    {
        if (window == 0)
            return false;
        SendVoid(window, Sel("setLevel:"), PopUpMenuLevel);
        SendVoid(window, Sel("setCollectionBehavior:"), Behavior);

        nint nsWindow = Class("NSWindow"), avnWindow = Class("AvnWindow");
        nint prevents = Sel("_setPreventsActivation:"), isPanel = Sel("_isNonactivatingPanel");
        if (avnWindow == 0 || InstanceMethod(nsWindow, prevents) == 0 || InstanceMethod(nsWindow, isPanel) == 0)
        {
            Log.Warn("Menu window: this macOS has no non-activating window API; the menu activates Rondel while it is open");
            return false;
        }
        // The answer of _isNonactivatingPanel decides whether a key window may stay in an inactive app. It is added
        // to AvnWindow (not by replacing the window's class: macOS may have given the window a KVO subclass, and
        // after replacing it the window came back 0×0) and says YES for the menu only.
        if (s_original == 0)
        {
            s_original = MethodImplementation(nsWindow, isPanel);
            AddMethod(avnWindow, isPanel, (nint)(delegate* unmanaged<nint, nint, byte>)&IsNonactivatingPanel, "c@:");
        }
        Panels.Add(window);
        SendVoid(window, prevents, true);
        Log.Info("Menu window: non-activating panel");
        return true;
    }

    [UnmanagedCallersOnly]
    static byte IsNonactivatingPanel(nint self, nint selector) =>
        Panels.Contains(self) ? (byte)1 : ((delegate* unmanaged<nint, nint, byte>)s_original)(self, selector);

    /// <summary>Before the menu shows: the fallback remembers the app to give the focus back to.</summary>
    public static void BeforeShow(nint window, bool nonActivating)
    {
        if (nonActivating || window == 0)
            return;
        nint workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
        nint app = Send(workspace, Sel("frontmostApplication"));
        if (s_previousApp != 0)
            Send(s_previousApp, Sel("release"));
        s_previousApp = app == 0 ? 0 : Send(app, Sel("retain"));
    }

    /// <summary>After the menu is shown: it becomes the key window, so it gets the keyboard.</summary>
    public static void TakeKey(nint window, bool nonActivating)
    {
        if (window == 0)
            return;
        if (!nonActivating)
            SendVoid(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("activateIgnoringOtherApps:"), true);
        Send(window, Sel("makeKeyAndOrderFront:"), 0);
    }

    /// <summary>After the menu closed: the fallback gives the focus back when nothing was started.</summary>
    public static void AfterClose(bool nonActivating, bool restoreFocus)
    {
        if (nonActivating || !restoreFocus || s_previousApp == 0)
            return;
        SendVoid(s_previousApp, Sel("activateWithOptions:"), 0L);
    }

    /// <summary>Reduce Motion is on, or the Mac is in Low Power Mode.</summary>
    public static bool ReduceMotion
    {
        get
        {
            nint workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
            nint process = Send(Class("NSProcessInfo"), Sel("processInfo"));
            return SendBool(workspace, Sel("accessibilityDisplayShouldReduceMotion")) || SendBool(process, Sel("isLowPowerModeEnabled"));
        }
    }
}
