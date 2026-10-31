namespace Rondel.Core.Themes;

/// <summary>The themes Rondel ships with. Every one has a dark and a light palette; the accent can be replaced.</summary>
public static class BuiltInThemes
{
    public const string RondelId = "rondel";

    /// <summary>Rondel's own look: ink tiles, a teal → violet accent on a lit rim.</summary>
    public static MenuTheme Rondel { get; } = new()
    {
        Id = RondelId,
        Name = "@Theme.Rondel",
        Dark = new()
        {
            AccentFrom = 0xFF2DD4BF, AccentTo = 0xFFA78BFA,
            RingFill = 0xF0101219, RingStroke = 0x1AFFFFFF,
            TileFill = 0xFF181B26, TileStroke = 0x10FFFFFF, RimIdle = 0x16FFFFFF, WashAlpha = 0x40,
            Text = 0xFFEEF0F7, SubText = 0xFF8B93A7, HubFill = 0xFF1D2130,
            Card = 0xFF161922, InputBackground = 0xFF0F1118, Error = 0xFFFF8A9B, ShadowOpacity = 0.55,
        },
        Light = new()
        {
            AccentFrom = 0xFF0D9488, AccentTo = 0xFF7C3AED,
            RingFill = 0xF5ECEEF5, RingStroke = 0x14000000,
            TileFill = 0xFFFFFFFF, TileStroke = 0x120B1020, RimIdle = 0x140B1020, WashAlpha = 0x2E,
            Text = 0xFF161A26, SubText = 0xFF6A7184, HubFill = 0xFFFFFFFF,
            Card = 0xFFFFFFFF, InputBackground = 0xFFF4F5F9, Error = 0xFFC4314B, ShadowOpacity = 0.25,
        },
    };

    /// <summary>Thin lines on a dark disc; the lit edge is a silver glint.</summary>
    public static MenuTheme Line { get; } = new()
    {
        Id = "line",
        Name = "@Theme.Line",
        TileGap = 0,
        RimWidth = 2,
        HubStrokeWidth = 1.2,
        Dark = new()
        {
            AccentFrom = 0xFFF1F5F9, AccentTo = 0xFF94A3B8,
            RingFill = 0xEB0D0F14, RingStroke = 0x40FFFFFF,
            TileFill = 0x00000000, TileStroke = 0x26FFFFFF, RimIdle = 0x00FFFFFF, WashAlpha = 0x1E,
            Text = 0xFFE5E7EB, SubText = 0xFF8A919E, HubFill = 0xFF101218, HubStroke = 0x40FFFFFF,
            Card = 0xFF14161C, InputBackground = 0xFF0D0F14, Error = 0xFFFF8A9B, ShadowOpacity = 0.5,
        },
        Light = new()
        {
            AccentFrom = 0xFF1E293B, AccentTo = 0xFF64748B,
            RingFill = 0xF2F8FAFC, RingStroke = 0x33000000,
            TileFill = 0x00FFFFFF, TileStroke = 0x1F0F172A, RimIdle = 0x000F172A, WashAlpha = 0x14,
            Text = 0xFF0F172A, SubText = 0xFF64748B, HubFill = 0xFFFFFFFF, HubStroke = 0x33000000,
            Card = 0xFFFFFFFF, InputBackground = 0xFFF1F5F9, Error = 0xFFC4314B, ShadowOpacity = 0.22,
        },
    };

    /// <summary>Grey tiles with gaps; the highlighted one fills with the accent and glows.</summary>
    public static MenuTheme Spotlight { get; } = new()
    {
        Id = "spotlight",
        Name = "@Theme.Spotlight",
        Highlight = HighlightStyle.Fill,
        TileGap = 6,
        HubStrokeWidth = 1.2,
        Dark = new()
        {
            AccentFrom = 0xFF2563EB, AccentTo = 0xFF60A5FA,
            RingFill = 0x66090B10, RingStroke = 0x00FFFFFF,
            TileFill = 0xF2292D37, TileFillEnd = 0xF21C1F27, TileStroke = 0x1FFFFFFF, RimIdle = 0x00FFFFFF, WashAlpha = 0xE6,
            Text = 0xFFF1F5F9, SubText = 0xFF9CA3AF, HubFill = 0xFF14161C, HubStroke = 0x26FFFFFF,
            Card = 0xFF17191F, InputBackground = 0xFF0F1115, Error = 0xFFFF8A9B, ShadowOpacity = 0.55,
        },
        Light = new()
        {
            AccentFrom = 0xFF2563EB, AccentTo = 0xFF3B82F6,
            RingFill = 0x99E2E8F0, RingStroke = 0x00000000,
            TileFill = 0xF5FFFFFF, TileFillEnd = 0xF5EEF2F7, TileStroke = 0x1F0F172A, RimIdle = 0x000F172A, WashAlpha = 0xF2,
            Text = 0xFF0F172A, SubText = 0xFF64748B, HubFill = 0xFFFFFFFF, HubStroke = 0x1F0F172A,
            Card = 0xFFFFFFFF, InputBackground = 0xFFF1F5F9, Error = 0xFFC4314B, ShadowOpacity = 0.25,
        },
    };

