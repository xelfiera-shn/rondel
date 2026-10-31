using System.Windows.Threading;
using Rondel.Core;
using Rondel.Core.Activity;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Windows.Interop;
using Rondel.Windows.Launch;
using Rondel.Windows.UI;

namespace Rondel.Windows;

/// <summary>Following the agents Rondel starts: notifications, and bringing an agent's terminal to the front.</summary>
internal sealed partial class AppHost
{
    /// <summary>How often closed terminal windows are looked for (an agent that crashed sends no SessionEnd).</summary>
    static readonly TimeSpan ClosedWindowsInterval = TimeSpan.FromSeconds(15);

    static string AgentWindowsFile => Path.Combine(Path.GetDirectoryName(AppInfo.LogDirectory)!, "agent-windows.txt");

    readonly AgentActivity _activity = new();
    readonly HookServer _hooks;
    readonly AgentWindows _agentWindows;
    DispatcherTimer? _closedWindows;

    /// <summary>The agent the last notification was about; null after any other notification.</summary>
    string? _balloonAgent;

    /// <summary>The agents Rondel follows (for the menu).</summary>
    public AgentActivity Activity => _activity;

    void StartFollowingAgents()
    {
        _activity.Changed += change => _ui.InvokeAsync(() => OnAgentChanged(change));
        _tray.BalloonClicked += () =>
        {
            if (_balloonAgent is { } id)
                FocusAgent(id);
        };
        if (!_hooks.Start(Store.DirectoryPath))
            return;
        _closedWindows = new DispatcherTimer(ClosedWindowsInterval, DispatcherPriority.Background, (_, _) =>
        {
            foreach (string id in _agentWindows.Closed())
            {
                Log.Info($"Agent {id}: its window was closed");
                _activity.Forget(id);
            }
        }, _ui);
    }

    void StopFollowingAgents()
    {
        _closedWindows?.Stop();
        _hooks.Dispose();
    }

    /// <summary>Why the Active Agents shortcut is not active, or null.</summary>
    public string? AgentsHotkeyError { get; private set; }

    void ApplyAgentsHotkey(string text)
    {
        AgentsHotkeyError = null;
        if (!HotkeyBinding.TryParse(text, out var binding))
        {
            _tray.ClearAgentsHotkey();
            if (!string.IsNullOrWhiteSpace(text))
                Log.Warn($"Active Agents shortcut '{text}' could not be parsed");
            return;
        }
        if (!_tray.SetAgentsHotkey(binding.NativeModifiers, binding.VirtualKey))
            AgentsHotkeyError = Loc.Get("Settings.Hotkey.Failed");
        else
            Log.Info($"Active Agents shortcut {binding} registered");
    }

    /// <summary>Opens the desktop menu at the mouse cursor with its Active Agents arc open; pressed again, closes it.</summary>
    void OnAgentsHotkey()
    {
        if (_menu.IsMenuOpen)
        {
            _menu.CloseMenu(restoreFocus: true);
            return;
        }
        if (Store.Current.Paused)
            return;
        var (model, slice) = _flow.AgentsMenu();
        ShowModel(model, Native.GetCursorPosition(), hover: slice);
    }

    /// <summary>Brings the agent's terminal to the front; forgets the agent when its window is gone.</summary>
    public void FocusAgent(string id)
    {
        if (_agentWindows.Focus(id))
            return;
        Log.Info($"Agent {id}: no window to bring to the front");
        if (_agentWindows.Closed().Contains(id))
            _activity.Forget(id);
    }

    void OnAgentChanged(AgentChange change)
    {
        if (change.Ended)
        {
            _agentWindows.Forget(change.Agent.Id);
            if (_balloonAgent == change.Agent.Id)
                _balloonAgent = null;
            return;
        }
        if (change.Notice is not { } notice)
            return;
        var options = Store.Current.ActiveAgents;
        if (!(notice == AgentNotice.Asks ? options.NotifyAsks : options.NotifyFinished))
            return;
        if (_agentWindows.IsForeground(change.Agent.Id))
        {
            Log.Debug($"Agent {change.Agent.Id}: {notice} not shown, its terminal is in front");
            return;
        }
        var (title, text) = AgentNotices.Describe(change.Agent, notice);
        ShowBalloon(title, text, warning: notice == AgentNotice.Failed, agentId: change.Agent.Id,
            icon: NoticeIconRenderer.For(change.Agent, notice));
    }

    /// <param name="agentId">The agent a click on the notification brings to the front, if any.</param>
    /// <param name="icon">The icon of a notification about an agent.</param>
    void ShowBalloon(string title, string text, bool warning, string? agentId = null, NoticeIcon? icon = null)
    {
        _balloonAgent = agentId;
        _tray.ShowBalloon(title, text, warning, icon);
    }
}
