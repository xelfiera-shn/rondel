namespace Rondel.Core.Themes;

/// <summary>How the tiles of the ring and of the arcs are drawn.</summary>
public enum TileShape
{
    /// <summary>Ring sectors with straight edges.</summary>
    Sector,

    /// <summary>Sectors with rounded corners, like petals.</summary>
    Rounded,

    /// <summary>No tile is drawn: icons and labels float over the ring's base, which still catches the mouse.</summary>
    Hidden,
}

/// <summary>How the tile under the pointer, and the one whose items are open, stands out.</summary>
public enum HighlightStyle
{
    /// <summary>A wash that deepens towards the outer edge, and a lit strip along that edge.</summary>
    Rim,

    /// <summary>The tile fills with the accent and glows.</summary>
    Fill,

    /// <summary>The tile brightens and its outline lights up in the accent.</summary>
    Outline,

    /// <summary>A soft accent glow behind the icon and the label.</summary>
    Glow,
}

/// <summary>Extra lines drawn with the ring.</summary>
public enum RingDecoration
{
    None,

    /// <summary>A thin accent ring along the edge, broken at every slice by a short tick.</summary>
    Orbit,
}

/// <summary>
/// The colors of a theme in one mode (dark or light), as 0xAARRGGBB. Platform renderers turn them into brushes;
/// the accent pair can be replaced by the user's own.
/// </summary>
public sealed record ThemePalette
{
    /// <summary>Start and end of the accent gradient: lit edges, the hub ring, arrows, focus.</summary>
    public required uint AccentFrom { get; init; }

    public required uint AccentTo { get; init; }

    /// <summary>The base under the tiles; it must not be fully transparent, or the mouse would fall through.</summary>
    public required uint RingFill { get; init; }

    public required uint RingStroke { get; init; }

    public required uint TileFill { get; init; }

    /// <summary>Bottom of a vertical gradient over <see cref="TileFill"/> (glass), or null for a flat fill.</summary>
    public uint? TileFillEnd { get; init; }

    public required uint TileStroke { get; init; }

    /// <summary>The fill of a highlighted tile for <see cref="HighlightStyle.Outline"/>.</summary>
    public uint HotFill { get; init; }

    /// <summary>The strip along a tile's outer edge while it is not lit.</summary>
    public required uint RimIdle { get; init; }

    /// <summary>Opacity (0–255) of the accent in a highlighted tile's wash or glow.</summary>
    public required byte WashAlpha { get; init; }

    public required uint Text { get; init; }

    public required uint SubText { get; init; }

    public required uint HubFill { get; init; }

    /// <summary>The hub's ring, or null for the accent gradient.</summary>
    public uint? HubStroke { get; init; }

    public required uint Card { get; init; }

    public required uint InputBackground { get; init; }

    public required uint Error { get; init; }

    public required double ShadowOpacity { get; init; }
}

/// <summary>A ready-made accent pair; Settings stores its colors, not its id, so a pair can be edited afterwards.</summary>
public sealed record AccentPreset(string Id, string Name, uint From, uint To);

/// <summary>
/// A look for the radial menu: tile shape, highlight, decoration and a palette for dark and light mode.
/// Geometry (radii, arcs) is the same for every theme, so a theme never changes where things are.
/// </summary>
public sealed record MenuTheme
{
    public required string Id { get; init; }

    /// <summary>Localized name ("@Key") shown in Settings.</summary>
    public required string Name { get; init; }

    public TileShape Tiles { get; init; } = TileShape.Sector;

    public HighlightStyle Highlight { get; init; } = HighlightStyle.Rim;

    public RingDecoration Decoration { get; init; } = RingDecoration.None;

    /// <summary>DIPs between neighbouring tiles.</summary>
    public double TileGap { get; init; } = 3;

    /// <summary>Corner radius for <see cref="TileShape.Rounded"/>.</summary>
    public double CornerRadius { get; init; }

    /// <summary>Width of the lit strip for <see cref="HighlightStyle.Rim"/>.</summary>
    public double RimWidth { get; init; } = 4;

    /// <summary>Thickness of the hub's ring.</summary>
    public double HubStrokeWidth { get; init; } = 1.6;

    /// <summary>The hub's ring glows in the accent.</summary>
    public bool HubGlow { get; init; }

    public required ThemePalette Dark { get; init; }

    public required ThemePalette Light { get; init; }

    public ThemePalette For(bool dark) => dark ? Dark : Light;
}
