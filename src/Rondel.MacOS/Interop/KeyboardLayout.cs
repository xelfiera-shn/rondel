using System.Globalization;
using System.Runtime.InteropServices;

namespace Rondel.MacOS.Interop;

/// <summary>
/// What the current keyboard layout types on a key, so a shortcut kept by the key's place reads as the user sees it:
/// on a German keyboard the key in the US Y's place shows Z, on AZERTY the A and Q swap. Call it on the main thread
/// (the Text Input Sources calls ask for it).
/// </summary>
internal static unsafe partial class KeyboardLayout
{
    const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const ushort KeyActionDisplay = 3;          // kUCKeyActionDisplay
    const uint NoDeadKeys = 1;                  // kUCKeyTranslateNoDeadKeysMask

    static readonly Lazy<nint> UnicodeKeyLayoutData = new(() =>
    {
        // kTISPropertyUnicodeKeyLayoutData is a CFStringRef variable, not a function.
        nint library = NativeLibrary.Load(Carbon);
        return NativeLibrary.TryGetExport(library, "kTISPropertyUnicodeKeyLayoutData", out nint address) ? *(nint*)address : 0;
    });

    /// <summary>
    /// The upper-cased character the current layout types on <paramref name="keyCode"/> with no modifier, or null when
    /// the layout gives none (an input method without a key layout, a dead key, a control character).
    /// </summary>
    public static string? KeyName(uint keyCode)
    {
        try
        {
            nint property = UnicodeKeyLayoutData.Value;
            if (property == 0)
                return null;
            nint source = TISCopyCurrentKeyboardLayoutInputSource();
            if (source == 0)
                return null;
            try
            {
                nint data = TISGetInputSourceProperty(source, property);
                if (data == 0)
                    return null;
                byte* layout = CFDataGetBytePtr(data);
                uint deadKeyState = 0;
                nuint length = 0;
                char* text = stackalloc char[4];
                int status = UCKeyTranslate(layout, (ushort)keyCode, KeyActionDisplay, 0, LMGetKbdType(), NoDeadKeys,
                    &deadKeyState, 4, &length, text);
                if (status != 0 || length == 0)
                    return null;
                string name = new string(text, 0, (int)length).ToUpper(CultureInfo.CurrentCulture);
                return name.All(c => !char.IsControl(c) && !char.IsWhiteSpace(c)) ? name : null;
            }
            finally
            {
                CFRelease(source);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport(Carbon)]
    private static partial nint TISCopyCurrentKeyboardLayoutInputSource();

    [LibraryImport(Carbon)]
    private static partial nint TISGetInputSourceProperty(nint source, nint key);

    [LibraryImport(Carbon)]
    private static partial byte LMGetKbdType();

    [LibraryImport(Carbon)]
    private static partial int UCKeyTranslate(byte* layout, ushort virtualKeyCode, ushort keyAction, uint modifierKeyState,
        uint keyboardType, uint keyTranslateOptions, uint* deadKeyState, nuint maxStringLength, nuint* actualStringLength, char* unicodeString);

    [LibraryImport(CoreFoundation)]
    private static partial byte* CFDataGetBytePtr(nint data);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
