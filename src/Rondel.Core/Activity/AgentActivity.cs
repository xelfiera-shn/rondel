using System.Text;
using Rondel.Core.Logging;

namespace Rondel.Core.Activity;

/// <summary>
/// The agents Rondel started and what each is doing, kept up to date by the events their hooks report
/// (<see cref="HookServer"/>). Safe to use from any thread; <see cref="Changed"/> is raised on the thread that
/// reported the event, so the UI marshals it.
/// </summary>
public sealed class AgentActivity(TimeProvider? time = null)
{
    /// <summary>The environment variable that carries a session's <see cref="ActiveAgent.Id"/> to its hooks.</summary>
    public const string EnvironmentVariable = "RONDEL_SESSION";

    /// <summary>Longest <see cref="ActiveAgent.Message"/>; a reply is cut there, it only has to be recognized.</summary>
    public const int MaxMessageLength = 200;

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Lock _lock = new();
    readonly Dictionary<string, ActiveAgent> _agents = new(StringComparer.Ordinal);

    /// <summary>
    /// The settings file that makes an agent report to Rondel (an agent's "{hooks}"), or null while
    /// <see cref="HookServer"/> is not listening; then agents start as before and are not followed.
    /// </summary>
    public string? HooksFile { get; internal set; }

    public event Action<AgentChange>? Changed;

    /// <summary>The followed agents: those waiting for the user first, then failed, ready and working ones, newest first.</summary>
    public IReadOnlyList<ActiveAgent> Agents
    {
        get
        {
            lock (_lock)
                return [.. _agents.Values.OrderBy(a => Rank(a.State)).ThenByDescending(a => a.Since)];
        }
    }

    public ActiveAgent? Find(string id)
    {
        lock (_lock)
            return _agents.GetValueOrDefault(id);
    }

    /// <summary>Follows a session that is about to start.</summary>
    /// <param name="working">It starts with a prompt; without one it waits for the user.</param>
    public ActiveAgent Start(string directory, string? agentName, bool working)
    {
        var agent = new ActiveAgent(Guid.NewGuid().ToString("N")[..12], directory, agentName)
        {
            State = working ? AgentState.Working : AgentState.Ready,
            Since = _time.GetUtcNow().UtcDateTime,
        };
        lock (_lock)
            _agents[agent.Id] = agent;
        Changed?.Invoke(new AgentChange(agent, null, Ended: false));
        return agent;
    }

    /// <summary>Stops following a session, e.g. one whose window could not be opened or was closed.</summary>
    public void Forget(string id)
    {
        ActiveAgent? removed;
        lock (_lock)
        {
            if (!_agents.Remove(id, out removed))
                return;
        }
        Changed?.Invoke(new AgentChange(removed, null, Ended: true));
    }

    /// <summary>
    /// Applies an event the session <paramref name="id"/> reported. An unknown id is a session started before Rondel
    /// restarted: it is followed again from its folder.
    /// </summary>
    public void Apply(string id, HookEvent e)
    {
        AgentChange? change;
        lock (_lock)
        {
            if (!_agents.TryGetValue(id, out var agent))
            {
                if (e.Name == "SessionEnd" || string.IsNullOrEmpty(e.Cwd))
                    return;
                agent = new ActiveAgent(id, e.Cwd, null) { State = AgentState.Ready, Since = _time.GetUtcNow().UtcDateTime };
                Log.Info($"Following agent {id} again in '{e.Cwd}'");
            }
            var (next, notice) = Next(agent, e, _time.GetUtcNow().UtcDateTime);
            if (next is null)
            {
                _agents.Remove(id);
                change = new AgentChange(agent, null, Ended: true);
            }
            else
            {
                bool known = _agents.ContainsKey(id);
                _agents[id] = next;
                change = known && next == agent && notice is null ? null : new AgentChange(next, notice, Ended: false);
            }
        }
        Log.Debug($"Agent {id}: {e.Name}{(e.NotificationType is null ? "" : $" ({e.NotificationType})")} → "
            + (change is null ? "no change" : change.Ended ? "ended" : $"{change.Agent.State}{(change.Notice is { } n ? $", notify {n}" : "")}"));
        if (change is not null)
            Changed?.Invoke(change);
    }

