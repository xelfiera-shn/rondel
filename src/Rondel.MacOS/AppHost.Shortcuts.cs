using Avalonia.Threading;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.MacOS.Interop;
using Rondel.MacOS.Trigger;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS;

/// <summary>The keyboard shortcut that opens the menu at the mouse cursor, as on Windows.</summary>
internal sealed partial class AppHost
{
    const int MenuShortcut = 1;

    string? _menuShortcut;
    bool _shortcutsStarted, _shortcutsPaused;

    /// <summary>Why the menu's shortcut is not active, or null.</summary>
    public string? HotkeyError { get; private set; }

    /// <summary>
    /// Registers the shortcuts once Rondel runs, not when the host is built: the developer renders build a host from
    /// the user's settings and must not take the running Rondel's shortcut.
    /// </summary>
    void StartShortcuts()
    {
        HotKeys.Pressed += id =>
        {
            if (id == MenuShortcut)
                OnMenuShortcut();
        };
        _shortcutsStarted = true;
        RegisterShortcuts();
    }

    /// <summary>Registers the menu's shortcut when it changed in the settings.</summary>
    void ApplyShortcuts(string menu)
    {
        if (menu == _menuShortcut)
            return;
        _menuShortcut = menu;
        if (_shortcutsStarted && !_shortcutsPaused)
            RegisterShortcuts();
    }

    void RegisterShortcuts()
    {
        HotkeyError = null;
        HotKeys.Unregister(MenuShortcut);
        string text = _menuShortcut ?? "";
        if (!Hotkey.TryParse(text, out var hotkey))
        {
            if (!string.IsNullOrWhiteSpace(text))
                Log.Warn($"Shortcut '{text}' could not be parsed");
            return;
        }
        if (!HotKeys.Register(MenuShortcut, hotkey))
            HotkeyError = Loc.Get("Settings.Hotkey.Failed");
        else
            Log.Info($"Shortcut {hotkey} registered");
    }

    /// <summary>
    /// While Settings records a new shortcut, the current ones are let go, so pressing one records it instead of
    /// opening the menu.
    /// </summary>
    public void PauseShortcuts(bool paused)
    {
        if (paused == _shortcutsPaused)
            return;
        _shortcutsPaused = paused;
        if (paused)
            HotKeys.Unregister(MenuShortcut);
        else
            RegisterShortcuts();
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
        var point = CursorLocation();
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
