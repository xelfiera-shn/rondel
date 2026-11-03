using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Rondel.Core.Localization;
using Rondel.Windows.Interop;
using static Rondel.Windows.Interop.Native;
using Path = System.Windows.Shapes.Path;
using Rect = System.Windows.Rect;

namespace Rondel.Windows.UI;

/// <summary>
/// Ask About a Screenshot: the screen, frozen as it was when the shortcut was pressed, under a dim veil over every
/// monitor. Dragging picks an area, a click picks the window under the pointer (outlined while hovered), Enter picks the
/// monitor under the pointer and Esc cancels. The picked part is cut out of the frozen picture, so what is shown is
/// exactly what is sent. One window spans the virtual screen; it works in physical pixels and draws in DIPs of its own
/// DPI, so each monitor's part lines up with it.
/// </summary>
internal sealed class ScreenshotWindow : Window
{
    /// <summary>Shorter than this (physical pixels) in either direction, a drag counts as a click.</summary>
    const int DragThreshold = 6;

    readonly BitmapSource _pixels;
    readonly RECT _bounds;
    readonly List<RECT> _monitors;
    readonly List<RECT> _windows;
    readonly Action<BitmapSource?> _done;
    readonly Brush _accent;

    readonly Canvas _canvas = new() { Background = Brushes.Transparent, ClipToBounds = true };
    readonly Path _veil = new() { Fill = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), IsHitTestVisible = false };
    readonly Rectangle _frame = new() { StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Border _hint = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 7, 12, 7), IsHitTestVisible = false };
    readonly TextBlock _size = new() { FontSize = 11, Foreground = Brushes.White, IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    double _scale = 1;
    POINT? _pressed;
    RECT? _selection;
    bool _finished;

    /// <param name="done">Gets the picked part, or null when the user cancelled.</param>
    public ScreenshotWindow(BitmapSource pixels, RECT bounds, List<RECT> monitors, List<RECT> windows, RondelPalette palette, Action<BitmapSource?> done)
    {
        _pixels = pixels;
        _bounds = bounds;
        _monitors = monitors;
        _windows = windows;
        _done = done;
        _accent = palette.Accent;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = Loc.Get("Action.Screenshot");

        _frame.Stroke = _accent;
        _hint.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x18, 0x1B, 0x22));
        _hint.Child = new TextBlock { Text = Loc.Get("Screenshot.Hint"), FontSize = 12.5, Foreground = Brushes.White };
        var picture = new Image { Source = pixels, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        _canvas.Children.Add(picture);
        _canvas.Children.Add(_veil);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_size);
        _canvas.Children.Add(_hint);
        Content = _canvas;

        SourceInitialized += (_, _) => Place();
        // Spanning monitors with other scales, Windows may move or resize the window to another DPI: cover the screen again.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        Loaded += (_, _) =>
        {
            Place();
            Activate();
            Focus();
            Keyboard.Focus(this);
            Highlight(GetCursorPosition());
        };
        Deactivated += (_, _) => Finish(null); // another window took over: nothing is picked behind the user's back
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Finish(null);
        PreviewKeyDown += OnKey;
    }

    /// <summary>Covers the virtual screen exactly, in physical pixels, and lays the picture and the veil out on it.</summary>
    void Place()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
            return;
        SetWindowPos(hwnd, HWND_TOPMOST, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, SWP_NOACTIVATE);
        uint dpi = GetDpiForWindow(hwnd);
        _scale = (dpi == 0 ? 96 : dpi) / 96.0;
        double width = _bounds.Width / _scale, height = _bounds.Height / _scale;
        _canvas.Width = width;
        _canvas.Height = height;
        var picture = (Image)_canvas.Children[0];
        picture.Width = width;
        picture.Height = height;
        PlaceHint();
        UpdateVeil(null);
    }

    /// <summary>The hint at the top of the monitor under the pointer.</summary>
    void PlaceHint()
    {
        var monitor = MonitorAt(GetCursorPosition());
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = (monitor.Left - _bounds.Left + monitor.Width / 2.0) / _scale - _hint.DesiredSize.Width / 2;
        Canvas.SetLeft(_hint, left);
        Canvas.SetTop(_hint, (monitor.Top - _bounds.Top) / _scale + 24);
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = GetCursorPosition();
        CaptureMouse();
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        var point = GetCursorPosition();
        if (_pressed is { } start && IsDrag(start, point))
        {
            _selection = Span(start, point);
            Show(_selection.Value, showSize: true);
        }
        else if (_pressed is null)
        {
            Highlight(point);
        }
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed is not { } start)
            return;
        ReleaseMouseCapture();
        var point = GetCursorPosition();
        _pressed = null;
        e.Handled = true;
        if (IsDrag(start, point))
            Finish(Span(start, point));
        else
            Finish(WindowAt(point) ?? MonitorAt(point)); // a click: the window under it, or its monitor
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Finish(null);
                break;
            case Key.Enter:
                e.Handled = true;
                Finish(MonitorAt(GetCursorPosition()));
                break;
        }
    }

    /// <summary>Outlines the window under the pointer, which a click would pick.</summary>
    void Highlight(POINT point)
    {
        if (WindowAt(point) is { } window)
            Show(window, showSize: false);
        else
            Show(null, showSize: false);
        PlaceHint();
    }

    void Show(RECT? area, bool showSize)
    {
        UpdateVeil(area);
        if (area is not { } a)
        {
            _frame.Visibility = Visibility.Collapsed;
            _size.Visibility = Visibility.Collapsed;
            return;
        }
        var rect = ToDips(a);
        Canvas.SetLeft(_frame, rect.Left - 1);
        Canvas.SetTop(_frame, rect.Top - 1);
        _frame.Width = rect.Width + 2;
        _frame.Height = rect.Height + 2;
        _frame.Visibility = Visibility.Visible;
        _size.Text = $"{a.Width} × {a.Height}";
        _size.Visibility = showSize ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_size, rect.Left);
        Canvas.SetTop(_size, rect.Top > 22 ? rect.Top - 20 : rect.Bottom + 4);
    }

    /// <summary>The veil over everything but <paramref name="area"/>.</summary>
    void UpdateVeil(RECT? area)
    {
        var all = new RectangleGeometry(new Rect(0, 0, _bounds.Width / _scale, _bounds.Height / _scale));
        _veil.Data = area is { } a
            ? new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(ToDips(a)))
            : all;
    }

    Rect ToDips(RECT r) =>
        new((r.Left - _bounds.Left) / _scale, (r.Top - _bounds.Top) / _scale, r.Width / _scale, r.Height / _scale);

    RECT? WindowAt(POINT point)
    {
        foreach (var window in _windows)
        {
            if (Contains(window, point))
                return Intersect(window, MonitorAt(point));
        }
        return null;
    }

    RECT MonitorAt(POINT point) =>
        _monitors.FirstOrDefault(m => Contains(m, point), _monitors.Count > 0 ? _monitors[0] : _bounds);

    static bool Contains(RECT r, POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    static bool IsDrag(POINT a, POINT b) => Math.Abs(a.X - b.X) >= DragThreshold || Math.Abs(a.Y - b.Y) >= DragThreshold;

    static RECT Span(POINT a, POINT b) => new()
    {
        Left = Math.Min(a.X, b.X),
        Top = Math.Min(a.Y, b.Y),
        Right = Math.Max(a.X, b.X) + 1,
        Bottom = Math.Max(a.Y, b.Y) + 1,
    };

    /// <summary>A window reaching past its monitor (maximized ones do by a few pixels) is cut to it.</summary>
    static RECT Intersect(RECT r, RECT to) => new()
    {
        Left = Math.Max(r.Left, to.Left),
        Top = Math.Max(r.Top, to.Top),
        Right = Math.Min(r.Right, to.Right),
        Bottom = Math.Min(r.Bottom, to.Bottom),
    };

    /// <summary>Developer aid (--render-shot-picker): draws the picker with <paramref name="area"/> picked, without showing it.</summary>
    internal BitmapSource RenderPreview(RECT area, bool dragging)
    {
        _scale = 1;
        _canvas.Width = _bounds.Width;
        _canvas.Height = _bounds.Height;
        var picture = (Image)_canvas.Children[0];
        picture.Width = _bounds.Width;
        picture.Height = _bounds.Height;
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_hint, _bounds.Width / 2.0 - _hint.DesiredSize.Width / 2);
        Canvas.SetTop(_hint, 24);
        Show(area, showSize: dragging);
        var size = new Size(_bounds.Width, _bounds.Height);
        _canvas.Measure(size);
        _canvas.Arrange(new Rect(size));
        _canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(_bounds.Width, _bounds.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_canvas);
        bitmap.Freeze();
        return bitmap;
    }

    void Finish(RECT? area)
    {
        if (_finished)
            return;
        _finished = true;
        BitmapSource? picked = null;
        if (area is { } a)
        {
            // Within the frozen picture: pixel coordinates from the virtual screen's corner.
            int left = Math.Clamp(a.Left - _bounds.Left, 0, _pixels.PixelWidth), top = Math.Clamp(a.Top - _bounds.Top, 0, _pixels.PixelHeight);
            int right = Math.Clamp(a.Right - _bounds.Left, 0, _pixels.PixelWidth), bottom = Math.Clamp(a.Bottom - _bounds.Top, 0, _pixels.PixelHeight);
            if (right - left > 0 && bottom - top > 0)
            {
                var cut = new CroppedBitmap(_pixels, new Int32Rect(left, top, right - left, bottom - top));
                cut.Freeze();
                picked = cut;
            }
        }
        Close();
        _done(picked);
    }
}
