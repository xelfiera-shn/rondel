using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Rondel.Core.Themes;

namespace Rondel.MacOS.UI;

/// <summary>
/// The colors of Rondel's own windows (Settings, the update window) in dark and light mode; the standard controls
/// take theirs from the Fluent theme.
/// </summary>
internal sealed record PageLook(IBrush Background, IBrush Card, IBrush CardStroke, IBrush Text, IBrush SubText, IBrush Accent, IBrush Idle, IBrush Hover)
{
    public static PageLook Dark { get; } = new(Solid(0xFF1E1E1E), Solid(0xFF2A2A2A), Solid(0xFF3A3A3A), Solid(0xFFF2F2F2), Solid(0xFFA3A3A3),
        Solid(0xFF2DD4BF), Solid(0xFF333333), Solid(0xFF5A5A5A));

    public static PageLook Light { get; } = new(Solid(0xFFECECEC), Solid(0xFFFFFFFF), Solid(0xFFDADADA), Solid(0xFF1D1D1F), Solid(0xFF6B6B70),
        Solid(0xFF0F766E), Solid(0xFFF2F2F2), Solid(0xFFB8B8B8));

    /// <summary>Rondel's brand gradient, for its mark and the line under a window's header.</summary>
    public IBrush Brand => MenuPalette.For(new AppearanceOptions(), this == Dark).Brand;

    /// <summary>Rondel's mark in the brand gradient, 36 points.</summary>
    public static Control Logo() => new AppIcon.Mark { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };

    static IBrush Solid(uint argb) =>
        new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)).ToImmutable();
}
