using System.Runtime.InteropServices;

namespace Rondel.MacOS.Interop;

/// <summary>A point in global display coordinates: points, origin at the top left of the main display.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint(double x, double y)
{
    public double X = x;
    public double Y = y;

    public override readonly string ToString() => $"({X:F0}, {Y:F0})";
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public double X, Y, Width, Height;

    public readonly bool Contains(CGPoint p) => p.X >= X && p.X < X + Width && p.Y >= Y && p.Y < Y + Height;

    public override readonly string ToString() => $"({X:F0},{Y:F0} {Width:F0}x{Height:F0})";
}

/// <summary>Apple Events address, as AECreateDesc fills it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AEDesc
{
    public uint DescriptorType;
    public nint DataHandle;
}

/// <summary>
/// The parts of CoreFoundation, CoreGraphics, Accessibility, Apple Events and the Objective-C runtime Rondel
/// uses. Everything is LibraryImport, so it works in the NativeAOT build.
/// </summary>
internal static unsafe partial class Native
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    const string LibSystem = "/usr/lib/libSystem.B.dylib";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";

    // ---- Objective-C runtime ----

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Class(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Sel(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendInt32(nint receiver, nint selector, int arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, ulong arg1, nint arg2);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, double arg1, double arg2);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nuint SendUInt(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendBool(nint receiver, nint selector, nint arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendBool(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, [MarshalAs(UnmanagedType.U1)] bool arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, long arg);

    [LibraryImport(ObjC, EntryPoint = "object_getClass")]
    public static partial nint ClassOf(nint instance);

    [LibraryImport(ObjC, EntryPoint = "class_getInstanceMethod")]
    public static partial nint InstanceMethod(nint cls, nint selector);

    [LibraryImport(ObjC, EntryPoint = "class_getMethodImplementation")]
    public static partial nint MethodImplementation(nint cls, nint selector);

    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AddMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")]
    public static partial nint AutoreleasePoolPush();

    [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")]
    public static partial void AutoreleasePoolPop(nint pool);

    /// <summary>Releases what Objective-C calls on a thread without its own pool (detection, the tap) autorelease.</summary>
    public readonly ref struct AutoreleasePool()
    {
        readonly nint _pool = AutoreleasePoolPush();

        public void Dispose() => AutoreleasePoolPop(_pool);
    }

    /// <summary>
    /// Keeps App Nap away for the app's lifetime: it slows down an app nobody looks at, and a napping app's event tap
    /// is disabled by macOS for answering too slowly (seen after ten idle minutes). User-initiated work that still
    /// lets the Mac sleep, with low latency. The returned activity is held until the app exits.
    /// </summary>
    public static nint BeginLatencyCriticalActivity(string reason)
    {
        const ulong UserInitiatedAllowingIdleSystemSleep = 0x00FFFFFFUL & ~(1UL << 20), LatencyCritical = 0xFF00000000UL;
        nint text = CFString(reason);
        nint activity = Send(Send(Class("NSProcessInfo"), Sel("processInfo")), Sel("beginActivityWithOptions:reason:"),
            UserInitiatedAllowingIdleSystemSleep | LatencyCritical, text);
        CFRelease(text);
        return activity == 0 ? 0 : Send(activity, Sel("retain"));
    }

    /// <summary>An NSString's text, or null.</summary>
    public static string? String(nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    [LibraryImport(LibSystem, EntryPoint = "pthread_main_np")]
    public static partial int IsMainThread();

    // ---- CoreFoundation ----

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(nint value);

    [LibraryImport(CoreFoundation)]
    public static partial long CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFArrayGetValueAtIndex(nint array, long index);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFDictionaryGetValue(nint dictionary, nint key);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, long count, nint keyCallbacks, nint valueCallbacks);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValue(nint number, long type, void* value);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial long CFStringGetLength(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial long CFStringGetMaximumSizeForEncoding(long length, uint encoding);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(nint value, byte* buffer, long size, uint encoding);

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFGetTypeID(nint value);

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFURLGetTypeID();

    [LibraryImport(CoreFoundation)]
    public static partial nint CFURLCopyFileSystemPath(nint url, int pathStyle);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFURLCreateWithString(nint allocator, nint value, nint baseUrl);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopAddSource(nint runLoop, nint source, nint mode);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopRun();

    const uint Utf8 = 0x08000100;

    static readonly nint CoreFoundationLibrary = NativeLibrary.Load(CoreFoundation);

    /// <summary>The value of an exported constant such as kCFRunLoopCommonModes.</summary>
    static nint Constant(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));

    public static nint RunLoopCommonModes { get; } = Constant(CoreFoundationLibrary, "kCFRunLoopCommonModes");

    /// <summary>A CFString the caller owns (CFRelease); toll-free bridged to NSString.</summary>
    public static nint CFString(string value) => CFStringCreateWithCString(0, value, Utf8);

    public static string? CFStringToString(nint value)
    {
        if (value == 0)
            return null;
        long size = CFStringGetMaximumSizeForEncoding(CFStringGetLength(value), Utf8) + 1;
        byte[] buffer = new byte[size];
        fixed (byte* p = buffer)
            return CFStringGetCString(value, p, size, Utf8) ? Marshal.PtrToStringUTF8((nint)p) : null;
    }

    public static long Number(nint dictionary, nint key, long fallback = 0)
    {
        nint value = CFDictionaryGetValue(dictionary, key);
        long result = fallback;
        if (value != 0)
            CFNumberGetValue(value, 4 /* kCFNumberSInt64Type */, &result);
        return result;
    }

    public static double Double(nint dictionary, nint key, double fallback)
    {
        nint value = CFDictionaryGetValue(dictionary, key);
        double result = fallback;
        if (value != 0)
            CFNumberGetValue(value, 13 /* kCFNumberDoubleType */, &result);
        return result;
    }

    // ---- CoreGraphics: events and windows ----

    public const uint SessionEventTap = 1;
    public const uint LeftMouseDown = 1, LeftMouseUp = 2, LeftMouseDragged = 6;
    public const uint TapDisabledByTimeout = 0xFFFFFFFE, TapDisabledByUserInput = 0xFFFFFFFF;
    public const ulong FlagShift = 0x20000, FlagControl = 0x40000, FlagOption = 0x80000, FlagCommand = 0x100000;
    public const uint EventSourceUserData = 42;

    [LibraryImport(CoreGraphics)]
    public static partial nint CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest, nint callback, nint userInfo);

    [LibraryImport(CoreGraphics)]
    public static partial void CGEventTapEnable(nint tap, [MarshalAs(UnmanagedType.U1)] bool enable);

    [LibraryImport(CoreGraphics)]
    public static partial ulong CGEventGetFlags(nint evt);

    [LibraryImport(CoreGraphics)]
    public static partial CGPoint CGEventGetLocation(nint evt);

    [LibraryImport(CoreGraphics)]
    public static partial long CGEventGetIntegerValueField(nint evt, uint field);

    [LibraryImport(CoreGraphics)]
    public static partial void CGEventSetIntegerValueField(nint evt, uint field, long value);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGEventCreate(nint source);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGEventCreateCopy(nint evt);

    [LibraryImport(CoreGraphics)]
    public static partial void CGEventPost(uint tap, nint evt);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CGRectMakeWithDictionaryRepresentation(nint dictionary, out CGRect rect);

    /// <summary>Where the mouse is now.</summary>
    public static CGPoint CursorLocation()
    {
        nint evt = CGEventCreate(0);
        var point = CGEventGetLocation(evt);
        CFRelease(evt);
        return point;
    }

    // ---- Accessibility ----

    [LibraryImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AXIsProcessTrusted();

    [LibraryImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool AXIsProcessTrustedWithOptions(nint options);

    [LibraryImport(ApplicationServices)]
    public static partial nint AXUIElementCreateSystemWide();

    [LibraryImport(ApplicationServices)]
    public static partial int AXUIElementCopyElementAtPosition(nint application, float x, float y, out nint element);

    [LibraryImport(ApplicationServices)]
    public static partial int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);

    [LibraryImport(ApplicationServices)]
    public static partial int AXUIElementSetMessagingTimeout(nint element, float seconds);

    [LibraryImport(ApplicationServices)]
    public static partial int AXUIElementGetPid(nint element, out int pid);

    /// <summary>Asks for the Accessibility permission: macOS shows its own prompt that leads to System Settings.</summary>
    public static bool RequestAccessibility()
    {
        nint key = CFString("AXTrustedCheckOptionPrompt");
        nint value = Constant(CoreFoundationLibrary, "kCFBooleanTrue");
        nint options = CFDictionaryCreate(0, &key, &value, 1,
            NativeLibrary.GetExport(CoreFoundationLibrary, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(CoreFoundationLibrary, "kCFTypeDictionaryValueCallBacks"));
        bool trusted = AXIsProcessTrustedWithOptions(options);
        CFRelease(options);
        CFRelease(key);
        return trusted;
    }

    // ---- Apple Events permission ----

    const uint TypeApplicationBundleId = 0x62756E64; // 'bund'
    const uint TypeWildCard = 0x2A2A2A2A; // '****'

    /// <summary>The Automation permission is granted.</summary>
    public const int AutomationAllowed = 0;

    /// <summary>The user said no (errAEEventNotPermitted).</summary>
    public const int AutomationDenied = -1743;

    /// <summary>Not asked yet (errAEEventWouldRequireUserConsent).</summary>
    public const int AutomationNotAsked = -1744;

    [LibraryImport(CoreServices)]
    private static partial short AECreateDesc(uint typeCode, byte* data, nint dataSize, AEDesc* result);

    [LibraryImport(CoreServices)]
    private static partial short AEDisposeDesc(AEDesc* desc);

    [LibraryImport(CoreServices)]
    private static partial int AEDeterminePermissionToAutomateTarget(AEDesc* target, uint eventClass, uint eventId, [MarshalAs(UnmanagedType.U1)] bool askUserIfNeeded);

    /// <summary>
    /// Whether Rondel may send Apple Events to the app with <paramref name="bundleId"/>. With
    /// <paramref name="ask"/>, macOS shows its prompt when the user was not asked yet, and this waits for the answer.
    /// </summary>
    /// <returns><see cref="AutomationAllowed"/>, <see cref="AutomationDenied"/>, <see cref="AutomationNotAsked"/> or another OSStatus.</returns>
    public static int AutomationPermission(string bundleId, bool ask)
    {
        byte[] id = System.Text.Encoding.UTF8.GetBytes(bundleId);
        AEDesc target;
        fixed (byte* p = id)
        {
            if (AECreateDesc(TypeApplicationBundleId, p, id.Length, &target) != 0)
                return -1;
        }
        int status = AEDeterminePermissionToAutomateTarget(&target, TypeWildCard, TypeWildCard, ask);
        AEDisposeDesc(&target);
        return status;
    }
}
