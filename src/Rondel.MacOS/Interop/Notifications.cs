using System.Runtime.InteropServices;
using Avalonia.Threading;
using Rondel.Core.Logging;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Interop;

/// <summary>
/// macOS notifications through UserNotifications, the counterpart of the Windows tray balloon: a title, a line of
/// text, and something to do when the user clicks it. macOS asks the user once whether Rondel may notify; until it
/// may (or when it may not), a notification is not shown and <c>denied</c> runs instead, so the caller can fall back.
/// <para>
/// UserNotifications reports through an Objective-C delegate and blocks, so this registers a small delegate class
/// with the runtime and passes global blocks (they capture nothing; what they need is kept here).
/// </para>
/// </summary>
internal static unsafe partial class Notifications
{
    const ulong Alert = 1 << 2, Sound = 1 << 1;
    const ulong PresentSound = 1 << 1, PresentList = 1 << 3, PresentBanner = 1 << 4;
    const int BlockIsGlobal = 1 << 28;

    static readonly Lock Gate = new();
    static readonly Dictionary<string, Action> Clicks = [];
    static readonly List<(string Id, string Title, string Body, Action? Denied)> Pending = [];
    static nint s_center;
    static bool s_ready;
    static long s_lastClick;

    /// <summary>
    /// Whether the user clicked one of Rondel's notifications within <paramref name="span"/>. macOS reopens the app
    /// for such a click too, which on its own would open Settings.
    /// </summary>
    public static bool ClickedWithin(TimeSpan span) =>
        Interlocked.Read(ref s_lastClick) is var at and not 0 && Environment.TickCount64 - at < span.TotalMilliseconds;

    /// <summary>
    /// Shows a notification; a click on it runs <paramref name="clicked"/> on the UI thread. When Rondel may not notify
    /// (the user said no, or this is not the app bundle), <paramref name="denied"/> runs on the UI thread instead.
    /// </summary>
    public static void Show(string title, string body, Action clicked, Action? denied = null)
    {
        if (!EnsureCenter())
        {
            if (denied is not null)
                Dispatcher.UIThread.Post(denied);
            return;
        }
        string id = Guid.NewGuid().ToString("N");
        lock (Gate)
        {
            Clicks[id] = clicked;
            Pending.Add((id, title, body, denied));
        }
        // Answers at once when the user has already decided; the first time, macOS asks them.
        Send(s_center, Sel("requestAuthorizationWithOptions:completionHandler:"), Alert | Sound, AuthorizationBlock);
    }

    static bool EnsureCenter()
    {
        if (s_ready)
            return s_center != 0;
        s_ready = true;
        // Outside the app bundle (a diagnostic run from the build folder) UserNotifications stops the process.
        nint bundleId = Send(Send(Class("NSBundle"), Sel("mainBundle")), Sel("bundleIdentifier"));
        if (bundleId == 0 || !NativeLibrary.TryLoad("/System/Library/Frameworks/UserNotifications.framework/UserNotifications", out _))
        {
            Log.Warn("Notifications are not available: Rondel does not run as its app bundle");
            return false;
        }
        nint delegateClass = objc_allocateClassPair(Class("NSObject"), "RondelNotificationDelegate", 0);
        AddMethod(delegateClass, Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidReceive, "v@:@@@?");
        AddMethod(delegateClass, Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&WillPresent, "v@:@@@?");
        if (objc_getProtocol("UNUserNotificationCenterDelegate") is var protocol and not 0)
            class_addProtocol(delegateClass, protocol);
        objc_registerClassPair(delegateClass);
        nint handler = Send(Send(delegateClass, Sel("alloc")), Sel("init"));
        s_center = Send(Class("UNUserNotificationCenter"), Sel("currentNotificationCenter"));
        Send(s_center, Sel("setDelegate:"), handler); // the delegate is weak; handler is never released
        return s_center != 0;
    }

