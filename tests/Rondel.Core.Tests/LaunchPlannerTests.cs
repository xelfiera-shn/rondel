using Rondel.Core.Agents;
using Rondel.Core.Launch;
using Rondel.Core.Menus;

namespace Rondel.Core.Tests;

public class LaunchPlannerTests
{
    [Fact]
    public void CreatePutsExtraArgumentsBeforeTheCapabilityArguments()
    {
        var action = new ActionDefinition { Capability = AgentCapability.Prompt, ExtraArgs = ["--permission-mode", "plan"] };

        var plan = LaunchPlanner.Create(BuiltInAgents.ClaudeCode(), action, @"C:\work", prompt: "fix the build");

        Assert.NotNull(plan);
        Assert.Equal(["--permission-mode", "plan", "fix the build"], plan.Arguments);
        Assert.Equal("claude", plan.Command);
        Assert.Equal("Claude Code · work", plan.Title);
    }

    [Fact]
    public void CreateReturnsNullWhenTheAgentLacksTheCapability()
    {
        var agent = new AgentProfile { Id = "minimal", Name = "Minimal", Command = "m", Capabilities = { ["Open"] = [] } };

        Assert.Null(LaunchPlanner.Create(agent, new ActionDefinition { Capability = AgentCapability.Continue }, @"C:\work"));
    }

    [Fact]
    public void ExpandLeavesOutArgumentsWithoutAValue()
    {
        Assert.Equal(["--resume"], LaunchPlanner.Expand(["--resume", "{id}", "{prompt}"], prompt: null, sessionId: null));
        Assert.Equal(["--resume", "abc", "use {id} here"], LaunchPlanner.Expand(["--resume", "{id}", "{prompt}"], "use {id} here", "abc"));
    }
}
