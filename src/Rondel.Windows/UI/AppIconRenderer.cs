using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rondel.Windows.Interop;

namespace Rondel.Windows.UI;

/// <summary>Draws the app icon (four ring segments around a dot) at any pixel size.</summary>
internal static class AppIconRenderer
{
    static readonly Color Active = Color.FromRgb(0xD9, 0x77, 0x57);
    static readonly Color Paused = Color.FromRgb(0x8C, 0x8C, 0x8C);

    public static BitmapSource Render(int px, bool paused = false)
    {
        var brush = new SolidColorBrush(paused ? Paused : Active);
        brush.Freeze();
        double c = px / 2.0;
        double thickness = Math.Max(1.6, px * 0.16);
        double radius = px * 0.47 - thickness / 2;
        var pen = new Pen(brush, thickness);
        pen.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            for (int i = 0; i < 4; i++)
            {
                double start = i * 90 - 45 + 13;
                double end = start + 64;
                var figure = new PathFigure { StartPoint = OnCircle(c, radius, start) };
                figure.Segments.Add(new ArcSegment(OnCircle(c, radius, end), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
                dc.DrawGeometry(null, pen, new PathGeometry([figure]));
            }
            dc.DrawEllipse(brush, null, new Point(c, c), px * 0.15, px * 0.15);
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
