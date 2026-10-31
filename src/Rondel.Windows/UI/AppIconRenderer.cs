using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rondel.Windows.Interop;

namespace Rondel.Windows.UI;

/// <summary>
/// Draws the app icon at any pixel size: four ring segments in the brand gradient around a hub, the
/// top one lifted outward like a selected slice. Paused draws the same shape in gray.
/// </summary>
internal static class AppIconRenderer
{
    static readonly Color BrandFrom = Color.FromRgb(0x2D, 0xD4, 0xBF);
    static readonly Color BrandTo = Color.FromRgb(0x8B, 0x5C, 0xF6);
    static readonly Color Paused = Color.FromRgb(0x8C, 0x8C, 0x8C);

    public static BitmapSource Render(int px, bool paused = false)
    {
        Brush brush = paused
            ? new SolidColorBrush(Paused)
            : new LinearGradientBrush(BrandFrom, BrandTo, new Point(0, 0), new Point(px, px)) { MappingMode = BrushMappingMode.Absolute }; // one gradient across the whole icon
        brush.Freeze();
        double c = px / 2.0;
        double thickness = Math.Max(1.8, px * 0.16);
        double radius = px * 0.40 - thickness / 2;
        double lift = Math.Max(0.8, px * 0.06);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            for (int i = 0; i < 4; i++)
            {
                double mid = i * 90;
                double r = i == 0 ? radius + lift : radius;
                var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
                var figure = new PathFigure { StartPoint = OnCircle(c, r, mid - 27) };
                figure.Segments.Add(new ArcSegment(OnCircle(c, r, mid + 27), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                dc.PushOpacity(i == 0 ? 1 : 0.78);
                dc.DrawGeometry(null, pen, new PathGeometry([figure]));
                dc.Pop();
            }
            dc.DrawEllipse(brush, null, new Point(c, c), px * 0.11, px * 0.11);
        }

        var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>HICON for the notification area. The caller owns it (DestroyIcon).</summary>
    public static unsafe nint CreateIconHandle(int px, bool paused)
    {
        var source = new FormatConvertedBitmap(Render(px, paused), PixelFormats.Bgra32, null, 0);
        int stride = px * 4;
        byte[] pixels = new byte[stride * px];
        source.CopyPixels(pixels, stride, 0);

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = px,
            biHeight = -px, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        void* bits;
        nint color = Native.CreateDIBSection(0, &header, 0, &bits, 0, 0);
        if (color == 0)
            return 0;
        Marshal.Copy(pixels, 0, (nint)bits, pixels.Length);

        byte[] maskBits = new byte[(px + 15) / 16 * 2 * px];
        nint mask;
        fixed (byte* m = maskBits)
            mask = Native.CreateBitmap(px, px, 1, 1, m);

        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        nint icon = Native.CreateIconIndirect(&info);
        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        return icon;
    }

    static Point OnCircle(double c, double r, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(c + r * Math.Sin(rad), c - r * Math.Cos(rad));
    }
}