    /// <summary>The answer to requestAuthorization: post what waits, or let the callers fall back.</summary>
    [UnmanagedCallersOnly]
    static void Authorized(nint block, byte granted, nint error)
    {
        List<(string Id, string Title, string Body, Action? Denied)> waiting;
        lock (Gate)
        {
            waiting = [.. Pending];
            Pending.Clear();
        }
        if (granted == 0)
        {
            string? reason = error == 0 ? null : String(Send(error, Sel("localizedDescription")));
            Log.Info($"Notifications are not allowed for Rondel{(reason is null ? "" : $": {reason}")}");
            foreach (var (id, _, _, denied) in waiting)
            {
                lock (Gate)
                    Clicks.Remove(id);
                if (denied is not null)
                    Dispatcher.UIThread.Post(denied);
            }
            return;
        }
        using var pool = new AutoreleasePool();
        foreach (var (id, title, body, _) in waiting)
        {
            nint content = Send(Send(Class("UNMutableNotificationContent"), Sel("alloc")), Sel("init"));
            nint titleText = CFString(title), bodyText = CFString(body), idText = CFString(id);
            Send(content, Sel("setTitle:"), titleText);
            Send(content, Sel("setBody:"), bodyText);
            nint request = Send(Class("UNNotificationRequest"), Sel("requestWithIdentifier:content:trigger:"), idText, content, 0);
            Send(s_center, Sel("addNotificationRequest:withCompletionHandler:"), request, AddedBlock);
            CFRelease(titleText);
            CFRelease(bodyText);
            CFRelease(idText);
            Send(content, Sel("release"));
        }
    }

    /// <summary>The answer to addNotificationRequest: whether macOS took the notification.</summary>
    [UnmanagedCallersOnly]
    static void Added(nint block, nint error)
    {
        if (error == 0)
            Log.Debug("Notification posted");
        else
            Log.Warn($"Posting a notification failed: {String(Send(error, Sel("localizedDescription")))}");
    }

    [UnmanagedCallersOnly]
    static void DidReceive(nint self, nint selector, nint center, nint response, nint completion)
    {
        Interlocked.Exchange(ref s_lastClick, Environment.TickCount64);
        string? id = String(Send(Send(Send(response, Sel("notification")), Sel("request")), Sel("identifier")));
        Action? clicked = null;
        lock (Gate)
        {
            if (id is not null && Clicks.Remove(id, out var action))
                clicked = action;
        }
        if (clicked is not null)
            Dispatcher.UIThread.Post(clicked);
        ((delegate* unmanaged<nint, void>)((BlockLiteral*)completion)->Invoke)(completion);
    }

    /// <summary>Rondel has no windows most of the time, but when one is in front a notification still shows.</summary>
    [UnmanagedCallersOnly]
    static void WillPresent(nint self, nint selector, nint center, nint notification, nint completion) =>
        ((delegate* unmanaged<nint, ulong, void>)((BlockLiteral*)completion)->Invoke)(completion, PresentBanner | PresentList | PresentSound);

    // ---- blocks ----

    [StructLayout(LayoutKind.Sequential)]
    struct BlockDescriptor
    {
        public ulong Reserved;
        public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public BlockDescriptor* Descriptor;
    }

    /// <summary>A global block that calls <see cref="Authorized"/>; it lives as long as the process.</summary>
    static readonly nint AuthorizationBlock = GlobalBlock((nint)(delegate* unmanaged<nint, byte, nint, void>)&Authorized);

    /// <summary>A global block that calls <see cref="Added"/>.</summary>
    static readonly nint AddedBlock = GlobalBlock((nint)(delegate* unmanaged<nint, nint, void>)&Added);

    static nint GlobalBlock(nint invoke)
    {
        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        descriptor->Size = (ulong)sizeof(BlockLiteral);
        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        block->Isa = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");
        block->Flags = BlockIsGlobal;
        block->Invoke = invoke;
        block->Descriptor = descriptor;
        return (nint)block;
    }

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);

    [LibraryImport("/usr/lib/libobjc.A.dylib")]
    private static partial void objc_registerClassPair(nint cls);

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getProtocol(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addProtocol(nint cls, nint protocol);
}
