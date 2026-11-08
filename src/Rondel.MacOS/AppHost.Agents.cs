using Avalonia.Threading;
using Rondel.Core.Activity;
using Rondel.Core.Logging;
using Rondel.MacOS.Interop;
using Rondel.MacOS.Launch;

namespace Rondel.MacOS;

/// <summary>Following the agents Rondel starts, as on Windows: notifications, and bringing an agent's terminal to the front.</summary>
internal sealed partial class AppHost
{
    /// <summary>How often closed terminal tabs are looked for (an agent whose tab was closed sends no SessionEnd).</summary>
    static readonly TimeSpan ClosedTabsInterval = TimeSpan.FromSeconds(15);

    readonly AgentActivity _agents = new();
    readonly AgentTerminals _agentTerminals = new();
    HookServer? _hooks;
    DispatcherTimer? _closedTabs;

    /// <summary>The agents Rondel follows (for the menu).</summary>
    public AgentActivity Activity => _agents;

    void StartFollowingAgents()
    {
        _agents.Changed += change => Dispatcher.UIThread.Post(() => OnAgentChanged(change));
        _hooks = new HookServer(_agents);
        if (!_hooks.Start(Store.DirectoryPath))
            return;
        _closedTabs = new DispatcherTimer(ClosedTabsInterval, DispatcherPriority.Background, (_, _) => ForgetClosedTabs());
        _closedTabs.Start();
    }

    void StopFollowingAgents()
    {
        _closedTabs?.Stop();
        _hooks?.Dispose();
    }

    /// <summary>Forgets, off the main thread, the agents whose terminal tab is gone.</summary>
    void ForgetClosedTabs()
    {
        if (_agents.Agents.Count == 0)
            return;
        var ids = _agents.Agents.Select(a => a.Id).ToList();
        Task.Run(() => _agentTerminals.Closed(ids)).ContinueWith(closed => Dispatcher.UIThread.Post(() =>
        {
            foreach (string id in closed.Result)
            {
                Log.Info($"Agent {id}: its terminal tab was closed");
                _agentTerminals.Forget(id);
                _agents.Forget(id);
            }
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>
    /// Brings the agent's terminal tab to the front, off the main thread (macOS may ask for the Automation permission
    /// first); forgets the agent when its tab is gone.
    /// </summary>
    public void FocusAgent(string id) =>
        Task.Run(() => _agentTerminals.Focus(id) || _agentTerminals.Closed([id]).Count == 0).ContinueWith(open =>
        {
            if (open.Result)
                return;
            Log.Info($"Agent {id}: its terminal tab is gone");
            Dispatcher.UIThread.Post(() =>
            {
                _agentTerminals.Forget(id);
                _agents.Forget(id);
            });
        }, TaskContinuationOptions.OnlyOnRanToCompletion);

    void OnAgentChanged(AgentChange change)
    {
        if (change.Ended)
        {
            _agentTerminals.Forget(change.Agent.Id);
            return;
        }
        if (change.Notice is not { } notice)
            return;
        var options = Store.Current.ActiveAgents;
        if (!(notice == AgentNotice.Asks ? options.NotifyAsks : options.NotifyFinished))
            return;
        if (_agentTerminals.IsForeground(change.Agent.Id))
        {
            Log.Debug($"Agent {change.Agent.Id}: {notice} not shown, its terminal is in front");
            return;
        }
        var (title, text) = AgentNotices.Describe(change.Agent, notice);
        string agent = change.Agent.Id;
        Notifications.Show(title, text, () => FocusAgent(agent));
    }
}
