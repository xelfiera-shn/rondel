using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Menus;
using Rondel.Core.Settings;
using Rondel.Core.Themes;

namespace Rondel.Windows.UI;

/// <summary>
/// Shown once, the first time Rondel runs, when nothing else would show that it runs: how the menu opens, played in a
/// loop (the pointer goes to an empty spot in a folder, Ctrl and the left button are held, the menu opens, the pointer
/// moves onto a slice and lets go, and the agent starts), with the three steps lit in turn. The menu in the picture is
/// drawn by the menu itself, in the user's theme and language.
/// </summary>
public partial class WelcomeWindow : Window
{
    /// <summary>One play of the gesture, in seconds.</summary>
    internal const double Period = 7.2;

    /// <summary>What is shown when Windows' animations are off: the menu open, a slice under the pointer.</summary>
    internal const double StillMoment = 3.95;

    const double MenuScale = 0.6;
    const double SliceRadius = 132; // with the angle below: under the slice's label, so the pointer does not cover it

    static readonly Point Start = new(118, 238);
    static readonly Point Press = new(236, 150);

    readonly Point _slice;
    readonly Brush _accent;
    readonly Brush _keyFill, _keyBorder, _keyText, _idleText, _activeText;
    readonly List<(Border Badge, TextBlock Number, TextBlock Text)> _steps = [];
    readonly Stopwatch _clock = new();
    bool _playing;

    /// <param name="mode">Light or dark; the system's by default (developer aids fix it).</param>
    internal WelcomeWindow(AppearanceOptions appearance, ThemeColors theme, ThemeMode? mode = null)
    {
        ThemeMode = mode ?? ThemeMode.System; // Fluent styles, like Settings
        InitializeComponent();
        Icon = AppIconRenderer.Render(64);
        HeaderIcon.Source = AppIconRenderer.Render(72);
        FluentBrand.Apply(this, theme);
        BrandLine.Background = FluentBrand.Line(theme);

        bool dark = theme.IsDark;
        var palette = RondelPalette.For(appearance, theme);
        _accent = palette.Accent;
        _keyFill = Solid(dark ? 0x323843 : 0xFFFFFF);
        _keyBorder = Solid(dark ? 0x4A5260 : 0xC5CDD8);
        _keyText = Solid(dark ? 0xE8EAED : 0x1F2329);
        _idleText = (Brush)FindResource("TextFillColorSecondaryBrush");
        _activeText = (Brush)FindResource("TextFillColorPrimaryBrush");

        // The scene: a folder window on the desktop.
        Stage.Background = Solid(dark ? 0x1A1E25 : 0xDDE4ED);
        Folder.Background = Solid(dark ? 0x262B33 : 0xFFFFFF);
        Folder.BorderBrush = Solid(dark ? 0x353C47 : 0xC9D1DC);
        FolderBar.Background = Solid(dark ? 0x2E343E : 0xF0F2F5);
        FolderGlyph.Foreground = Solid(0xF2C14E);
        FolderName.Foreground = _keyText;
        foreach (var (glyph, name, color) in new[] { ("\uE8B7", "src", 0xF2C14E), ("\uE8A5", "README.md", dark ? 0x9AA3AE : 0x5F6B78),
            ("\uE8A5", "package.json", dark ? 0x9AA3AE : 0x5F6B78) })
            FolderItems.Children.Add(Item(glyph, name, Solid(color)));
        TerminalTitle.Text = "weather-api";
        TerminalText.Text = @"PS C:\Users\dev\Projects\weather-api> claude";
        Plus.Foreground = _idleText;
        MouseBody.BorderBrush = _keyBorder;
        MouseBody.Background = _keyFill;
        MouseSplit.Background = _keyBorder;
        HoldRing.Stroke = _accent;
        Ripple.Stroke = _accent;

        // The menu of a folder window, drawn by the menu itself: as it opens, and with Open Here under the pointer.
        var settings = new AppSettings();
        settings.Normalize();
        settings.Appearance = appearance;
        string folder = Path.Combine(settings.ResolveProjectsRoot(), "weather-api");
        var model = MenuBuilder.Build(new TriggerContext(ContextKind.ExplorerEmpty, folder, "weather-api", "welcome"), settings);
        int open = Math.Max(0, model.Items.ToList().FindIndex(i => i.Action.Id == "open-here"));
        var hints = model.Items.Select(_ => ((string?)null, false)).ToList();
        var children = model.Items.Select(_ => (IReadOnlyList<MenuItemModel>)[]).ToList();
        var menu = new RadialMenuWindow();
        try
        {
            MenuIdle.Source = menu.RenderPreview(model, palette, hints, children, [], Colors.Transparent);
            MenuHot.Source = menu.RenderPreview(model, palette, hints, children, [open], Colors.Transparent);
        }
        finally
        {
            menu.Close();
        }
        foreach (var image in new[] { MenuIdle, MenuHot })
        {
            image.Width = image.Source.Width * MenuScale;
            image.Height = image.Source.Height * MenuScale;
            Canvas.SetLeft(image, Press.X - image.Width / 2);
            Canvas.SetTop(image, Press.Y - image.Height / 2);
        }
        int n = model.Items.Count;
        double angle = ((open + 0.3) * 360.0 / n + (n == 2 ? -90 : 0)) * Math.PI / 180;
        _slice = new Point(Press.X + SliceRadius * MenuScale * Math.Sin(angle), Press.Y - SliceRadius * MenuScale * Math.Cos(angle));

        foreach (string key in new[] { "Welcome.Step1", "Welcome.Step2", "Welcome.Step3" })
            AddStep(_steps.Count + 1, Loc.Get(key));

        Loaded += (_, _) => Play();
        Closed += (_, _) => StopPlaying();
        ShowMoment(0);
    }

