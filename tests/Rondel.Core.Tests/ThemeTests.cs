using Rondel.Core.Themes;

namespace Rondel.Core.Tests;

public class ThemeTests
{
    [Fact]
    public void AnUnknownThemeFallsBackToRondelAndTheUsersAccentApplies()
    {
        var appearance = new AppearanceOptions { Theme = "from-a-newer-version", Mode = ColorMode.Light, AccentFrom = "#F97316", AccentTo = "ec4899" };
        appearance.Normalize();

        var palette = appearance.ResolvePalette(systemDark: true);

        Assert.Same(BuiltInThemes.Rondel, appearance.ResolveTheme());
        Assert.Equal(BuiltInThemes.Rondel.Light.Text, palette.Text); // the mode wins over Windows
        Assert.Equal(0xFFF97316u, palette.AccentFrom);
        Assert.Equal(0xFFEC4899u, palette.AccentTo);
        Assert.Equal("#EC4899", appearance.AccentTo);
    }

    [Fact]
    public void ASingleAccentColorIsUsedAtBothEnds()
    {
        var appearance = new AppearanceOptions { Theme = "orbit", AccentFrom = "#3B82F6", AccentTo = "not a color" };
        appearance.Normalize();

        var palette = appearance.ResolvePalette(systemDark: true);

        Assert.Equal("", appearance.AccentTo);
        Assert.Equal(0xFF3B82F6u, palette.AccentFrom);
        Assert.Equal(0xFF3B82F6u, palette.AccentTo);
        Assert.Equal(BuiltInThemes.Orbit.Dark.RingFill, palette.RingFill);
    }

    [Fact]
    public void EveryThemesBaseCatchesTheMouse()
    {
        // A fully transparent base would let clicks fall through the menu's layered window.
        Assert.All(BuiltInThemes.All, theme =>
        {
            Assert.True(theme.Dark.RingFill >> 24 > 0, theme.Id);
            Assert.True(theme.Light.RingFill >> 24 > 0, theme.Id);
        });
    }

    [Theory]
    [InlineData(0xFFFF0000u, 0, 1, 1)]
    [InlineData(0xFF00FF00u, 120, 1, 1)]
    [InlineData(0xFF0000FFu, 240, 1, 1)]
    [InlineData(0xFF808080u, 0, 0, 0.502)]
    public void HsvSeesColorsAsAPickerDoes(uint argb, double hue, double saturation, double value)
    {
        var hsv = HsvColor.FromArgb(argb);

        Assert.Equal(hue, hsv.Hue, 3);
        Assert.Equal(saturation, hsv.Saturation, 3);
        Assert.Equal(value, hsv.Value, 3);
    }

    [Fact]
    public void EveryColorComesBackFromHsvUnchanged()
    {
        uint[] colors = [0xFF7C3AED, 0xFF22D3EE, 0xFF14B8A6, 0xFFF59E0B, 0xFFE11D48, 0xFF000000, 0xFFFFFFFF, 0xFF6B7280];
        Assert.All(colors, argb => Assert.Equal(argb, HsvColor.FromArgb(argb).ToArgb()));
        Assert.Equal(0xFFFF0000u, new HsvColor(360, 1, 1).ToArgb()); // the end of the hue strip is red again
    }
}
