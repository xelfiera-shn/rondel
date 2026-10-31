using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace Rondel.Windows.UI;

internal sealed record ThemeColors(bool IsDark);

/// <summary>Reads the app light/dark mode the same way on Windows 10 and 11.</summary>
internal static class SystemTheme
{
    public static ThemeColors Read()
    {
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return new ThemeColors(personalize?.GetValue("AppsUseLightTheme") is int light && light == 0);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new ThemeColors(false);
        }
    }
}

/// <summary>
/// Rondel's own colors, independent of the Windows accent. Ink surfaces carry the content; the brand
/// pair (teal → violet) marks whatever is active: the lit rim of the hovered slice, the hub ring and
/// the arrow of a slice that expands.
/// </summary>
internal sealed class RondelPalette
{
    public required Color BrandFrom { get; init; }
    public required Color BrandTo { get; init; }
    public required Brush RingFill { get; init; }
    public required Brush RingStroke { get; init; }
    public required Brush TileFill { get; init; }
    public required Brush TileStroke { get; init; }
    public required Brush RimIdle { get; init; }
    public required Color WashInner { get; init; }
    public required Color WashOuter { get; init; }
    public required Brush Text { get; init; }
    public required Brush SubText { get; init; }
    public required Brush HubFill { get; init; }
    public required Brush Card { get; init; }
    public required Brush InputBackground { get; init; }
    public required Brush Error { get; init; }
    public required double ShadowOpacity { get; init; }

    public Brush Accent => Freeze(new SolidColorBrush(BrandFrom));

    /// <summary>Brand gradient across an element's own bounds (hub ring, card border, icons).</summary>
    public Brush Brand => Freeze(new LinearGradientBrush(BrandFrom, BrandTo, new Point(0, 0), new Point(1, 1)));

    /// <summary>Nearly transparent fill: invisible, but makes an area receive the mouse in the layered window.</summary>
    public static Brush Catcher { get; } = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    /// <summary>Soft brand-colored glow around lit elements.</summary>
    public Effect Glow => Freeze(new DropShadowEffect { Color = BrandFrom, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.9 });

    /// <summary>Brand gradient between two points in canvas coordinates (e.g. along a slice's rim).</summary>
    public Brush BrandBetween(Point from, Point to) =>
        Freeze(new LinearGradientBrush(BrandFrom, BrandTo, from, to) { MappingMode = BrushMappingMode.Absolute });

    /// <summary>Hover wash that deepens from the inner to the outer edge of a slice.</summary>
    public Brush WashBetween(Point inner, Point outer) =>
        Freeze(new LinearGradientBrush(WashInner, WashOuter, inner, outer) { MappingMode = BrushMappingMode.Absolute });

    public static RondelPalette For(ThemeColors theme) => theme.IsDark
        ? new RondelPalette
        {
            BrandFrom = Color.FromRgb(0x2D, 0xD4, 0xBF),
            BrandTo = Color.FromRgb(0xA7, 0x8B, 0xFA),
            RingFill = Solid(0xF0, 0x10, 0x12, 0x19),
            RingStroke = Solid(0x1A, 0xFF, 0xFF, 0xFF),
            TileFill = Solid(0xFF, 0x18, 0x1B, 0x26),
            TileStroke = Solid(0x10, 0xFF, 0xFF, 0xFF),
            RimIdle = Solid(0x16, 0xFF, 0xFF, 0xFF),
            WashInner = Color.FromArgb(0x00, 0x2D, 0xD4, 0xBF),
            WashOuter = Color.FromArgb(0x40, 0x2D, 0xD4, 0xBF),
            Text = Solid(0xFF, 0xEE, 0xF0, 0xF7),
            SubText = Solid(0xFF, 0x8B, 0x93, 0xA7),
            HubFill = Solid(0xFF, 0x1D, 0x21, 0x30),
            Card = Solid(0xFF, 0x16, 0x19, 0x22),
            InputBackground = Solid(0xFF, 0x0F, 0x11, 0x18),
            Error = Solid(0xFF, 0xFF, 0x8A, 0x9B),
            ShadowOpacity = 0.55,
        }
        : new RondelPalette
        {
            BrandFrom = Color.FromRgb(0x0D, 0x94, 0x88),
            BrandTo = Color.FromRgb(0x7C, 0x3A, 0xED),
            RingFill = Solid(0xF5, 0xEC, 0xEE, 0xF5),
            RingStroke = Solid(0x14, 0x00, 0x00, 0x00),
            TileFill = Solid(0xFF, 0xFF, 0xFF, 0xFF),
            TileStroke = Solid(0x12, 0x0B, 0x10, 0x20),
            RimIdle = Solid(0x14, 0x0B, 0x10, 0x20),
            WashInner = Color.FromArgb(0x00, 0x0D, 0x94, 0x88),
            WashOuter = Color.FromArgb(0x2E, 0x0D, 0x94, 0x88),
            Text = Solid(0xFF, 0x16, 0x1A, 0x26),
            SubText = Solid(0xFF, 0x6A, 0x71, 0x84),
            HubFill = Solid(0xFF, 0xFF, 0xFF, 0xFF),
            Card = Solid(0xFF, 0xFF, 0xFF, 0xFF),
            InputBackground = Solid(0xFF, 0xF4, 0xF5, 0xF9),
            Error = Solid(0xFF, 0xC4, 0x31, 0x4B),
            ShadowOpacity = 0.25,
        };

    static Brush Solid(byte a, byte r, byte g, byte b) => Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
