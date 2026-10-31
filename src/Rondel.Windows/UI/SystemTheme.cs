using System.Windows.Media;
using Microsoft.Win32;

namespace Rondel.Windows.UI;

/// <param name="AccentForDark">Accent shade Windows uses on dark surfaces (AccentLight2).</param>
/// <param name="AccentForLight">Accent shade Windows uses on light surfaces (AccentDark1).</param>
internal sealed record ThemeColors(bool IsDark, Color AccentForDark, Color AccentForLight)
{
    public Color Accent => IsDark ? AccentForDark : AccentForLight;
}

/// <summary>Reads the app light/dark mode and the accent color the same way on Windows 10 and 11.</summary>
internal static class SystemTheme
{
    static readonly Color FallbackAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    public static ThemeColors Read()
    {
        bool dark = false;
        Color forDark = Lighten(FallbackAccent), forLight = FallbackAccent;
        try
        {
            using (var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                dark = personalize?.GetValue("AppsUseLightTheme") is int light && light == 0;

            using var accent = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (accent?.GetValue("AccentPalette") is byte[] { Length: >= 32 } palette)
            {
                // 8 RGBA entries: Light3, Light2, Light1, Base, Dark1, Dark2, Dark3, ...
                forDark = Color.FromRgb(palette[4], palette[5], palette[6]);
                forLight = Color.FromRgb(palette[16], palette[17], palette[18]);
            }
            else if (accent?.GetValue("AccentColorMenu") is int abgr)
            {
                forLight = Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
                forDark = Lighten(forLight);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return new ThemeColors(dark, forDark, forLight);
    }

    static Color Lighten(Color c) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * 0.45), (byte)(c.G + (255 - c.G) * 0.45), (byte)(c.B + (255 - c.B) * 0.45));
}

/// <summary>Brushes for the radial menu; an "acrylic-like" tinted surface drawn by WPF itself.</summary>
internal sealed class RadialPalette
{
    public required Brush Ring { get; init; }
    public required Brush RingStroke { get; init; }
    public required Brush Center { get; init; }
    public required Brush CenterStroke { get; init; }
    public required Brush Separator { get; init; }
    public required Brush Hover { get; init; }
    public required Brush HoverText { get; init; }
    public required Brush Text { get; init; }
    public required Brush SubText { get; init; }
    public required Brush Card { get; init; }
    public required Brush CardStroke { get; init; }
    public required Brush InputBackground { get; init; }
    public required Brush Error { get; init; }
    public required double ShadowOpacity { get; init; }

    public static RadialPalette From(ThemeColors theme) => theme.IsDark
        ? new RadialPalette
        {
            Ring = Solid(0xF2, 0x24, 0x24, 0x26),
            RingStroke = Solid(0x38, 0xFF, 0xFF, 0xFF),
            Center = Solid(0xFF, 0x30, 0x30, 0x33),
            CenterStroke = Solid(0x24, 0xFF, 0xFF, 0xFF),
            Separator = Solid(0x1F, 0xFF, 0xFF, 0xFF),
            Hover = Freeze(new SolidColorBrush(theme.Accent)),
            HoverText = Solid(0xFF, 0x00, 0x00, 0x00),
            Text = Solid(0xFF, 0xFF, 0xFF, 0xFF),
            SubText = Solid(0xA8, 0xFF, 0xFF, 0xFF),
            Card = Solid(0xFF, 0x2B, 0x2B, 0x2E),
            CardStroke = Solid(0x33, 0xFF, 0xFF, 0xFF),
            InputBackground = Solid(0xFF, 0x1E, 0x1E, 0x20),
            Error = Solid(0xFF, 0xFF, 0x99, 0xA4),
            ShadowOpacity = 0.5,
        }
        : new RadialPalette
        {
            Ring = Solid(0xF5, 0xF9, 0xF9, 0xFA),
            RingStroke = Solid(0x1F, 0x00, 0x00, 0x00),
            Center = Solid(0xFF, 0xFF, 0xFF, 0xFF),
            CenterStroke = Solid(0x17, 0x00, 0x00, 0x00),
            Separator = Solid(0x14, 0x00, 0x00, 0x00),
            Hover = Freeze(new SolidColorBrush(theme.Accent)),
            HoverText = Solid(0xFF, 0xFF, 0xFF, 0xFF),
            Text = Solid(0xE6, 0x00, 0x00, 0x00),
            SubText = Solid(0x8F, 0x00, 0x00, 0x00),
            Card = Solid(0xFF, 0xFB, 0xFB, 0xFB),
            CardStroke = Solid(0x1F, 0x00, 0x00, 0x00),
            InputBackground = Solid(0xFF, 0xFF, 0xFF, 0xFF),
            Error = Solid(0xFF, 0xC4, 0x2B, 0x1C),
            ShadowOpacity = 0.28,
        };

    static Brush Solid(byte a, byte r, byte g, byte b) => Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
