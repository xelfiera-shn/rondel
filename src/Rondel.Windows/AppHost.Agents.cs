using System.Windows.Threading;
using Rondel.Core;
using Rondel.Core.Activity;
using Rondel.Core.Logging;
using Rondel.Windows.Launch;

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
        ShowBalloon(title, text, warning: notice == AgentNotice.Failed, agentId: change.Agent.Id);
    }

    /// <param name="agentId">The agent a click on the notification brings to the front, if any.</param>
    void ShowBalloon(string title, string text, bool warning, string? agentId = null)
    {
        _balloonAgent = agentId;
        _tray.ShowBalloon(title, text, warning);
    }
}
