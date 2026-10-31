using System.Globalization;

namespace Rondel.Core.Themes;

public enum ColorMode { Auto, Dark, Light }

/// <summary>How the menu looks: a theme, dark or light, and optionally the user's own accent colors.</summary>
public sealed class AppearanceOptions
{
    /// <summary>Id of a built-in theme (<see cref="BuiltInThemes"/>); an unknown one falls back to Rondel's.</summary>
    public string Theme { get; set; } = BuiltInThemes.RondelId;

    /// <summary>Auto follows the Windows app mode.</summary>
    public ColorMode Mode { get; set; } = ColorMode.Auto;

    /// <summary>Start of the accent gradient as #RRGGBB, replacing the theme's; empty keeps the theme's.</summary>
    public string AccentFrom { get; set; } = "";

    /// <summary>End of the accent gradient as #RRGGBB; empty uses <see cref="AccentFrom"/> (a single color) or the theme's.</summary>
    public string AccentTo { get; set; } = "";

    public MenuTheme ResolveTheme() => BuiltInThemes.Find(Theme);

    public bool IsDark(bool systemDark) => Mode switch
    {
        ColorMode.Dark => true,
        ColorMode.Light => false,
        _ => systemDark,
    };

    /// <summary>The theme's palette for the mode, with the user's accent when one is set.</summary>
    public ThemePalette ResolvePalette(bool systemDark)
    {
        var palette = ResolveTheme().For(IsDark(systemDark));
        uint? from = ParseColor(AccentFrom), to = ParseColor(AccentTo);
        return from is null && to is null ? palette : palette with { AccentFrom = from ?? to!.Value, AccentTo = to ?? from!.Value };
    }

    public void Normalize()
    {
        Theme = string.IsNullOrWhiteSpace(Theme) ? BuiltInThemes.RondelId : Theme.Trim();
        AccentFrom = ParseColor(AccentFrom) is { } from ? FormatColor(from) : "";
        AccentTo = ParseColor(AccentTo) is { } to ? FormatColor(to) : "";
    }

    /// <summary>"#RRGGBB" or "RRGGBB" as an opaque 0xAARRGGBB, or null when it is not a color.</summary>
    public static uint? ParseColor(string? text)
    {
        string hex = (text ?? "").Trim().TrimStart('#');
        return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)
            ? 0xFF000000 | rgb
            : null;
    }

    public static string FormatColor(uint argb) => $"#{argb & 0xFFFFFF:X6}";
}
