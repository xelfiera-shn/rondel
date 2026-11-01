using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Rondel.Core.Themes;

namespace Rondel.MacOS.UI;

/// <summary>
/// A menu theme turned into brushes for one mode (see <see cref="MenuTheme"/>), the counterpart of the Windows
/// RondelPalette. The accent pair marks whatever is active: the lit edge of the hovered tile, the hub ring and the
/// arrow of a tile that expands.
/// </summary>
internal sealed class MenuPalette
{
    public required MenuTheme Theme { get; init; }
    public required Color BrandFrom { get; init; }
    public required Color BrandTo { get; init; }
    public required IBrush RingFill { get; init; }
    public required IBrush RingStroke { get; init; }
    public required IBrush TileFill { get; init; }
    public required IBrush TileStroke { get; init; }
    public required IBrush HotFill { get; init; }
    public required IBrush RimIdle { get; init; }
    public required Color WashInner { get; init; }
    public required Color WashOuter { get; init; }
    public required IBrush Text { get; init; }
    public required IBrush SubText { get; init; }
    public required IBrush HubFill { get; init; }
    public required IBrush HubStroke { get; init; }
    public required IBrush Card { get; init; }
    public required IBrush InputBackground { get; init; }
    public required IBrush Error { get; init; }
    public required double ShadowOpacity { get; init; }

    /// <summary>Text on an accent-filled tile.</summary>
    public static IBrush OnAccent { get; } = new ImmutableSolidColorBrush(Colors.White);

    public static IBrush OnAccentSoft { get; } = new ImmutableSolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    public IBrush Accent => new ImmutableSolidColorBrush(BrandFrom);

    /// <summary>Brand gradient across an element's own bounds (hub ring, icons).</summary>
    public IBrush Brand => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(BrandFrom, 0), new GradientStop(BrandTo, 1) },
    };

    /// <summary>Soft accent-colored glow around lit elements.</summary>
    public IEffect Glow => new DropShadowEffect { Color = BrandFrom, BlurRadius = 14, OffsetX = 0, OffsetY = 0, Opacity = 0.9 };

    /// <summary>Brand gradient between two points in canvas coordinates (e.g. along a slice's rim).</summary>
    public IBrush BrandBetween(Point from, Point to) => Between(BrandFrom, BrandTo, from, to);

    /// <summary>Hover wash that deepens from the inner to the outer edge of a slice.</summary>
    public IBrush WashBetween(Point inner, Point outer) => Between(WashInner, WashOuter, inner, outer);

    /// <summary>The accent gradient from the inner to the outer edge of a tile, at the wash's strength (a filled highlight).</summary>
    public IBrush AccentFillBetween(Point inner, Point outer) =>
        Between(WithAlpha(BrandFrom, WashOuter.A), WithAlpha(BrandTo, WashOuter.A), inner, outer);

    /// <summary>A soft accent glow around a point, fading out at <paramref name="radius"/>.</summary>
    public IBrush GlowAround(Point center, double radius, double strength) => new RadialGradientBrush
    {
        Center = new RelativePoint(center, RelativeUnit.Absolute),
        GradientOrigin = new RelativePoint(center, RelativeUnit.Absolute),
        RadiusX = new RelativeScalar(radius, RelativeUnit.Absolute),
        RadiusY = new RelativeScalar(radius, RelativeUnit.Absolute),
        GradientStops =
        {
            new GradientStop(WithAlpha(BrandFrom, (byte)(WashOuter.A * strength)), 0),
            new GradientStop(WithAlpha(BrandFrom, 0), 1),
        },
    };

    /// <summary>The menu's palette for the settings' appearance and the system's dark or light mode.</summary>
    public static MenuPalette For(AppearanceOptions appearance, bool systemDark) =>
        For(appearance.ResolveTheme(), appearance.ResolvePalette(systemDark));

    public static MenuPalette For(MenuTheme theme, ThemePalette p)
    {
        Color brandFrom = ToColor(p.AccentFrom), brandTo = ToColor(p.AccentTo);
        IBrush tileFill = p.TileFillEnd is { } end
            ? Between(ToColor(p.TileFill), ToColor(end), new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative))
            : Solid(p.TileFill);
        return new MenuPalette
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
            HubStroke = p.HubStroke is { } hub
                ? Solid(hub)
                : Between(brandFrom, brandTo, new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative)),
            Card = Solid(p.Card),
            InputBackground = Solid(p.InputBackground),
            Error = Solid(p.Error),
            ShadowOpacity = p.ShadowOpacity,
        };
    }

    static IBrush Between(Color from, Color to, Point start, Point end) =>
        Between(from, to, new RelativePoint(start, RelativeUnit.Absolute), new RelativePoint(end, RelativeUnit.Absolute));

    static IBrush Between(Color from, Color to, RelativePoint start, RelativePoint end) => new LinearGradientBrush
    {
        StartPoint = start,
        EndPoint = end,
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
    };

    static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    static IBrush Solid(uint argb) => new ImmutableSolidColorBrush(ToColor(argb));
}