    /// <summary>No tiles: icons float inside an accent orbit with ticks, around a glowing hub.</summary>
    public static MenuTheme Orbit { get; } = new()
    {
        Id = "orbit",
        Name = "@Theme.Orbit",
        Tiles = TileShape.Hidden,
        Highlight = HighlightStyle.Glow,
        Decoration = RingDecoration.Orbit,
        TileGap = 0,
        HubStrokeWidth = 2,
        HubGlow = true,
        Dark = new()
        {
            AccentFrom = 0xFFA78BFA, AccentTo = 0xFF6366F1,
            RingFill = 0xD90A0B12, RingStroke = 0x00FFFFFF,
            TileFill = 0x00000000, TileStroke = 0x00000000, RimIdle = 0x00000000, WashAlpha = 0x48,
            Text = 0xFFE9E7F5, SubText = 0xFF8E8AA8, HubFill = 0xFF0F0E18,
            Card = 0xFF13121C, InputBackground = 0xFF0C0B12, Error = 0xFFFF8A9B, ShadowOpacity = 0.5,
        },
        Light = new()
        {
            AccentFrom = 0xFF7C3AED, AccentTo = 0xFF4F46E5,
            RingFill = 0xE6FAF8FF, RingStroke = 0x00000000,
            TileFill = 0x00000000, TileStroke = 0x00000000, RimIdle = 0x00000000, WashAlpha = 0x2A,
            Text = 0xFF1E1B2E, SubText = 0xFF6B6785, HubFill = 0xFFFFFFFF,
            Card = 0xFFFFFFFF, InputBackground = 0xFFF5F3FF, Error = 0xFFC4314B, ShadowOpacity = 0.22,
        },
    };

    /// <summary>Rounded glass petals with wide gaps; the highlighted one brightens and its outline lights up.</summary>
    public static MenuTheme Petal { get; } = new()
    {
        Id = "petal",
        Name = "@Theme.Petal",
        Tiles = TileShape.Rounded,
        Highlight = HighlightStyle.Outline,
        TileGap = 8,
        CornerRadius = 12,
        HubStrokeWidth = 1.2,
        Dark = new()
        {
            AccentFrom = 0xFF7DD3FC, AccentTo = 0xFFC4B5FD,
            RingFill = 0x4D07080C, RingStroke = 0x00FFFFFF,
            TileFill = 0xE6343843, TileFillEnd = 0xE61F222A, TileStroke = 0x33FFFFFF, HotFill = 0xF0434856, RimIdle = 0x00FFFFFF,
            WashAlpha = 0x30,
            Text = 0xFFF1F3F8, SubText = 0xFF9AA1B2, HubFill = 0xF0262931, HubStroke = 0x33FFFFFF,
            Card = 0xFF1A1C23, InputBackground = 0xFF111318, Error = 0xFFFF8A9B, ShadowOpacity = 0.5,
        },
        Light = new()
        {
            AccentFrom = 0xFF0284C7, AccentTo = 0xFF7C3AED,
            RingFill = 0x59CBD5E1, RingStroke = 0x00000000,
            TileFill = 0xF2FFFFFF, TileFillEnd = 0xF2EEF1F6, TileStroke = 0x260F172A, HotFill = 0xFFFFFFFF, RimIdle = 0x000F172A,
            WashAlpha = 0x22,
            Text = 0xFF0F172A, SubText = 0xFF64748B, HubFill = 0xF2FFFFFF, HubStroke = 0x260F172A,
            Card = 0xFFFFFFFF, InputBackground = 0xFFF1F5F9, Error = 0xFFC4314B, ShadowOpacity = 0.25,
        },
    };

    /// <summary>In the order they are offered in Settings.</summary>
    public static IReadOnlyList<MenuTheme> All { get; } = [Rondel, Line, Spotlight, Orbit, Petal];

    /// <summary>Accent pairs offered in Settings next to the theme's own and a custom one.</summary>
    public static IReadOnlyList<AccentPreset> Accents { get; } =
    [
        new("rondel", "@Accent.Rondel", 0xFF2DD4BF, 0xFFA78BFA),
        new("sunset", "@Accent.Sunset", 0xFFF97316, 0xFFEC4899),
        new("ocean", "@Accent.Ocean", 0xFF22D3EE, 0xFF3B82F6),
        new("forest", "@Accent.Forest", 0xFF10B981, 0xFF84CC16),
        new("lavender", "@Accent.Lavender", 0xFFA78BFA, 0xFFF0ABFC),
        new("silver", "@Accent.Silver", 0xFFE2E8F0, 0xFF94A3B8),
    ];

    /// <summary>The theme with this id, or Rondel's when there is none (an id from a newer version, a typo).</summary>
    public static MenuTheme Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Rondel;
}
