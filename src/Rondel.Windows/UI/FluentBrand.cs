using System.Windows;
using System.Windows.Media;

namespace Rondel.Windows.UI;

/// <summary>
/// Rondel's teal instead of the Windows accent in the Fluent-styled windows (Settings, the update window): for
/// checked boxes, sliders, selections, focus and accent buttons, by overriding the Fluent theme's accent brushes in
/// the window's resources.
/// </summary>
internal static class FluentBrand
{
    /// <summary>Teal from light to dark; the Fluent theme derives its accent brushes from these shades.</summary>
    static readonly Color[] Teal =
    [
        Color.FromRgb(0x99, 0xF6, 0xE4), Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0D, 0x94, 0x88),
        Color.FromRgb(0x0F, 0x76, 0x6E), Color.FromRgb(0x11, 0x5E, 0x59), Color.FromRgb(0x13, 0x4E, 0x4A),
    ];

    public static void Apply(FrameworkElement window, ThemeColors theme)
    {
        var resources = window.Resources;
        string[] shades = ["SystemAccentColorLight3", "SystemAccentColorLight2", "SystemAccentColorLight1", "SystemAccentColor",
            "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];
        for (int i = 0; i < shades.Length; i++)
            resources[shades[i]] = Teal[i];
        // Controls such as the slider use the SystemColors accent keys directly.
        (ResourceKey Color, ResourceKey Brush)[] systemKeys =
        [
            (SystemColors.AccentColorLight3Key, SystemColors.AccentColorLight3BrushKey),
            (SystemColors.AccentColorLight2Key, SystemColors.AccentColorLight2BrushKey),
            (SystemColors.AccentColorLight1Key, SystemColors.AccentColorLight1BrushKey),
            (SystemColors.AccentColorKey, SystemColors.AccentColorBrushKey),
            (SystemColors.AccentColorDark1Key, SystemColors.AccentColorDark1BrushKey),
            (SystemColors.AccentColorDark2Key, SystemColors.AccentColorDark2BrushKey),
            (SystemColors.AccentColorDark3Key, SystemColors.AccentColorDark3BrushKey),
        ];
        for (int i = 0; i < systemKeys.Length; i++)
        {
            resources[systemKeys[i].Color] = Teal[i];
            resources[systemKeys[i].Brush] = Solid(Teal[i], 1);
        }
        var accent = theme.IsDark ? Teal[1] : Teal[4];
        resources["AccentFillColorPrimaryBrush"] = Solid(accent, 1);
        resources["AccentFillColorDefaultBrush"] = Solid(accent, 1);
        resources["AccentFillColorSecondaryBrush"] = Solid(accent, 0.9);
        resources["AccentFillColorTertiaryBrush"] = Solid(accent, 0.8);
        resources["AccentTextFillColorPrimaryBrush"] = Solid(accent, 1);
        resources["AccentTextFillColorSecondaryBrush"] = Solid(accent, 1);
        resources["AccentTextFillColorTertiaryBrush"] = Solid(accent, 1);
        // Text on the accent: dark on the bright teal of the dark theme, white on the deep teal of the light theme.
        resources["TextOnAccentFillColorPrimaryBrush"] = Solid(theme.IsDark ? Color.FromRgb(0x0B, 0x10, 0x14) : Colors.White, 1);
    }

    /// <summary>The thin line under a window's header, in the brand gradient.</summary>
    public static Brush Line(ThemeColors theme)
    {
        var palette = RondelPalette.For(theme);
        var line = new LinearGradientBrush(palette.BrandFrom, palette.BrandTo, 0);
        line.Freeze();
        return line;
    }

    static SolidColorBrush Solid(Color color, double opacity)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}
