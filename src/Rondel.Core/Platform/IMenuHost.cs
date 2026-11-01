using Rondel.Core.Flow;

namespace Rondel.Core.Platform;

/// <summary>What <see cref="MenuFlow"/> needs from the platform's menu and tray. Called on the UI thread.</summary>
public interface IMenuHost
{
    /// <param name="restoreFocus">Give the focus back to the window that had it (nothing was started).</param>
    void CloseMenu(bool restoreFocus);

    /// <summary>Replaces the menu with the inline card that asks for a folder name (and a prompt).</summary>
    void StartInput(InputSpec spec);

    /// <summary>Replaces the menu with the New Project card: a folder name, start options and templates, and a prompt.</summary>
    void StartNewProject(NewProjectSpec spec);

    /// <summary>Tells the user about a problem outside the menu (a notification or tray balloon).</summary>
    void ShowWarning(string message);

    /// <summary>Brings the terminal of a followed agent (<see cref="Activity.ActiveAgent.Id"/>) to the front.</summary>
    void FocusAgent(string id);
}
