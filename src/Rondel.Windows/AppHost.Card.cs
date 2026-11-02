using Rondel.Core;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Settings;
using Rondel.Windows.UI;

namespace Rondel.Windows;

/// <summary>The agents card: the followed agents in sight, on top of the windows or on the desktop.</summary>
internal sealed partial class AppHost
{
    /// <summary>%LOCALAPPDATA%\Rondel\agents-card.json: where the card was on each monitor (not in settings.json).</summary>
    static string CardStateFile => Path.Combine(Path.GetDirectoryName(AppInfo.LogDirectory)!, "agents-card.json");

    AgentsCard? _card;

    void StartCard()
    {
        // The card's own changes (its pin, its quick settings) are saved like any setting, so they are back after a
        // restart; an open Settings window shows them at once.
        _card = new AgentsCard(() => Store.Current.ActiveAgents.Card, change =>
        {
            Store.Update(s => change(s.ActiveAgents.Card));
            _settingsWindow?.LoadValues();
        }, CardStateFile);
        _card.AgentClicked += FocusAgent;
        _card.SettingsRequested += OpenSettings;
        _card.ApplyLook(RondelPalette.For(Store.Current.Appearance, SystemTheme.Read()));
    }

    void UpdateCard() => _card?.Update(_activity.Agents, Store.Current.ActiveAgents.Follow);

    void ApplyCard(AppSettings settings)
    {
        var card = settings.ActiveAgents.Card;
        ApplyCardHotkey(card);
        if (_card is null)
            return;
        _card.HideShortcut = card.Show && CardHotkeyError is null && !string.IsNullOrEmpty(card.Hotkey) ? card.Hotkey : null;
        _card.Update(_activity.Agents, settings.ActiveAgents.Follow);
    }

    /// <summary>The card's shortcut, registered only while the card is turned on.</summary>
    void ApplyCardHotkey(AgentsCardOptions card)
    {
        CardHotkeyError = null;
        if (!card.Show || !HotkeyBinding.TryParse(card.Hotkey, out var binding))
        {
            _tray.ClearCardHotkey();
            if (card.Show && !string.IsNullOrWhiteSpace(card.Hotkey))
                Log.Warn($"Agents card shortcut '{card.Hotkey}' could not be parsed");
            return;
        }
        if (!_tray.SetCardHotkey(binding.NativeModifiers, binding.VirtualKey))
            CardHotkeyError = Loc.Get("Settings.Hotkey.Failed");
        else
            Log.Info($"Agents card shortcut {binding} registered");
    }
}
