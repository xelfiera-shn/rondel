using System.Runtime.InteropServices;
using Rondel.Core.Logging;
using Rondel.MacOS.Trigger;

namespace Rondel.MacOS.Interop;

/// <summary>
/// Global keyboard shortcuts through Carbon's RegisterEventHotKey, which needs no permission (unlike listening to every
/// key). macOS delivers a press to the main thread as an event, whatever app is in front. Each shortcut has a number of
/// Rondel's own (the menu, Active Agents, the card, a screenshot).
/// </summary>
internal static unsafe partial class HotKeys
{
    const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    const uint EventClassKeyboard = 0x6B657962; // 'keyb'
    const uint EventHotKeyPressed = 5;
    const uint ParamDirectObject = 0x2D2D2D2D; // '----'
    const uint TypeEventHotKeyId = 0x686B6964; // 'hkid'
    const uint Signature = 0x526E646C; // 'Rndl'

    [StructLayout(LayoutKind.Sequential)]
    struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    static readonly Dictionary<int, nint> Registered = [];
    static bool s_installed;

    /// <summary>A shortcut was pressed (its number), on the main thread.</summary>
    public static event Action<int>? Pressed;

    /// <summary>Registers <paramref name="hotkey"/> as shortcut <paramref name="id"/>, replacing what it was.</summary>
    /// <returns>False when macOS refused it (an app of the same user already has it).</returns>
    public static bool Register(int id, Hotkey hotkey)
    {
        Unregister(id);
        Install();
        nint reference = 0;
        int status = RegisterEventHotKey(hotkey.KeyCode, hotkey.CarbonModifiers, new EventHotKeyId { Signature = Signature, Id = (uint)id },
            GetApplicationEventTarget(), 0, &reference);
        if (status != 0)
        {
            Log.Warn($"Registering shortcut {hotkey} failed ({status})");
            return false;
        }
        Registered[id] = reference;
        return true;
    }

    public static void Unregister(int id)
    {
        if (Registered.Remove(id, out nint reference))
            UnregisterEventHotKey(reference);
    }

    static void Install()
    {
        if (s_installed)
            return;
        var spec = new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed };
        nint handler = 0;
        int status = InstallEventHandler(GetApplicationEventTarget(), &OnHotKey, 1, &spec, 0, &handler);
        if (status != 0)
            Log.Warn($"Installing the shortcut handler failed ({status})");
        s_installed = status == 0;
    }

    [UnmanagedCallersOnly]
    static int OnHotKey(nint handlerCall, nint evt, nint userData)
    {
        try
        {
            EventHotKeyId id;
            if (GetEventParameter(evt, ParamDirectObject, TypeEventHotKeyId, null, (nuint)sizeof(EventHotKeyId), null, &id) == 0
                && id.Signature == Signature)
            {
                Pressed?.Invoke((int)id.Id);
            }
        }
        catch (Exception ex)
        {
            Log.Error("A shortcut failed", ex);
        }
        return 0; // noErr: handled
    }

    [LibraryImport(Carbon)]
    private static partial nint GetApplicationEventTarget();

    [LibraryImport(Carbon)]
    private static partial int InstallEventHandler(nint target, delegate* unmanaged<nint, nint, nint, int> handler, uint count,
        EventTypeSpec* types, nint userData, nint* handlerRef);

    [LibraryImport(Carbon)]
    private static partial int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, nint target, uint options, nint* hotKeyRef);

    [LibraryImport(Carbon)]
    private static partial int UnregisterEventHotKey(nint hotKeyRef);

    [LibraryImport(Carbon)]
    private static partial int GetEventParameter(nint evt, uint name, uint desiredType, uint* actualType, nuint bufferSize, nuint* actualSize, void* data);
}
