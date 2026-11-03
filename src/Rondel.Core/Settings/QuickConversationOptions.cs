namespace Rondel.Core.Settings;

/// <summary>Quick Conversation: a session that leaves no trace (see <see cref="Menus.ActionDefinition.Ephemeral"/>).</summary>
public sealed class QuickConversationOptions
{
    /// <summary>Ask for the first message on the prompt card; off, the agent opens right away and the user types in its terminal.</summary>
    public bool PromptCard { get; set; } = true;

    /// <summary>The shortcut of Ask About a Screenshot, e.g. "Ctrl+Alt+S"; none by default (it is also in the tray menu).</summary>
    public string ScreenshotHotkey { get; set; } = "";
}
