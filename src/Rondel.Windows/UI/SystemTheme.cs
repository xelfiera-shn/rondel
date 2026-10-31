using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using Rondel.Core.Themes;

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
/// A menu theme turned into brushes for one mode (see <see cref="MenuTheme"/>). The accent pair marks whatever is
/// active: the lit edge of the hovered tile, the hub ring and the arrow of a tile that expands.
/// </summary>
internal sealed class RondelPalette
{
    public required MenuTheme Theme { get; init; }
    public required Color BrandFrom { get; init; }
    public required Color BrandTo { get; init; }
    public required Brush RingFill { get; init; }
    public required Brush RingStroke { get; init; }
    public required Brush TileFill { get; init; }
    public required Brush TileStroke { get; init; }
    public required Brush HotFill { get; init; }
    public required Brush RimIdle { get; init; }
    public required Color WashInner { get; init; }
    public required Color WashOuter { get; init; }
    public required Brush Text { get; init; }
    public required Brush SubText { get; init; }
    public required Brush HubFill { get; init; }
    public required Brush HubStroke { get; init; }
    public required Brush Card { get; init; }
    public required Brush InputBackground { get; init; }
    public required Brush Error { get; init; }
    public required double ShadowOpacity { get; init; }

    /// <summary>Text on an accent-filled tile.</summary>
    public static Brush OnAccent { get; } = Freeze(new SolidColorBrush(Colors.White));

    public static Brush OnAccentSoft { get; } = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));

    public Brush Accent => Freeze(new SolidColorBrush(BrandFrom));

    /// <summary>Brand gradient across an element's own bounds (hub ring, card border, icons).</summary>
    public Brush Brand => Freeze(new LinearGradientBrush(BrandFrom, BrandTo, new Point(0, 0), new Point(1, 1)));

    /// <summary>Nearly transparent fill: invisible, but makes an area receive the mouse in the layered window.</summary>
    public static Brush Catcher { get; } = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    /// <summary>Soft accent-colored glow around lit elements.</summary>
    public Effect Glow => Freeze(new DropShadowEffect { Color = BrandFrom, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.9 });

    /// <summary>Brand gradient between two points in canvas coordinates (e.g. along a slice's rim).</summary>
    public Brush BrandBetween(Point from, Point to) =>
        Freeze(new LinearGradientBrush(BrandFrom, BrandTo, from, to) { MappingMode = BrushMappingMode.Absolute });

    /// <summary>Hover wash that deepens from the inner to the outer edge of a slice.</summary>
    public Brush WashBetween(Point inner, Point outer) =>
        Freeze(new LinearGradientBrush(WashInner, WashOuter, inner, outer) { MappingMode = BrushMappingMode.Absolute });

    /// <summary>The accent gradient from the inner to the outer edge of a tile, at the wash's strength (a filled highlight).</summary>
    public Brush AccentFillBetween(Point inner, Point outer) =>
        Freeze(new LinearGradientBrush(WithAlpha(BrandFrom, WashOuter.A), WithAlpha(BrandTo, WashOuter.A), inner, outer) { MappingMode = BrushMappingMode.Absolute });

    /// <summary>A soft accent glow around a point, fading out at <paramref name="radius"/>.</summary>
    public Brush GlowAround(Point center, double radius, double strength) =>
        Freeze(new RadialGradientBrush(WithAlpha(BrandFrom, (byte)(WashOuter.A * strength)), WithAlpha(BrandFrom, 0))
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = center,
            GradientOrigin = center,
            RadiusX = radius,
            RadiusY = radius,
        });

    /// <summary>Rondel's own theme in the Windows app mode (the settings window's brand).</summary>
    public static RondelPalette For(ThemeColors theme) => For(BuiltInThemes.Rondel, BuiltInThemes.Rondel.For(theme.IsDark));

    /// <summary>The menu's palette for the settings' appearance and the Windows app mode.</summary>
    public static RondelPalette For(AppearanceOptions appearance, ThemeColors system) =>
        For(appearance.ResolveTheme(), appearance.ResolvePalette(system.IsDark));

    public static RondelPalette For(MenuTheme theme, ThemePalette p)
    {
        Color brandFrom = ToColor(p.AccentFrom), brandTo = ToColor(p.AccentTo);
        Brush tileFill = p.TileFillEnd is { } end
            ? Freeze(new LinearGradientBrush(ToColor(p.TileFill), ToColor(end), new Point(0, 0), new Point(0, 1)))
            : Solid(p.TileFill);
        return new RondelPalette
        {
            Theme = theme,
            BrandFrom = brandFrom,
            BrandTo = brandTo,
            RingFill = Solid(p.RingFill),
            RingStroke = Solid(p.RingStroke),
            TileFill = tileFill,
            TileStroke = Solid(p.TileStroke),
            HotFill = p.HotFill == 0 ? tileFill : Solid(p.HotFill),
            RimIdle = Solid(p.RimIdle),
            WashInner = WithAlpha(brandFrom, 0),
            WashOuter = WithAlpha(brandFrom, p.WashAlpha),
            Text = Solid(p.Text),
            SubText = Solid(p.SubText),
            HubFill = Solid(p.HubFill),
            HubStroke = p.HubStroke is { } hub ? Solid(hub) : Freeze(new LinearGradientBrush(brandFrom, brandTo, new Point(0, 0), new Point(1, 1))),
            Card = Solid(p.Card),
            InputBackground = Solid(p.InputBackground),
            Error = Solid(p.Error),
            ShadowOpacity = p.ShadowOpacity,
        };
    }

    static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    static Brush Solid(uint argb) => Freeze(new SolidColorBrush(ToColor(argb)));

    static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
