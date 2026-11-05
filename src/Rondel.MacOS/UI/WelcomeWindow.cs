using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Rondel.Core;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Menus;
using Rondel.Core.Settings;
using Rondel.MacOS.Interop;
using Path = Avalonia.Controls.Shapes.Path;

namespace Rondel.MacOS.UI;

/// <summary>
/// Shown by itself the first time Rondel runs, and from the menu bar (How to use) whenever asked, the counterpart of
/// the Windows WelcomeWindow: how the menu opens, played in a loop (the pointer goes to an empty spot in a Finder
/// window, Option and the left button are held, the menu opens, the pointer moves onto a slice and lets go, and the
/// agent starts in Terminal), with the three steps lit in turn. The menu in the picture is drawn by the menu itself,
/// in the user's theme and language. Below it are the two permissions Rondel needs, with what they are for: the first
/// time, they are asked from here rather than by macOS the moment Rondel starts.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    /// <summary>One play of the gesture, in seconds.</summary>
    public const double Period = 7.2;

    /// <summary>What is shown when Reduce Motion is on: the menu open, a slice under the pointer.</summary>
    public const double StillMoment = 3.95;

    const double MenuScale = 0.6;
    const double SliceRadius = 132; // with the angle below: under the slice's label, so the pointer does not cover it

    static readonly Point Start = new(118, 238);
    static readonly Point Press = new(236, 150);

    readonly AppHost _host;
    readonly Stopwatch _clock = new();
    readonly List<(Border Badge, TextBlock Number, TextBlock Text)> _steps = [];
    IDisposable? _timer;
    PageLook _look = PageLook.Dark;
    IBrush _accent = Brushes.Teal, _keyFill = Brushes.White, _keyBorder = Brushes.Gray, _keyText = Brushes.Black;
    Point _slice;

    // The picture's moving parts.
    readonly Image _menuIdle = new() { Opacity = 0, RenderTransformOrigin = RelativePoint.Center };
    readonly Image _menuHot = new() { Opacity = 0, RenderTransformOrigin = RelativePoint.Center };
    readonly ScaleTransform _menuIdleScale = new(), _menuHotScale = new(), _terminalScale = new();
    readonly Border _terminal = new() { Width = 320, Height = 150, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Opacity = 0, RenderTransformOrigin = RelativePoint.Center };
    readonly Ellipse _ripple = new() { StrokeThickness = 2, Opacity = 0 };
    readonly Path _holdRing = new() { StrokeThickness = 3, StrokeLineCap = PenLineCap.Round };
    readonly Path _pointer = new() { Fill = Brushes.Black, Stroke = Brushes.White, StrokeThickness = 1.1, StrokeJoin = PenLineJoin.Round };
    readonly Border _optionKey = new() { Width = 70, Height = 36, CornerRadius = new CornerRadius(6) };
    readonly TextBlock _optionText = new() { Text = "⌥ option", FontSize = 12.5, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _mouseLeft = new() { Width = 13, Height = 18, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1.5, 1.5, 0, 0), CornerRadius = new CornerRadius(12, 0, 0, 0) };
    PermissionRow _accessibility = null!, _finder = null!;

    public WelcomeWindow(AppHost host)
    {
        _host = host;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily(".AppleSystemUIFont");
        _menuIdle.RenderTransform = _menuIdleScale;
        _menuHot.RenderTransform = _menuHotScale;
        _terminal.RenderTransform = _terminalScale;
        Build();

        Loc.Changed += Build;
        _host.PermissionsChanged += RefreshPermissions;
        ActualThemeVariantChanged += (_, _) => Build();
        Activated += (_, _) =>
        {
            _host.CheckFinderAccess();
            RefreshPermissions();
        };
        Opened += (_, _) => Play();
        Closed += (_, _) =>
        {
            StopPlaying();
            Loc.Changed -= Build;
            _host.PermissionsChanged -= RefreshPermissions;
        };
    }

    /// <summary>Open Settings was chosen; the window closes itself.</summary>
    public event Action? SettingsRequested;

    /// <summary>Plays the gesture in a loop, or shows one moment of it when Reduce Motion is on.</summary>
    void Play()
    {
        if (MenuPanel.ReduceMotion)
        {
            ShowMoment(StillMoment);
            return;
        }
        _clock.Restart();
        _timer ??= DispatcherTimer.Run(() =>
        {
            ShowMoment(_clock.Elapsed.TotalSeconds);
            return true;
        }, TimeSpan.FromMilliseconds(16));
    }

    /// <summary>Stops the loop where it is (developer aids draw chosen moments).</summary>
    public void StopPlaying()
    {
        _timer?.Dispose();
        _timer = null;
    }

    void RefreshPermissions()
    {
        _accessibility.Show(Native.AXIsProcessTrusted());
        _finder.Show(_host.HasFinderAccess);
    }

    // ---- building ----

    void Build()
    {
        bool dark = ActualThemeVariant != ThemeVariant.Light;
        _look = dark ? PageLook.Dark : PageLook.Light;
        Title = Loc.Get("Welcome.Title");
        Background = _look.Background;
        var appearance = _host.Store.Current.Appearance;
        var palette = MenuPalette.For(appearance, appearance.IsDark(dark));
        _accent = palette.Accent;
        _keyFill = Solid(dark ? 0xFF323843 : 0xFFFFFFFF);
        _keyBorder = Solid(dark ? 0xFF4A5260 : 0xFFC5CDD8);
        _keyText = Solid(dark ? 0xFFE8EAED : 0xFF1F2329);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                PageLook.Logo(),
                new TextBlock
                {
                    Text = Loc.Get("Welcome.Title"), FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = _look.Text,
                    Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                },
            },
        });
        root.Children.Add(new Border { Height = 2, Margin = new Thickness(0, 14, 0, 16), CornerRadius = new CornerRadius(1), Background = _look.Brand });
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = _look.CardStroke,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = Stage(dark, palette),
        });

        var steps = new StackPanel { Margin = new Thickness(2, 16, 0, 0) };
        _steps.Clear();
        foreach (string key in new[] { "Welcome.Step1.Mac", "Welcome.Step2.Mac", "Welcome.Step3" })
            steps.Children.Add(Step(_steps.Count + 1, Loc.Get(key)));
        root.Children.Add(steps);

        _accessibility = new PermissionRow(Loc.Get("Settings.Permissions.Accessibility"), Loc.Get("Settings.Permissions.Accessibility.Desc"), _host.AllowAccessibility, _look);
        _finder = new PermissionRow(Loc.Get("Settings.Permissions.Finder"), Loc.Get("Settings.Permissions.Finder.Desc"), _host.AllowFinderAccess, _look);
        root.Children.Add(new Border
        {
            Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 8, 0, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = _look.Card,
            BorderBrush = _look.CardStroke,
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = Loc.Get("Welcome.Permissions.Mac"), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = _look.Text },
                            new TextBlock { Text = Loc.Get("Welcome.Permissions.Desc.Mac"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Foreground = _look.SubText },
                        },
                    },
                    _accessibility,
                    _finder,
                },
            },
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.Get("Welcome.Note.Mac"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = _look.SubText, Margin = new Thickness(2, 14, 0, 0),
        });

        var settings = new Button { Content = Loc.Get("Welcome.Settings"), Padding = new Thickness(12, 5) };
        settings.Click += (_, _) =>
        {
            SettingsRequested?.Invoke();
            Close();
        };
        var start = new Button { Content = Loc.Get("Welcome.Start"), Padding = new Thickness(16, 5) };
        start.Classes.Add("accent");
        start.Click += (_, _) => Close();
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0),
            Children = { settings, start },
        });
        Content = root;
        RefreshPermissions();
        ShowMoment(_timer is null ? StillMoment : _clock.Elapsed.TotalSeconds);
    }

    /// <summary>The scene: a Finder window on the desktop, the Option key and the mouse.</summary>
    Canvas Stage(bool dark, MenuPalette palette)
    {
        var stage = new Canvas { Width = 530, Height = 290, Background = Solid(dark ? 0xFF1A1E25 : 0xFFDDE4ED) };
        foreach (var part in new Control[] { _menuIdle, _menuHot, _terminal, _ripple, _holdRing, _pointer, _optionKey, _mouseLeft })
            (part.Parent as Panel)?.Children.Remove(part);
        (_optionText.Parent as Border)?.Child = null;

        var text = Solid(dark ? 0xFFE8EAED : 0xFF1F2329);
        var subText = _look.SubText;
        var items = new WrapPanel { Margin = new Thickness(14, 12, 0, 0) };
        foreach (var (symbol, name, color) in new[]
        {
            ("folder.fill", "src", 0xFF5AA9F2u), ("doc.text", "README.md", dark ? 0xFF9AA3AEu : 0xFF5F6B78u), ("doc.text", "package.json", dark ? 0xFF9AA3AEu : 0xFF5F6B78u),
        })
        {
            items.Children.Add(new StackPanel
            {
                Width = 84,
                Margin = new Thickness(0, 0, 8, 0),
                Children =
                {
                    Symbol(symbol, 30, Solid(color)),
                    new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Foreground = subText },
                },
            });
        }
        var finder = new Border
        {
            Width = 386, Height = 254, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Background = Solid(dark ? 0xFF262B33 : 0xFFFFFFFF), BorderBrush = Solid(dark ? 0xFF353C47 : 0xFFC9D1DC), ClipToBounds = true,
            Child = new DockPanel
            {
                Children =
                {
                    TitleBar(Solid(dark ? 0xFF2E343E : 0xFFF0F2F5), Symbol("folder.fill", 13, Solid(0xFF5AA9F2)), "weather-api", text),
                    items,
                },
            },
        };
        Place(stage, finder, 20, 18);

        // The terminal the agent starts in.
        _terminal.Background = Solid(0xFF1E1E1E);
        _terminal.BorderBrush = Solid(0xFF3A3A3A);
        _terminal.Child = new DockPanel
        {
            Children =
            {
                TitleBar(Solid(0xFF2B2B2B), null, "weather-api — zsh", Solid(0xFFCCCCCC)),
                new TextBlock { Text = "weather-api % claude", Margin = new Thickness(10, 8), FontFamily = new FontFamily("Menlo"), FontSize = 11.5, Foreground = Solid(0xFFD0D0D0) },
            },
        };
        Place(stage, _terminal, 70, 62);

        // The menu of an empty spot in a Finder window, drawn by the menu itself: as it opens, and with Open Here hot.
        var settings = new AppSettings();
        settings.Normalize();
        settings.Appearance = _host.Store.Current.Appearance;
        string folder = System.IO.Path.Combine(settings.ResolveProjectsRoot(), "weather-api");
        var model = MenuBuilder.Build(new TriggerContext(ContextKind.ExplorerEmpty, folder, "weather-api", "welcome"), settings);
        int open = Math.Max(0, model.Items.ToList().FindIndex(i => i.Action.Id == "open-here"));
        var hints = model.Items.Select(_ => ((string?)null, false)).ToList();
        var children = model.Items.Select(_ => (IReadOnlyList<MenuItemModel>)[]).ToList();
        var menu = new RadialMenuWindow(palette);
        try
        {
            _menuIdle.Source = ThemePreviews.Ring(menu.RenderPreview(model, hints, children, [], Colors.Transparent));
            _menuHot.Source = ThemePreviews.Ring(menu.RenderPreview(model, hints, children, [open], Colors.Transparent));
        }
        finally
        {
            menu.Close();
        }
        foreach (var image in new[] { _menuIdle, _menuHot })
        {
            // Two pixels a point.
            var size = image.Source!.Size;
            image.Width = size.Width / 2 * MenuScale;
            image.Height = size.Height / 2 * MenuScale;
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            Place(stage, image, Press.X - image.Width / 2, Press.Y - image.Height / 2);
        }
        int n = model.Items.Count;
        double angle = ((open + 0.3) * 360.0 / n + (n == 2 ? -90 : 0)) * Math.PI / 180;
        _slice = new Point(Press.X + SliceRadius * MenuScale * Math.Sin(angle), Press.Y - SliceRadius * MenuScale * Math.Cos(angle));

        _ripple.Stroke = _accent;
        _holdRing.Stroke = _accent;
        stage.Children.Add(_ripple);
        stage.Children.Add(_holdRing);
        // The macOS arrow: black with a white edge, its tip at the point.
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(new Point(0, 0), true);
            foreach (var p in new[] { new Point(0, 17), new Point(4.4, 13.2), new Point(7.4, 19.6), new Point(10.2, 18.4), new Point(7.3, 12.2), new Point(12.6, 12.2) })
                g.LineTo(p);
            g.EndFigure(true);
        }
        _pointer.Data = arrow;
        stage.Children.Add(_pointer);

        // Option + the mouse, the left button lit while held.
        _optionKey.Child = _optionText;
        var mouse = new Panel
        {
            Width = 30, Height = 44, Margin = new Thickness(2, 0, 0, 0),
            Children =
            {
                new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), BorderBrush = _keyBorder, Background = _keyFill },
                _mouseLeft,
                new Border { Width = 1.5, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1.5, 0, 0), Background = _keyBorder },
            },
        };
        var keys = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _optionKey, new TextBlock { Text = "+", FontSize = 16, Margin = new Thickness(7, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = _look.SubText }, mouse },
        };
        Place(stage, keys, 380, 210);
        return stage;
    }

    static DockPanel TitleBar(IBrush background, Control? glyph, string title, IBrush text)
    {
        var lights = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (uint color in new[] { 0xFFFF5F57u, 0xFFFEBC2Eu, 0xFF28C840u })
            lights.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Solid(color) });
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null)
            name.Children.Add(glyph);
        name.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = text, VerticalAlignment = VerticalAlignment.Center });
        var bar = new DockPanel { Height = 28, Background = background, Children = { new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0), Children = { lights, name } } } };
        DockPanel.SetDock(bar, Dock.Top);
        return bar;
    }

    Control Symbol(string name, double size, IBrush fill)
    {
        var mask = SymbolIcons.Mask(name, size);
        return new Rectangle
        {
            Width = size, Height = size, Fill = fill, HorizontalAlignment = HorizontalAlignment.Center,
            OpacityMask = mask is null ? null : new ImageBrush(mask) { Stretch = Stretch.Uniform },
        };
    }

    Control Step(int number, string text)
    {
        var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5) };
        var digit = new TextBlock
        {
            Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11.5, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        badge.Child = digit;
        var label = new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0), FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(label);
        _steps.Add((badge, digit, label));
        return row;
    }

    // ---- the loop ----

    /// <summary>Puts every part of the picture where it is <paramref name="seconds"/> into the loop.</summary>
    public void ShowMoment(double seconds)
    {
        double t = seconds % Period;
        double Seg(double from, double to) => Math.Clamp((t - from) / (to - from), 0, 1);
        static double Ease(double x) => x * x * (3 - 2 * x);
        static Point Lerp(Point a, Point b, double k) => new(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);

        // 0.4–1.2 s to an empty spot; Option at 1.4, the button at 1.9, held until the menu opens at 2.7;
        // 3.2–3.9 s onto Open Here, let go at 4.4; the terminal at 4.7; everything fades from 6.5.
        double fade = 1 - Seg(6.5, 6.9);
        var pointer = t < 3.2 ? Lerp(Start, Press, Ease(Seg(0.4, 1.2))) : Lerp(Press, _slice, Ease(Seg(3.2, 3.9)));
        Canvas.SetLeft(_pointer, pointer.X);
        Canvas.SetTop(_pointer, pointer.Y);
        _pointer.Opacity = Seg(0, 0.3) * fade;

        bool held = t >= 1.9 && t < 4.4;
        bool option = t >= 1.4 && t < 4.4;
        _optionKey.Background = option ? _accent : _keyFill;
        _optionKey.BorderBrush = option ? _accent : _keyBorder;
        _optionKey.BorderThickness = new Thickness(1, 1, 1, option ? 1 : 3);
        _optionKey.Margin = new Thickness(0, option ? 2 : 0, 0, 0);
        _optionText.Foreground = option ? MenuPalette.OnAccent : _keyText;
        _mouseLeft.Background = held ? _accent : Brushes.Transparent;

        double hold = Seg(1.9, 2.7);
        _holdRing.Data = t >= 1.9 && t < 2.9 ? Arc(Press, 15, hold) : null;
        _holdRing.Opacity = 1 - Seg(2.7, 2.9);
        double ripple = Seg(1.9, 2.35);
        double size = 8 + 26 * ripple;
        _ripple.Width = _ripple.Height = size;
        Canvas.SetLeft(_ripple, Press.X - size / 2);
        Canvas.SetTop(_ripple, Press.Y - size / 2);
        _ripple.Opacity = t >= 1.9 && t < 2.35 ? 0.7 * (1 - ripple) : 0;

        double opened = Ease(Seg(2.7, 3.0));
        double shown = opened * (1 - Seg(4.5, 4.8));
        double hot = Seg(3.55, 3.7);
        _menuIdle.Opacity = shown * (1 - hot);
        _menuHot.Opacity = shown * hot;
        foreach (var scale in new[] { _menuIdleScale, _menuHotScale })
            scale.ScaleX = scale.ScaleY = 0.86 + 0.14 * opened;

        double terminal = Seg(4.7, 5.0);
        _terminal.Opacity = terminal * fade;
        _terminalScale.ScaleX = _terminalScale.ScaleY = 0.94 + 0.06 * Ease(terminal);

        int step = t < 1.4 ? -1 : t < 1.9 ? 0 : t < 3.2 ? 1 : t < 4.6 ? 2 : -1;
        for (int i = 0; i < _steps.Count; i++)
        {
            var (badge, number, text) = _steps[i];
            bool active = i == step;
            badge.Background = active ? _accent : Brushes.Transparent;
            badge.BorderBrush = active ? _accent : _look.SubText;
            number.Foreground = active ? MenuPalette.OnAccent : _look.SubText;
            text.Foreground = active ? _look.Text : _look.SubText;
        }
    }

    /// <summary>The part of a circle from its top, clockwise, that <paramref name="fraction"/> covers.</summary>
    static Geometry? Arc(Point center, double radius, double fraction)
    {
        if (fraction <= 0)
            return null;
        if (fraction >= 1)
            return new EllipseGeometry(new Rect(center.X - radius, center.Y - radius, 2 * radius, 2 * radius));
        double angle = fraction * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(center.X, center.Y - radius), false);
            g.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)), new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        return geometry;
    }

    static void Place(Canvas canvas, Control control, double left, double top)
    {
        Canvas.SetLeft(control, left);
        Canvas.SetTop(control, top);
        canvas.Children.Add(control);
    }

    static IBrush Solid(uint argb) =>
        new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)).ToImmutable();
}
