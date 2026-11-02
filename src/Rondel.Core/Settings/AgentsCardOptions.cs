namespace Rondel.Core.Settings;

/// <summary>Where the agents card stays.</summary>
public enum CardPlacement
{
    /// <summary>Above other windows, wherever the user works.</summary>
    OnTop,

    /// <summary>On the desktop, behind the windows, like a note stuck to it.</summary>
    OnDesktop,
}

/// <summary>
/// The agents card: a small window that keeps the agents Rondel follows in sight. It appears when the first agent
/// starts and hides when none is left.
/// </summary>
public sealed class AgentsCardOptions
{
    /// <summary>Show the card while agents run; off until the user turns it on.</summary>
    public bool Show { get; set; }

    public CardPlacement Placement { get; set; } = CardPlacement.OnTop;

    /// <summary>Stay over full-screen apps too, open (nobody can open a collapsed card while a game has the focus).</summary>
    public bool OverFullScreen { get; set; }

    /// <summary>No notifications about agents while the card is shown: its badge already says what needs the user.</summary>
    public bool MuteNotifications { get; set; } = true;

    /// <summary>Keep the card open instead of collapsing it to its badge when the mouse leaves it.</summary>
    public bool Pinned { get; set; }

    /// <summary>Shows or hides the card, e.g. "Ctrl+Alt+K"; empty = none.</summary>
    public string Hotkey { get; set; } = "";
}
