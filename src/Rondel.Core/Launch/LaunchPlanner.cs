using Rondel.Core.Agents;
using Rondel.Core.Menus;

namespace Rondel.Core.Launch;

public static class LaunchPlanner
{
    /// <summary>Turns a menu action into a launch plan for <paramref name="agent"/>.</summary>
    /// <param name="planMode">Put the agent's <see cref="AgentCapability.Plan"/> arguments first, when it has them.</param>
    /// <returns>The plan, or null when the agent does not support the action's capability.</returns>
    public static LaunchPlan? Create(AgentProfile agent, ActionDefinition action, string directory, string? prompt = null, string? sessionId = null,
        bool planMode = false)
    {
        if (!agent.TryGetArguments(action.Capability, out var templates))
            return null;
        IEnumerable<string> plan = planMode && action.Capability != AgentCapability.Plan && agent.TryGetArguments(AgentCapability.Plan, out var planArgs)
            ? planArgs
            : [];
        var arguments = Expand(plan.Concat(action.ExtraArgs).Concat(templates), prompt, sessionId);
        return new LaunchPlan(directory, agent.Name, agent.Command, arguments);
    }

    /// <summary>
    /// The plan of a session Rondel follows: the agent's <see cref="AgentProfile.ActivityArgs"/> (with "{hooks}" filled
    /// in) in front of the other arguments, and <paramref name="activityId"/> for the script and the launcher.
    /// </summary>
    /// <returns>The plan, or null when the agent cannot be followed.</returns>
    public static LaunchPlan? Follow(LaunchPlan plan, AgentProfile agent, string hooksFile, string activityId)
    {
        if (agent.ActivityArgs is not { Count: > 0 } templates)
            return null;
        var arguments = templates.Where(t => !string.IsNullOrEmpty(t)).Select(t => t.Replace("{hooks}", hooksFile, StringComparison.Ordinal));
        return plan with { Arguments = [.. arguments, .. plan.Arguments], ActivityId = activityId };
    }

    /// <summary>Fills in "{prompt}" and "{id}"; an argument whose value is missing is left out.</summary>
    internal static List<string> Expand(IEnumerable<string> templates, string? prompt, string? sessionId)
    {
        var result = new List<string>();
        foreach (string template in templates)
        {
            if (string.IsNullOrEmpty(template))
                continue;
            bool needsPrompt = template.Contains("{prompt}", StringComparison.Ordinal);
            bool needsId = template.Contains("{id}", StringComparison.Ordinal);
            if ((needsPrompt && string.IsNullOrEmpty(prompt)) || (needsId && string.IsNullOrEmpty(sessionId)))
                continue;
            // {id} first: a session id never contains "{prompt}", while a prompt may well contain "{id}".
            string value = needsId ? template.Replace("{id}", sessionId, StringComparison.Ordinal) : template;
            if (needsPrompt)
                value = value.Replace("{prompt}", prompt, StringComparison.Ordinal);
            result.Add(value);
        }
        return result;
    }
}