    /// <summary>Open Settings was chosen; the window closes itself.</summary>
    internal event Action? SettingsRequested;

    /// <summary>Plays the gesture in a loop, or shows one moment of it when Windows' animations are off.</summary>
    void Play()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            ShowMoment(StillMoment);
            return;
        }
        _clock.Restart();
        if (!_playing)
            CompositionTarget.Rendering += OnFrame;
        _playing = true;
    }

    /// <summary>Stops the loop where it is (developer aids draw chosen moments).</summary>
    internal void StopPlaying()
    {
        if (_playing)
            CompositionTarget.Rendering -= OnFrame;
        _playing = false;
    }

    void OnFrame(object? sender, EventArgs e) => ShowMoment(_clock.Elapsed.TotalSeconds);

    /// <summary>Puts every part of the picture where it is <paramref name="seconds"/> into the loop.</summary>
    internal void ShowMoment(double seconds)
    {
        double t = seconds % Period;
        double Seg(double from, double to) => Math.Clamp((t - from) / (to - from), 0, 1);
        static double Ease(double x) => x * x * (3 - 2 * x);
        static Point Lerp(Point a, Point b, double k) => new(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);

        // 0.4–1.2 s to an empty spot; Ctrl at 1.4, the button at 1.9, held until the menu opens at 2.7;
        // 3.2–3.9 s onto Open Here, let go at 4.4; the terminal at 4.7; everything fades from 6.5.
        double fade = 1 - Seg(6.5, 6.9);
        var pointer = t < 3.2 ? Lerp(Start, Press, Ease(Seg(0.4, 1.2))) : Lerp(Press, _slice, Ease(Seg(3.2, 3.9)));
        Canvas.SetLeft(Pointer, pointer.X);
        Canvas.SetTop(Pointer, pointer.Y);
        Pointer.Opacity = Seg(0, 0.3) * fade;

        bool held = t >= 1.9 && t < 4.4;
        SetKey(t >= 1.4 && t < 4.4);
        MouseLeft.Background = held ? _accent : Brushes.Transparent;

        double hold = Seg(1.9, 2.7);
        HoldRing.Data = t >= 1.9 && t < 2.9 ? Arc(Press, 15, hold) : null;
        HoldRing.Opacity = 1 - Seg(2.7, 2.9);
        double ripple = Seg(1.9, 2.35);
        double size = 8 + 26 * ripple;
        Ripple.Width = Ripple.Height = size;
        Canvas.SetLeft(Ripple, Press.X - size / 2);
        Canvas.SetTop(Ripple, Press.Y - size / 2);
        Ripple.Opacity = t >= 1.9 && t < 2.35 ? 0.7 * (1 - ripple) : 0;

        double opened = Ease(Seg(2.7, 3.0));
        double shown = opened * (1 - Seg(4.5, 4.8));
        double hot = Seg(3.55, 3.7);
        MenuIdle.Opacity = shown * (1 - hot);
        MenuHot.Opacity = shown * hot;
        foreach (var scale in new[] { MenuIdleScale, MenuHotScale })
            scale.ScaleX = scale.ScaleY = 0.86 + 0.14 * opened;

        double terminal = Seg(4.7, 5.0);
        Terminal.Opacity = terminal * fade;
        TerminalScale.ScaleX = TerminalScale.ScaleY = 0.94 + 0.06 * Ease(terminal);

        int step = t < 1.4 ? -1 : t < 1.9 ? 0 : t < 3.2 ? 1 : t < 4.6 ? 2 : -1;
        for (int i = 0; i < _steps.Count; i++)
        {
            var (badge, number, text) = _steps[i];
            bool active = i == step;
            badge.Background = active ? _accent : Brushes.Transparent;
            badge.BorderBrush = active ? _accent : _idleText;
            number.Foreground = active ? (Brush)FindResource("TextOnAccentFillColorPrimaryBrush") : _idleText;
            text.Foreground = active ? _activeText : _idleText;
        }
    }

    void SetKey(bool down)
    {
        CtrlKey.Background = down ? _accent : _keyFill;
        CtrlKey.BorderBrush = down ? _accent : _keyBorder;
        CtrlKey.BorderThickness = new Thickness(1, 1, 1, down ? 1 : 3);
        CtrlKey.Margin = new Thickness(0, down ? 2 : 0, 0, 0);
        CtrlText.Foreground = down ? (Brush)FindResource("TextOnAccentFillColorPrimaryBrush") : _keyText;
    }

    void AddStep(int number, string text)
    {
        var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5) };
        var digit = new TextBlock
        {
            Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.Child = digit;
        var label = new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0), FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(label);
        Steps.Children.Add(row);
        _steps.Add((badge, digit, label));
    }

    static FrameworkElement Item(string glyph, string name, Brush color)
    {
        var item = new StackPanel { Width = 84, Margin = new Thickness(0, 0, 8, 0) };
        item.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 28,
            Foreground = color,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var label = new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        item.Children.Add(label);
        return item;
    }

    /// <summary>The part of a circle from its top, clockwise, that <paramref name="fraction"/> covers.</summary>
    static Geometry? Arc(Point center, double radius, double fraction)
    {
        if (fraction <= 0)
            return null;
        if (fraction >= 1)
            return new EllipseGeometry(center, radius, radius);
        double angle = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    static SolidColorBrush Solid(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    void OnSettings(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
        Close();
    }

    void OnStart(object sender, RoutedEventArgs e) => Close();
}
