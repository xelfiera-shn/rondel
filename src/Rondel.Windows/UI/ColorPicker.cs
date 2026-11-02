using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Rondel.Core.Themes;

namespace Rondel.Windows.UI;

/// <summary>
/// Picks a color with the mouse: an area of the chosen hue (saturation across, brightness up) and a hue strip under
/// it. Dragging reports the color as it changes; releasing the button commits it. The keyboard has the hex box next
/// to it.
/// </summary>
internal sealed class ColorPicker : StackPanel
{
    const double AreaWidth = 240, AreaHeight = 150, StripHeight = 12, ThumbSize = 16;

    readonly SolidColorBrush _hueFill = new();
    readonly SolidColorBrush _colorFill = new();
    readonly Ellipse _areaThumb, _stripThumb;
    readonly Canvas _area, _strip;
    double _hue, _saturation, _value;
    bool _dragging;

    public ColorPicker()
    {
        var hues = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        uint[] stops = [0xFFFF0000, 0xFFFFFF00, 0xFF00FF00, 0xFF00FFFF, 0xFF0000FF, 0xFFFF00FF, 0xFFFF0000];
        for (int i = 0; i < stops.Length; i++)
            hues.GradientStops.Add(new GradientStop(ToColor(stops[i]), i / (double)(stops.Length - 1)));
        hues.Freeze();
        var whiteAcross = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0));
        var blackDown = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1));
        whiteAcross.Freeze();
        blackDown.Freeze();

        _area = new Canvas { Width = AreaWidth, Height = AreaHeight, Background = Brushes.Transparent, Cursor = Cursors.Cross };
        foreach (var fill in new Brush[] { _hueFill, whiteAcross, blackDown })
            _area.Children.Add(new Rectangle { Width = AreaWidth, Height = AreaHeight, RadiusX = 6, RadiusY = 6, Fill = fill });
        _areaThumb = Thumb(_colorFill);
        _area.Children.Add(_areaThumb);

        // The strip is as tall as its thumb, so the thumb is never cut off; the colors are a band in its middle.
        _strip = new Canvas { Width = AreaWidth, Height = ThumbSize, Margin = new Thickness(0, 12, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var band = new Rectangle { Width = AreaWidth, Height = StripHeight, RadiusX = StripHeight / 2, RadiusY = StripHeight / 2, Fill = hues };
        Canvas.SetTop(band, (ThumbSize - StripHeight) / 2);
        _strip.Children.Add(band);
        _stripThumb = Thumb(_hueFill);
        Canvas.SetTop(_stripThumb, 0);
        _strip.Children.Add(_stripThumb);

        Children.Add(_area);
        Children.Add(_strip);
        Follow(_area, p => (_saturation, _value) = (p.X / AreaWidth, 1 - p.Y / AreaHeight));
        Follow(_strip, p => _hue = p.X / AreaWidth * 360);
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
        void Pick(MouseEventArgs e)
        {
            var p = e.GetPosition(target);
            pick(new Point(Math.Clamp(p.X, 0, AreaWidth), Math.Clamp(p.Y, 0, target.Height)));
            Update();
            Changing?.Invoke(Picked);
        }
        target.MouseLeftButtonDown += (_, e) =>
        {
            _dragging = true;
            target.CaptureMouse();
            Pick(e);
            e.Handled = true;
        };
        target.MouseMove += (_, e) =>
        {
            if (_dragging && target.IsMouseCaptured)
                Pick(e);
        };
        target.MouseLeftButtonUp += (_, e) =>
        {
            target.ReleaseMouseCapture();
            e.Handled = true;
        };
        // Released, or capture taken away (another window came to the front): what was picked is the color.
        target.LostMouseCapture += (_, _) =>
        {
            if (!_dragging)
                return;
            _dragging = false;
            Committed?.Invoke(Picked);
        };
    }

    void Update()
    {
        _hueFill.Color = ToColor(new HsvColor(_hue, 1, 1).ToArgb());
        _colorFill.Color = ToColor(Picked);
        Canvas.SetLeft(_areaThumb, _saturation * AreaWidth - ThumbSize / 2);
        Canvas.SetTop(_areaThumb, (1 - _value) * AreaHeight - ThumbSize / 2);
        Canvas.SetLeft(_stripThumb, _hue / 360 * AreaWidth - ThumbSize / 2);
    }

    /// <summary>A white ring with a soft shadow, so it shows on light and dark colors alike.</summary>
    static Ellipse Thumb(Brush fill) => new()
    {
        Width = ThumbSize,
        Height = ThumbSize,
        Fill = fill,
        Stroke = Brushes.White,
        StrokeThickness = 2.5,
        IsHitTestVisible = false,
        Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black },
    };

    static Color ToColor(uint argb) => System.Windows.Media.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
