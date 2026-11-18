using Avalonia.Threading;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Settings;
using Rondel.MacOS.Interop;
using Rondel.MacOS.Trigger;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS;

/// <summary>
/// The keyboard shortcuts, as on Windows: the menu at the mouse cursor, Ask About a Screenshot, the menu with its
/// Active Agents arc open, and the agents card.
/// </summary>
internal sealed partial class AppHost
{
    const int MenuShortcut = 1, ScreenshotShortcut = 2, AgentsShortcut = 3, CardShortcut = 4;

    /// <summary>The shortcuts as the settings have them, by id.</summary>
    readonly Dictionary<int, string> _shortcuts = [];
    readonly Dictionary<int, string> _shortcutErrors = [];
    bool _shortcutsStarted, _shortcutsPaused;

    /// <summary>Why the menu's shortcut is not active, or null.</summary>
    public string? HotkeyError => _shortcutErrors.GetValueOrDefault(MenuShortcut);

    /// <summary>Why the Ask About a Screenshot shortcut is not active, or null.</summary>
    public string? ScreenshotHotkeyError => _shortcutErrors.GetValueOrDefault(ScreenshotShortcut);

    /// <summary>Why the Active Agents shortcut is not active, or null.</summary>
    public string? AgentsHotkeyError => _shortcutErrors.GetValueOrDefault(AgentsShortcut);

    /// <summary>Why the agents card's shortcut is not active, or null.</summary>
    public string? CardHotkeyError => _shortcutErrors.GetValueOrDefault(CardShortcut);

    /// <summary>
    /// Registers the shortcuts once Rondel runs, not when the host is built: the developer renders build a host from
    /// the user's settings and must not take the running Rondel's shortcuts.
    /// </summary>
    void StartShortcuts()
    {
        HotKeys.Pressed += id =>
        {
            if (id == MenuShortcut)
                OnMenuShortcut();
            else if (id == ScreenshotShortcut)
                AskAboutScreenshot();
            else if (id == AgentsShortcut)
                OnAgentsShortcut();
            else if (id == CardShortcut)
                OnCardShortcut();
        };
        _shortcutsStarted = true;
        foreach (int id in _shortcuts.Keys)
            Register(id);
    }

    /// <summary>Registers the shortcuts that changed in the settings.</summary>
    void ApplyShortcuts(AppSettings settings)
    {
        var changed = new List<int>();
        Apply(MenuShortcut, settings.Hotkey);
        Apply(ScreenshotShortcut, settings.Quick.ScreenshotHotkey);
        Apply(AgentsShortcut, settings.ActiveAgents.Hotkey);
        // The card's, as on Windows, only while the card is turned on.
        var card = settings.ActiveAgents.Card;
        Apply(CardShortcut, card.Show ? card.Hotkey : "");
        if (changed.Count == 0 || !_shortcutsStarted || _shortcutsPaused)
            return;
        // All the changed ones let go first, so a combination moving from one shortcut to another is free for it; one that
        // another of Rondel's shortcuts held may be free now too.
        foreach (int id in changed)
            HotKeys.Unregister(id);
        foreach (int id in changed.Union(_shortcutErrors.Keys).Order().ToList())
            Register(id);

        void Apply(int id, string text)
        {
            if (_shortcuts.TryGetValue(id, out string? old) && old == text)
                return;
            _shortcuts[id] = text;
            changed.Add(id);
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
        // macOS refuses a combination this app already holds: say so, rather than blaming another app.
        if (_shortcuts.Any(other => other.Key != id && HotKeys.IsRegistered(other.Key)
            && Hotkey.TryParse(other.Value, out var held) && held == hotkey))
        {
            Log.Warn($"Shortcut {hotkey} ({ShortcutName(id)}) is already another of Rondel's");
            _shortcutErrors[id] = Loc.Get("Settings.Hotkey.Taken.Mac");
            return;
        }
        if (!HotKeys.Register(id, hotkey))
            _shortcutErrors[id] = Loc.Get("Settings.Hotkey.Failed");
        else
            Log.Info($"Shortcut {hotkey} registered ({ShortcutName(id)})");
    }

    static string ShortcutName(int id) => id switch
    {
        MenuShortcut => "menu",
        ScreenshotShortcut => "screenshot",
        AgentsShortcut => "Active Agents",
        CardShortcut => "agents card",
        _ => id.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

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

    /// <summary>
    /// Opens the desktop menu at the mouse cursor with its Active Agents arc open, wherever the cursor is; pressed again,
    /// closes it.
    /// </summary>
    void OnAgentsShortcut()
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
        var (model, slice) = _flow.AgentsMenu();
        ShowModel(model, CursorLocation(), hover: slice);
    }

    /// <summary>Hides the agents card, or shows it, also with no agent: "Hide the card" on the card does the same.</summary>
    void OnCardShortcut()
    {
        _card?.Toggle();
        UpdateCardMenu();
    }

    /// <summary>The card's shortcut as its options show it next to "Hide the card", while it is active.</summary>
    string? CardShortcutText() =>
        HotKeys.IsRegistered(CardShortcut) && _shortcuts.TryGetValue(CardShortcut, out string? text) ? Hotkey.Display(text) : null;
}
