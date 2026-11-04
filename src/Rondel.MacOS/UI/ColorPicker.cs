using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using HsvColor = Rondel.Core.Themes.HsvColor;

namespace Rondel.MacOS.UI;

/// <summary>
/// Picks a color with the mouse, as on Windows: an area of the chosen hue (saturation across, brightness up) and a hue
/// strip under it. Dragging reports the color as it changes; releasing the button commits it. The keyboard has the hex
/// box next to it.
/// </summary>
internal sealed class ColorPicker : StackPanel
{
    const double AreaWidth = 240, AreaHeight = 150, StripHeight = 12, ThumbSize = 16;

    readonly SolidColorBrush _hueFill = new();
    readonly SolidColorBrush _colorFill = new();
    readonly Border _areaThumb, _stripThumb;
    double _hue, _saturation, _value;
    bool _dragging;

    public ColorPicker()
    {
        var hues = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative) };
        uint[] stops = [0xFFFF0000, 0xFFFFFF00, 0xFF00FF00, 0xFF00FFFF, 0xFF0000FF, 0xFFFF00FF, 0xFFFF0000];
        for (int i = 0; i < stops.Length; i++)
            hues.GradientStops.Add(new GradientStop(Color.FromUInt32(stops[i]), i / (double)(stops.Length - 1)));
        var whiteAcross = Linear(Colors.White, Color.FromArgb(0, 255, 255, 255), across: true);
        var blackDown = Linear(Color.FromArgb(0, 0, 0, 0), Colors.Black, across: false);

        var area = new Canvas { Width = AreaWidth, Height = AreaHeight, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Cross) };
        foreach (var fill in new IBrush[] { _hueFill, whiteAcross, blackDown })
            area.Children.Add(new Rectangle { Width = AreaWidth, Height = AreaHeight, RadiusX = 6, RadiusY = 6, Fill = fill });
        _areaThumb = Thumb(_colorFill);
        area.Children.Add(_areaThumb);

        // The strip is as tall as its thumb, so the thumb is never cut off; the colors are a band in its middle.
        var strip = new Canvas { Width = AreaWidth, Height = ThumbSize, Margin = new Thickness(0, 12, 0, 0), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        var band = new Rectangle { Width = AreaWidth, Height = StripHeight, RadiusX = StripHeight / 2, RadiusY = StripHeight / 2, Fill = hues };
        Canvas.SetTop(band, (ThumbSize - StripHeight) / 2);
        strip.Children.Add(band);
        _stripThumb = Thumb(_hueFill);
        strip.Children.Add(_stripThumb);

        Children.Add(area);
        Children.Add(strip);
        Follow(area, p => (_saturation, _value) = (p.X / AreaWidth, 1 - p.Y / AreaHeight));
        Follow(strip, p => _hue = p.X / AreaWidth * 360);
        Update();
    }

    /// <summary>The color while the mouse drags it.</summary>
    public event Action<uint>? Changing;

    /// <summary>The color once the mouse button is released.</summary>
    public event Action<uint>? Committed;

    public uint Picked => new HsvColor(_hue, _saturation, _value).ToArgb();

    public void SetColor(uint argb)
    {
        var hsv = HsvColor.FromArgb(argb);
        // A gray has no hue of its own: keep the one in use, so the area does not jump to red.
        if (hsv.Saturation > 0 && hsv.Value > 0)
            _hue = hsv.Hue;
        (_saturation, _value) = (hsv.Saturation, hsv.Value);
        Update();
    }

    /// <summary>Pressing on <paramref name="target"/> and dragging moves its thumb to the mouse, kept inside it.</summary>
    void Follow(Canvas target, Action<Point> pick)
    {
        void Pick(PointerEventArgs e)
        {
            var p = e.GetPosition(target);
            pick(new Point(Math.Clamp(p.X, 0, AreaWidth), Math.Clamp(p.Y, 0, target.Height)));
            Update();
            Changing?.Invoke(Picked);
        }
        target.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(target).Properties.IsLeftButtonPressed)
                return;
            _dragging = true;
            e.Pointer.Capture(target);
            Pick(e);
            e.Handled = true;
        };
        target.PointerMoved += (_, e) =>
        {
            if (_dragging && e.Pointer.Captured == target)
                Pick(e);
        };
        target.PointerReleased += (_, e) =>
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        // Released, or capture taken away (another window came to the front): what was picked is the color.
        target.PointerCaptureLost += (_, _) =>
        {
            if (!_dragging)
                return;
            _dragging = false;
            Committed?.Invoke(Picked);
        };
    }

    void Update()
    {
        _hueFill.Color = Color.FromUInt32(new HsvColor(_hue, 1, 1).ToArgb());
        _colorFill.Color = Color.FromUInt32(Picked);
        Canvas.SetLeft(_areaThumb, _saturation * AreaWidth - ThumbSize / 2);
        Canvas.SetTop(_areaThumb, (1 - _value) * AreaHeight - ThumbSize / 2);
        Canvas.SetLeft(_stripThumb, _hue / 360 * AreaWidth - ThumbSize / 2);
    }

    /// <summary>A white ring with a soft shadow, so it shows on light and dark colors alike.</summary>
    static Border Thumb(IBrush fill) => new()
    {
        Width = ThumbSize,
        Height = ThumbSize,
        CornerRadius = new CornerRadius(ThumbSize / 2),
        Background = fill,
        BorderBrush = Brushes.White,
        BorderThickness = new Thickness(2.5),
        IsHitTestVisible = false,
        BoxShadow = BoxShadows.Parse("0 0 4 0 #8C000000"),
    };

    static LinearGradientBrush Linear(Color from, Color to, bool across) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(across ? 1 : 0, across ? 0 : 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
    };
}

/// <summary>
/// The dot inside a hex box: it shows the box's color (none while the text is not a color) and opens the color picker
/// on a click, Space or Enter.
/// </summary>
internal sealed class ColorDot : Border
{
    readonly PageLook _look;

    public ColorDot(PageLook look)
    {
        _look = look;
        Width = Height = 18;
        CornerRadius = new CornerRadius(9);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(8, 0, 2, 0);
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        PointerEntered += (_, _) => Paint();
        PointerExited += (_, _) => Paint();
        GotFocus += (_, _) => Paint();
        LostFocus += (_, _) => Paint();
        PointerPressed += (_, e) =>
        {
            // The box around it would take the focus and select its text.
            e.Handled = true;
        };
        PointerReleased += (_, e) =>
        {
            e.Handled = true;
            Clicked?.Invoke();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter)
            {
                e.Handled = true;
                Clicked?.Invoke();
            }
        };
        Paint();
    }

    public event Action? Clicked;

    public void Show(uint? color) => Background = color is { } value ? new SolidColorBrush(Color.FromUInt32(value)) : Brushes.Transparent;

    void Paint() => BorderBrush = IsPointerOver || IsFocused ? _look.SubText : _look.CardStroke;
}
