using Rondel.Core.Localization;

namespace Rondel.Core.Activity;

/// <summary>The words of a notification about a followed agent, the same on every platform.</summary>
public static class AgentNotices
{
    /// <returns>A title naming the project and what happened, and a line with the details.</returns>
    public static (string Title, string Text) Describe(ActiveAgent agent, AgentNotice notice)
    {
        string who = agent.AgentName ?? Loc.Get("Agent.Someone");
        return notice switch
        {
            AgentNotice.Asks when agent.Tool is not null =>
                (Loc.Format("Agent.Permission.Title", agent.Name), Loc.Format("Agent.Permission.Text", who, agent.Tool)),
            AgentNotice.Asks =>
                (Loc.Format("Agent.Question.Title", agent.Name), agent.Message ?? Loc.Format("Agent.Question.Text", who)),
            AgentNotice.Finished =>
                (Loc.Format("Agent.Finished.Title", agent.Name), agent.Message ?? Loc.Format("Agent.Finished.Text", who)),
            _ => (Loc.Format("Agent.Failed.Title", agent.Name), agent.Message ?? Loc.Get("Agent.Failed.Text")),
        };
    }
}
