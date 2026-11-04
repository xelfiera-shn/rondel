using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Rondel.MacOS.UI;

/// <summary>
/// Rondel's mark, the same as the Windows app icon: four ring segments around a hub, the top one lifted outward like
/// a selected slice. One drawing for the menu bar icon, the windows' headers and the app icon.
/// </summary>
internal static class AppIcon
{
    /// <summary>The brand gradient's ends, as on Windows.</summary>
    public static readonly Color BrandFrom = Color.FromRgb(0x2D, 0xD4, 0xBF), BrandTo = Color.FromRgb(0x8B, 0x5C, 0xF6);

    /// <summary>The other segments a little lighter, so the lifted one reads as the selection.</summary>
    const double OtherOpacity = 0.78;

    /// <summary>Draws the mark into the square of <paramref name="size"/> at <paramref name="origin"/>.</summary>
    public static void DrawMark(DrawingContext context, Point origin, double size, IBrush ink)
    {
        double thickness = size * 0.16, radius = size * 0.40 - thickness / 2, lift = size * 0.06;
        var center = new Point(origin.X + size / 2, origin.Y + size / 2);
        var pen = new Pen(ink, thickness, lineCap: PenLineCap.Round);
        for (int i = 0; i < 4; i++)
        {
            double mid = i * 90, r = i == 0 ? radius + lift : radius;
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(OnCircle(center, r, mid - 27), false);
                g.ArcTo(OnCircle(center, r, mid + 27), new Size(r, r), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            using (context.PushOpacity(i == 0 ? 1 : OtherOpacity))
                context.DrawGeometry(null, pen, arc);
        }
        context.DrawEllipse(ink, null, center, size * 0.11, size * 0.11);
    }

    /// <summary>The brand gradient across <paramref name="area"/>: one gradient over the whole mark, not one per segment.</summary>
    public static IBrush Gradient(Rect area) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(area.TopLeft, RelativeUnit.Absolute),
        EndPoint = new RelativePoint(area.BottomRight, RelativeUnit.Absolute),
        GradientStops = { new GradientStop(BrandFrom, 0), new GradientStop(BrandTo, 1) },
    };

    /// <summary>
    /// The app icon at <paramref name="pixels"/> square, on Apple's grid: a rounded square (824 of 1024, with a
    /// continuous-looking corner) in dark slate, and the mark in the brand gradient.
    /// </summary>
    public static RenderTargetBitmap RenderAppIcon(int pixels)
    {
        double scale = pixels / 1024.0;
        var bitmap = new RenderTargetBitmap(new PixelSize(pixels, pixels), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var body = new Rect(100 * scale, 100 * scale, 824 * scale, 824 * scale);
            var backdrop = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(0x24, 0x28, 0x34), 0), new GradientStop(Color.FromRgb(0x10, 0x12, 0x18), 1) },
            };
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)), Math.Max(1, 2 * scale));
            context.DrawRectangle(backdrop, edge, body, 185 * scale, 185 * scale);
            double mark = 540 * scale;
            var at = new Point((pixels - mark) / 2, (pixels - mark) / 2);
            DrawMark(context, at, mark, Gradient(new Rect(at, new Size(mark, mark))));
        }
        return bitmap;
    }

    static Point OnCircle(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Sin(rad), center.Y - radius * Math.Cos(rad));
    }

    /// <summary>The mark in the brand gradient, for a window's header.</summary>
    public sealed class Mark : Control
    {
        public override void Render(DrawingContext context) =>
            DrawMark(context, default, Math.Min(Bounds.Width, Bounds.Height), Gradient(new Rect(Bounds.Size)));
    }
}
