using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Rondel.Windows.Interop;

/// <summary>
/// What Ask About a Screenshot needs from Windows: the pixels of every monitor as they are now, where each monitor is,
/// and the windows on screen with the bounds they are drawn in. Everything is in physical pixels of the virtual screen
/// (the process is per-monitor DPI aware).
/// </summary>
internal static unsafe partial class ScreenCapture
{
    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    /// <summary>The whole virtual screen as it looks now, and where it is.</summary>
    /// <exception cref="InvalidOperationException">Windows gave no picture (for example on a locked or secure desktop).</exception>
    public static (BitmapSource Pixels, RECT Bounds) VirtualScreen()
    {
        var bounds = new RECT
        {
            Left = Native.GetSystemMetrics(SM_XVIRTUALSCREEN),
            Top = Native.GetSystemMetrics(SM_YVIRTUALSCREEN),
        };
        bounds.Right = bounds.Left + Native.GetSystemMetrics(SM_CXVIRTUALSCREEN);
        bounds.Bottom = bounds.Top + Native.GetSystemMetrics(SM_CYVIRTUALSCREEN);

        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
        try
        {
            if (screen == 0 || memory == 0 || bitmap == 0)
                throw new InvalidOperationException("No device context or bitmap for the screen");
            nint previous = SelectObject(memory, bitmap);
            // CAPTUREBLT takes layered windows (menus, tooltips, translucent apps) too.
            bool copied = BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.Left, bounds.Top, SRCCOPY | CAPTUREBLT);
            SelectObject(memory, previous);
            if (!copied)
                throw new InvalidOperationException($"BitBlt failed ({Marshal.GetLastPInvokeError()})");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // Opaque whatever Windows says of the alpha channel, and a copy of its own, so the GDI bitmap can go.
            var pixels = new WriteableBitmap(new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr32, null, 0));
            pixels.Freeze();
            return (pixels, bounds);
        }
        finally
        {
            if (bitmap != 0)
                Native.DeleteObject(bitmap);
            if (memory != 0)
                DeleteDC(memory);
            if (screen != 0)
                _ = ReleaseDC(0, screen);
        }
    }

    [ThreadStatic] static List<RECT>? s_monitors;

    /// <summary>The monitors' bounds.</summary>
    public static List<RECT> Monitors()
    {
        s_monitors = [];
        try
        {
            EnumDisplayMonitors(0, null, &OnMonitor, 0);
            return s_monitors;
        }
        finally
        {
            s_monitors = null;
        }
    }

    [UnmanagedCallersOnly]
    static int OnMonitor(nint monitor, nint dc, RECT* rect, nint data)
    {
        s_monitors?.Add(*rect);
        return 1;
    }

    /// <summary>
    /// The windows the user sees, topmost first, with the bounds they are drawn in (without the invisible resize borders):
    /// visible, not minimized, not cloaked (other virtual desktops, suspended apps), not click-through, and not the
    /// desktop itself or this process's own.
    /// </summary>
    public static List<RECT> Windows()
    {
        var windows = new List<RECT>();
        uint self = (uint)Environment.ProcessId;
        int guard = 0;
        for (nint hwnd = Native.GetTopWindow(0); hwnd != 0 && guard++ < 5000; hwnd = Native.GetWindow(hwnd, Native.GW_HWNDNEXT))
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
                continue;
            uint process;
            Native.GetWindowThreadProcessId(hwnd, &process);
            if (process == self)
                continue;
            int cloaked = 0;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0)
                continue;
            if ((Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TRANSPARENT) != 0)
                continue;
            if (Native.GetClassName(hwnd) is "Progman" or "WorkerW")
                continue;
            RECT bounds;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, (uint)sizeof(RECT)) != 0)
                Native.GetWindowRect(hwnd, &bounds);
            if (bounds.Width > 0 && bounds.Height > 0)
                windows.Add(bounds);
        }
        return windows;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint hdc, RECT* clip, delegate* unmanaged<nint, nint, RECT*, nint, int> callback, nint data);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);
}
