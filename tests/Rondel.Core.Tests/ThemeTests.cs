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
}
