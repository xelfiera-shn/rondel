using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Rondel.Core.Logging;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.UI;

/// <summary>
/// Icons for the menu: actions name Segoe Fluent Icons code points (a Windows font), and the Mac menu draws the
/// SF Symbol that means the same, as a mask that takes the palette's colors. Unknown code points get the
/// terminal symbol, as Windows falls back to CommandPrompt.
/// </summary>
internal static class SymbolIcons
{
    /// <summary>Segoe Fluent Icons code point → SF Symbol name.</summary>
    static readonly Dictionary<char, string> Symbols = new()
    {
        [''] = "folder.badge.plus",              // NewFolder: New Project
        [''] = "text.bubble",                     // Message: New + Prompt
        [''] = "terminal",                        // CommandPrompt: Open
        [''] = "bubble.left.and.bubble.right",    // Chat: Ask
        [''] = "play",                            // Play: Continue
        [''] = "clock.arrow.circlepath",          // History: Resume
        [''] = "clock",                           // Recent: Recent Projects
        [''] = "folder",                          // Folder
        [''] = "desktopcomputer",                 // TVMonitor: the desktop
        [''] = "doc",                             // Page: a file
        [''] = "list.bullet",                     // BulletedList: plan mode
    };

    const string Fallback = "terminal";

    static readonly Dictionary<(string, double), Bitmap?> Cache = [];

    public static string NameFor(char glyph) => Symbols.TryGetValue(glyph, out string? name) ? name : Fallback;

    /// <summary>The symbol drawn at twice <paramref name="pointSize"/> (sharp on Retina), black on transparent; null if missing.</summary>
    public static Bitmap? Mask(string symbol, double pointSize)
    {
        if (Cache.TryGetValue((symbol, pointSize), out var cached))
            return cached;
        var bitmap = Render(symbol, pointSize * 2) ?? (symbol == Fallback ? null : Render(Fallback, pointSize * 2));
        if (bitmap is null)
            Log.Warn($"SF Symbol '{symbol}' could not be drawn");
        Cache[(symbol, pointSize)] = bitmap;
        return bitmap;
    }

    static Bitmap? Render(string symbol, double pointSize)
    {
        using var pool = new AutoreleasePool();
        nint name = CFString(symbol);
        nint image = Send(Class("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"), name, 0);
        CFRelease(name);
        if (image == 0)
            return null;
        nint configuration = Send(Class("NSImageSymbolConfiguration"), Sel("configurationWithPointSize:weight:"), pointSize, 0.0 /* regular */);
        image = Send(image, Sel("imageWithSymbolConfiguration:"), configuration);
        nint tiff = image == 0 ? 0 : Send(image, Sel("TIFFRepresentation"));
        nint rep = tiff == 0 ? 0 : Send(Class("NSBitmapImageRep"), Sel("imageRepWithData:"), tiff);
        nint png = rep == 0 ? 0 : Send(rep, Sel("representationUsingType:properties:"), (nint)4 /* PNG */, Send(Class("NSDictionary"), Sel("dictionary")));
        if (png == 0)
            return null;
        byte[] bytes = new byte[(int)SendUInt(png, Sel("length"))];
        Marshal.Copy(Send(png, Sel("bytes")), bytes, 0, bytes.Length);
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }
}
