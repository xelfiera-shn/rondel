using Avalonia.Threading;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Settings;
using Rondel.MacOS.Interop;
using Rondel.MacOS.Trigger;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS;

/// <summary>The keyboard shortcuts: the menu at the mouse cursor, and Ask About a Screenshot, as on Windows.</summary>
internal sealed partial class AppHost
{
    const int MenuShortcut = 1, ScreenshotShortcut = 2;

    /// <summary>The shortcuts as the settings have them, by id.</summary>
    readonly Dictionary<int, string> _shortcuts = [];
    readonly Dictionary<int, string> _shortcutErrors = [];
    bool _shortcutsPaused;

    /// <summary>Why the menu's shortcut is not active, or null.</summary>
    public string? HotkeyError => _shortcutErrors.GetValueOrDefault(MenuShortcut);

    /// <summary>Why the Ask About a Screenshot shortcut is not active, or null.</summary>
    public string? ScreenshotHotkeyError => _shortcutErrors.GetValueOrDefault(ScreenshotShortcut);

    void StartShortcuts() => HotKeys.Pressed += id =>
    {
        if (id == MenuShortcut)
            OnMenuShortcut();
        else if (id == ScreenshotShortcut)
            AskAboutScreenshot();
    };

    /// <summary>Registers the shortcuts that changed in the settings.</summary>
    void ApplyShortcuts(AppSettings settings)
    {
        Apply(MenuShortcut, settings.Hotkey);
        Apply(ScreenshotShortcut, settings.Quick.ScreenshotHotkey);

        void Apply(int id, string text)
        {
            if (_shortcuts.TryGetValue(id, out string? old) && old == text)
                return;
            _shortcuts[id] = text;
            if (!_shortcutsPaused)
                Register(id);
        }
    }

    void Register(int id)
    {
        _shortcutErrors.Remove(id);
        HotKeys.Unregister(id);
        string text = _shortcuts.GetValueOrDefault(id) ?? "";
        if (!Hotkey.TryParse(text, out var hotkey))
        {
            if (!string.IsNullOrWhiteSpace(text))
                Log.Warn($"Shortcut '{text}' could not be parsed");
            return;
        }
        if (!HotKeys.Register(id, hotkey))
            _shortcutErrors[id] = Loc.Get("Settings.Hotkey.Failed");
        else
            Log.Info($"Shortcut {hotkey} registered ({(id == MenuShortcut ? "menu" : "screenshot")})");
    }

    /// <summary>
    /// While Settings records a new shortcut, the current ones are let go, so pressing one records it instead of
    /// doing what it does.
    /// </summary>
    public void PauseShortcuts(bool paused)
    {
        if (paused == _shortcutsPaused)
            return;
        _shortcutsPaused = paused;
        foreach (int id in _shortcuts.Keys)
        {
            if (paused)
                HotKeys.Unregister(id);
            else
                Register(id);
        }
    }

    /// <summary>Opens the menu at the mouse cursor for what is under it; pressed again, closes it.</summary>
    void OnMenuShortcut()
    {
        if (_menu is null)
            return;
        if (_menu.IsMenuOpen)
        {
            _menu.CloseMenu(restoreFocus: true);
            return;
        }
        if (Store.Current.Paused)
            return;
        nint where = CGEventCreate(0);
        var point = CGEventGetLocation(where);
        CFRelease(where);
        _worker.Post(() =>
        {
            var context = Detect(point, "shortcut");
            // Nothing useful under the cursor: offer the desktop actions for the projects folder.
            if (context.Kind == ContextKind.None)
                context = _flow.ProjectsContext(ContextKind.DesktopEmpty, "shortcut fallback");
            Dispatcher.UIThread.Post(() => ShowMenu(context, point));
        });
    }
}
