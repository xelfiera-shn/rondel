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

    public MenuBar()
    {
        _accessibility = Item(() => AccessibilityClicked?.Invoke());
        _finderAccess = Item(() => FinderAccessClicked?.Invoke());
        _settings = Item(() => SettingsClicked?.Invoke());
        _pause = Item(() => PauseClicked?.Invoke());
        _openLogs = Item(() => OpenLogsClicked?.Invoke());
        _quit = Item(() => QuitClicked?.Invoke());
        _icon = new TrayIcon { Icon = RenderIcon(), Menu = _menu };
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

    /// <summary>A ring with its top slice filled, 18 points at 2x: the radial menu in a glyph.</summary>
    static WindowIcon RenderIcon()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(144, 144));
        using (var context = bitmap.CreateDrawingContext())
        {
            var ink = Brushes.Black;
            context.DrawEllipse(null, new Pen(ink, 1.6), new Point(9, 9), 6.6, 6.6);
            var slice = new StreamGeometry();
            using (var g = slice.Open())
            {
                g.BeginFigure(new Point(9, 9), true);
                g.LineTo(new Point(9 - 4.7, 9 - 4.7));
                g.ArcTo(new Point(9 + 4.7, 9 - 4.7), new Size(6.6, 6.6), 0, false, SweepDirection.Clockwise);
                g.EndFigure(true);
            }
            context.DrawGeometry(ink, null, slice);
        }
        var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        Loc.Changed -= Refresh;
        _icon.Dispose();
    }
}
