using Rondel.Core;
using Rondel.MacOS.UI;

namespace Rondel.MacOS;

/// <summary>The agents card: the followed agents in sight, above the windows or on the desktop.</summary>
internal sealed partial class AppHost
{
    /// <summary>Where the card was on each screen (not in settings.json).</summary>
    static string CardStateFile => Path.Combine(AppInfo.SettingsDirectory, "agents-card.json");

    AgentsCard? _card;

    void StartCard()
    {
        // The card's own changes (its pin, its options) are saved like any setting, so they are back after a restart;
        // an open Settings window shows them at once.
        _card = new AgentsCard(() => Store.Current.ActiveAgents.Card, change =>
        {
            Store.Update(s => change(s.ActiveAgents.Card));
            _settingsWindow?.LoadValues();
        }, Palette(Store.Current), CardStateFile);
        _card.AgentClicked += FocusAgent;
        _card.SettingsRequested += OpenSettings;
        _card.HiddenByUser += UpdateCardMenu;
        UpdateCard();
    }

    void UpdateCard()
    {
        _card?.Update(_agents.Agents, Store.Current.ActiveAgents.Follow);
        UpdateCardMenu();
    }

    /// <summary>
    /// "Show the agents card" in the menu bar menu while the card is turned on: available when it is not on screen, so a
    /// hidden card can come back.
    /// </summary>
    void UpdateCardMenu()
    {
        var settings = Store.Current.ActiveAgents;
        bool? item = _card is null || !settings.Follow || !settings.Card.Show ? null : _card.CanShowAgain;
        _menuBar.Update(s => s with { ShowCard = item });
    }
}
