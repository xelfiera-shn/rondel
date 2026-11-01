using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Rondel.Core.Localization;

namespace Rondel.MacOS.UI;

/// <summary>What the menu bar extra shows; the menu is rebuilt from it whenever it changes.</summary>
internal sealed record MenuBarState(bool Paused, bool NeedsAccessibility, bool NeedsFinderAccess, string? Warning);

/// <summary>
/// Rondel's menu bar extra, the counterpart of the Windows tray icon: a template image (black, tinted by macOS for
/// light and dark menu bars) and a menu. There is no Dock icon (LSUIElement).
/// </summary>
internal sealed class MenuBar : IDisposable
{
    readonly TrayIcon _icon;
    readonly NativeMenuItem _warning = new() { IsEnabled = false };
    readonly NativeMenuItem _accessibility, _finderAccess, _settings, _pause, _openLogs, _quit;
    readonly NativeMenuItemSeparator _permissionsSeparator = new(), _quitSeparator = new();

    /// <summary>
    /// One menu for the app's lifetime: Avalonia's macOS menu bar refuses a replaced menu (and does not apply
    /// IsVisible), so its items are put back in on every change.
    /// </summary>
    readonly NativeMenu _menu = [];
    MenuBarState _state = new(false, false, false, null);
    bool? _pausedIcon;

    public MenuBar()
    {
        _accessibility = Item(() => AccessibilityClicked?.Invoke());
        _finderAccess = Item(() => FinderAccessClicked?.Invoke());
        _settings = Item(() => SettingsClicked?.Invoke());
        _pause = Item(() => PauseClicked?.Invoke());
        _openLogs = Item(() => OpenLogsClicked?.Invoke());
        _quit = Item(() => QuitClicked?.Invoke());
        _icon = new TrayIcon { Menu = _menu };
        MacOSProperties.SetIsTemplateIcon(_icon, true);
        Loc.Changed += Refresh;
    }

    public event Action? AccessibilityClicked;
    public event Action? FinderAccessClicked;
    public event Action? SettingsClicked;
    public event Action? PauseClicked;
    public event Action? OpenLogsClicked;
    public event Action? QuitClicked;

    public void Show(MenuBarState state)
    {
        _state = state;
        Refresh();
        TrayIcon.SetIcons(Application.Current!, [_icon]);
    }

    public void Update(Func<MenuBarState, MenuBarState> change)
    {
        var state = change(_state);
        if (state == _state)
            return;
        _state = state;
        Refresh();
    }

    void Refresh()
    {
        var s = _state;
        if (_pausedIcon != s.Paused)
        {
            _pausedIcon = s.Paused;
            _icon.Icon = RenderIcon(s.Paused);
        }
        _icon.ToolTipText = Loc.Get(s.Paused ? "Tray.TooltipPaused" : "MacMenu.Tooltip");
        _warning.Header = "⚠︎ " + s.Warning;
        _accessibility.Header = Loc.Get("MacMenu.AllowAccessibility");
        _finderAccess.Header = Loc.Get("MacMenu.AllowFinder");
        _settings.Header = Loc.Get("Tray.Settings");
        _pause.Header = Loc.Get(s.Paused ? "Tray.Resume" : "Tray.Pause");
        _openLogs.Header = Loc.Get("Tray.OpenLogs");
        _quit.Header = Loc.Get("MacMenu.Quit");

        NativeMenuItemBase?[] items =
        [
            s.Warning is null ? null : _warning,
            s.NeedsAccessibility ? _accessibility : null,
            s.NeedsFinderAccess ? _finderAccess : null,
        ];
        _menu.Items.Clear();
        foreach (var item in items.OfType<NativeMenuItemBase>())
            _menu.Items.Add(item);
        if (_menu.Items.Count > 0)
            _menu.Items.Add(_permissionsSeparator);
        foreach (var item in new NativeMenuItemBase[] { _settings, _pause, _openLogs, _quitSeparator, _quit })
            _menu.Items.Add(item);
    }

    static NativeMenuItem Item(Action click)
    {
        var item = new NativeMenuItem();
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>
    /// The app icon's shape as a template image: four thick ring segments around a hub, the top one lifted outward
    /// like a selected slice; paused, the same shape faded. It fills the menu bar's 18 points (drawn at 2x).
    /// </summary>
    internal static WindowIcon RenderIcon(bool paused)
    {
        const int Px = 36;
        const double C = Px / 2.0, Thickness = Px * 0.16, Radius = Px * 0.40 - Thickness / 2, Lift = Px * 0.06;
        var bitmap = new RenderTargetBitmap(new PixelSize(Px, Px), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            byte alpha = paused ? (byte)0x70 : (byte)0xFF;
            for (int i = 0; i < 4; i++)
            {
                double mid = i * 90, r = i == 0 ? Radius + Lift : Radius;
                // The other segments slightly lighter, as on Windows, so the lifted one reads as the selection.
                var ink = new SolidColorBrush(Color.FromArgb(i == 0 ? alpha : (byte)(alpha * 0.78), 0, 0, 0));
                var arc = new StreamGeometry();
                using (var g = arc.Open())
                {
                    g.BeginFigure(OnCircle(C, r, mid - 27), false);
                    g.ArcTo(OnCircle(C, r, mid + 27), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(ink, Thickness, lineCap: PenLineCap.Round), arc);
            }
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)), null, new Point(C, C), Px * 0.11, Px * 0.11);
        }
        var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    static Point OnCircle(double center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(center + radius * Math.Sin(rad), center - radius * Math.Cos(rad));
    }

    public void Dispose()
    {
        Loc.Changed -= Refresh;
        _icon.Dispose();
    }
}
