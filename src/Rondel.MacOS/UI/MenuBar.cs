using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Rondel.Core.Localization;

namespace Rondel.MacOS.UI;

/// <summary>What the menu bar extra shows; the menu is rebuilt from it whenever it changes.</summary>
/// <param name="Update">The version a newer release has, offered at the top of the menu; null for none.</param>
/// <param name="ShowCard">"Show the agents card": null leaves it out, false shows it greyed (the card is on screen).</param>
internal sealed record MenuBarState(bool Paused, bool NeedsAccessibility, bool NeedsFinderAccess, string? Warning, string? Update = null,
    bool? ShowCard = null);

/// <summary>
/// Rondel's menu bar extra, the counterpart of the Windows tray icon: a template image (black, tinted by macOS for
/// light and dark menu bars) and a menu. There is no Dock icon (LSUIElement).
/// </summary>
internal sealed class MenuBar : IDisposable
{
    readonly TrayIcon _icon;
    readonly NativeMenuItem _warning = new() { IsEnabled = false };
    readonly NativeMenuItem _update, _accessibility, _finderAccess, _screenshot, _settings, _sessions, _howToUse, _showCard, _pause, _openLogs, _quit;
    readonly NativeMenuItemSeparator _permissionsSeparator = new(), _screenshotSeparator = new(), _quitSeparator = new();

    /// <summary>
    /// One menu for the app's lifetime: Avalonia's macOS menu bar refuses a replaced menu (and does not apply
    /// IsVisible), so its items are put back in on every change.
    /// </summary>
    readonly NativeMenu _menu = [];
    MenuBarState _state = new(false, false, false, null);
    bool? _pausedIcon;

    public MenuBar()
    {
        _update = Item(() => UpdateClicked?.Invoke());
        _accessibility = Item(() => AccessibilityClicked?.Invoke());
        _finderAccess = Item(() => FinderAccessClicked?.Invoke());
        _screenshot = Item(() => ScreenshotClicked?.Invoke());
        _settings = Item(() => SettingsClicked?.Invoke());
        _sessions = Item(() => SessionsClicked?.Invoke());
        _howToUse = Item(() => HowToUseClicked?.Invoke());
        _showCard = Item(() => ShowCardClicked?.Invoke());
        _pause = Item(() => PauseClicked?.Invoke());
        _openLogs = Item(() => OpenLogsClicked?.Invoke());
        _quit = Item(() => QuitClicked?.Invoke());
        _icon = new TrayIcon { Menu = _menu };
        MacOSProperties.SetIsTemplateIcon(_icon, true);
        Loc.Changed += Refresh;
    }

    public event Action? AccessibilityClicked;
    public event Action? FinderAccessClicked;
    public event Action? ScreenshotClicked;
    public event Action? SettingsClicked;

    public event Action? SessionsClicked;

    public event Action? HowToUseClicked;

    public event Action? ShowCardClicked;
    public event Action? PauseClicked;
    public event Action? OpenLogsClicked;
    public event Action? QuitClicked;

    public event Action? UpdateClicked;

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
        _update.Header = Loc.Format("Tray.Update", s.Update ?? "");
        _accessibility.Header = Loc.Get("MacMenu.AllowAccessibility");
        _finderAccess.Header = Loc.Get("MacMenu.AllowFinder");
        _screenshot.Header = Loc.Get("Tray.Screenshot");
        _settings.Header = Loc.Get("Tray.Settings");
        _sessions.Header = Loc.Get("Tray.Sessions");
        _howToUse.Header = Loc.Get("Tray.HowToUse");
        _showCard.Header = Loc.Get("Tray.ShowCard");
        _showCard.IsEnabled = s.ShowCard == true;
        _pause.Header = Loc.Get(s.Paused ? "Tray.Resume" : "Tray.Pause");
        _openLogs.Header = Loc.Get("Tray.OpenLogs");
        _quit.Header = Loc.Get("MacMenu.Quit");

        NativeMenuItemBase?[] items =
        [
            s.Warning is null ? null : _warning,
            s.Update is null ? null : _update,
            s.NeedsAccessibility ? _accessibility : null,
            s.NeedsFinderAccess ? _finderAccess : null,
        ];
        _menu.Items.Clear();
        foreach (var item in items.OfType<NativeMenuItemBase>())
            _menu.Items.Add(item);
        if (_menu.Items.Count > 0)
            _menu.Items.Add(_permissionsSeparator);
        foreach (var item in new NativeMenuItemBase?[] { _screenshot, _screenshotSeparator, _settings, _sessions, _howToUse, s.ShowCard is null ? null : _showCard, _pause, _openLogs, _quitSeparator, _quit })
        {
            if (item is not null)
                _menu.Items.Add(item);
        }
    }

    static NativeMenuItem Item(Action click)
    {
        var item = new NativeMenuItem();
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>
    /// The app icon's shape (<see cref="AppIcon"/>) as a template image: black, which macOS tints for light and dark
    /// menu bars; paused, faded. It fills the menu bar's 18 points (drawn at 2x).
    /// </summary>
    internal static WindowIcon RenderIcon(bool paused)
    {
        const int Px = 36;
        var bitmap = new RenderTargetBitmap(new PixelSize(Px, Px), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushOpacity(paused ? 0.44 : 1))
            AppIcon.DrawMark(context, default, Px, Brushes.Black);
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
