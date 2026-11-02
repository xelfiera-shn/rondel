namespace Rondel.Core.Themes;

/// <summary>
/// A color as hue (0–360°), saturation and value (0–1): what a color picker's area (saturation across, value up)
/// and hue strip show.
/// </summary>
public readonly record struct HsvColor(double Hue, double Saturation, double Value)
{
    /// <summary>The color 0xAARRGGBB (alpha ignored); grays get hue 0.</summary>
    public static HsvColor FromArgb(uint argb)
    {
        double r = ((argb >> 16) & 0xFF) / 255.0, g = ((argb >> 8) & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double hue = delta == 0 ? 0
            : max == r ? 60 * ((g - b) / delta)
            : max == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);
        return new HsvColor(hue < 0 ? hue + 360 : hue, max == 0 ? 0 : delta / max, max);
    }

    /// <summary>The opaque color as 0xAARRGGBB.</summary>
    public uint ToArgb()
    {
        double sector = ((Hue % 360) + 360) % 360 / 60;
        double s = Math.Clamp(Saturation, 0, 1), v = Math.Clamp(Value, 0, 1);
        double chroma = v * s, x = chroma * (1 - Math.Abs(sector % 2 - 1)), m = v - chroma;
        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return 0xFF000000 | Channel(r + m) << 16 | Channel(g + m) << 8 | Channel(b + m);
    }

    static uint Channel(double value) => (uint)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