    /// <summary>What <paramref name="e"/> makes of <paramref name="agent"/>.</summary>
    /// <returns>The agent after the event (null when its session ended) and whether to announce the change.</returns>
    internal static (ActiveAgent? Next, AgentNotice? Notice) Next(ActiveAgent agent, HookEvent e, DateTime now)
    {
        if (!string.IsNullOrEmpty(e.SessionId))
            agent = agent with { SessionId = e.SessionId };

        switch (e.Name)
        {
            case "UserPromptSubmit" or "PostToolUse" or "PostToolUseFailure":
                return (Enter(agent, AgentState.Working, now) with { Tool = null, Message = null }, null);

            case "PreToolUse" when e.ToolName == "AskUserQuestion":
                // A question: announced at once, since no permission prompt (with its own reminder) comes first.
                return (Enter(agent, AgentState.Waiting, now) with { Tool = null, Message = Brief(e.Question), Announced = true },
                    AgentNotice.Asks);

            case "PreToolUse":
                return (Enter(agent, AgentState.Working, now) with { Tool = null, Message = null }, null);

            case "PermissionRequest":
                // Announced by the Notification that follows when the user has not answered within a few seconds.
                return (Enter(agent, AgentState.Waiting, now) with { Tool = e.ToolName, Message = null }, null);

            case "Notification" when e.NotificationType is "permission_prompt" or "elicitation_dialog":
                {
                    var waiting = Enter(agent, AgentState.Waiting, now);
                    if (waiting.Tool is null && waiting.Message is null)
                        waiting = waiting with { Message = Brief(e.Message) };
                    return waiting.Announced ? (waiting, null) : (waiting with { Announced = true }, AgentNotice.Asks);
                }

            case "Notification" when e.NotificationType == "idle_prompt" && agent.State == AgentState.Working:
                // Interrupted by the user: no Stop event came, but the agent has been waiting for a prompt for a while.
                return (Enter(agent, AgentState.Ready, now) with { Tool = null, Message = null }, null);

            case "Stop":
                return (Enter(agent, AgentState.Ready, now) with { Tool = null, Message = Brief(e.LastAssistantMessage) }, AgentNotice.Finished);

            case "StopFailure":
                return (Enter(agent, AgentState.Failed, now) with { Tool = null, Message = Brief(e.Message) }, AgentNotice.Failed);

            case "SessionEnd" when e.Reason is "clear" or "resume":
                // /clear and /resume end one session and go on with another in the same window.
                return (Enter(agent, AgentState.Ready, now) with { Tool = null, Message = null }, null);

            case "SessionEnd":
                return (null, null);

            default:
                return (agent, null);
        }
    }

    static ActiveAgent Enter(ActiveAgent agent, AgentState state, DateTime now) =>
        agent.State == state ? agent : agent with { State = state, Since = now, Announced = false };

    static int Rank(AgentState state) => state switch
    {
        AgentState.Waiting => 0,
        AgentState.Failed => 1,
        AgentState.Ready => 2,
        _ => 3,
    };

    /// <summary>One line, at most <see cref="MaxMessageLength"/> characters: enough to recognize a reply or a question.</summary>
    internal static string? Brief(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var builder = new StringBuilder(Math.Min(text.Length, MaxMessageLength + 1));
        bool space = false;
        foreach (char c in text.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && builder.Length > 0)
                builder.Append(' ');
            space = false;
            builder.Append(c);
            if (builder.Length > MaxMessageLength)
            {
                int cut = char.IsHighSurrogate(builder[MaxMessageLength - 2]) ? MaxMessageLength - 2 : MaxMessageLength - 1;
                return builder.ToString(0, cut).TrimEnd() + "…";
            }
        }
        return builder.ToString();
    }
}
