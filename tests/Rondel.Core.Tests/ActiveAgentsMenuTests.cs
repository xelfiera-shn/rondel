using Rondel.Core.Activity;
using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Localization;
using Rondel.Core.Menus;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;

namespace Rondel.Core.Tests;

/// <summary>The Active Agents slice of the desktop menu and the arc it opens into.</summary>
public class ActiveAgentsMenuTests
{
    readonly MenuModel _model;
    readonly int _slice;
    readonly DateTime _since = DateTime.UtcNow.AddMinutes(-5);

    public ActiveAgentsMenuTests()
    {
        var settings = new AppSettings();
        settings.Normalize();
        _model = MenuBuilder.Build(new TriggerContext(ContextKind.DesktopEmpty, Path.GetTempPath(), null, "test"), settings);
        _slice = _model.Items.ToList().FindIndex(MenuBuilder.ListsActiveAgents);
    }

    ActiveAgent Agent(string name, AgentState state) => new(name, Path.Combine(Path.GetTempPath(), "work", name), "Claude Code")
    {
        State = state,
        Since = _since,
    };

    [Fact]
    public void WithoutAgentsTheSliceIsDimmedAndOpensNothing()
    {
        Assert.True(MenuDetails.Needed(_model));

        var details = MenuDetails.Load(_model, []);

        Assert.Equal((Loc.Get("Agents.None"), true), details.Hints[_slice]);
        Assert.Empty(details.Children[_slice]);
        Assert.Null(details.States[_slice]);
    }

    [Fact]
    public void TheArcShowsWhatEachAgentIsDoingAndTheSliceCountsThoseWaiting()
    {
        // In the order AgentActivity gives them: waiting first.
        var agents = new[]
        {
            Agent("weather-api", AgentState.Waiting) with { Tool = "Bash" },
            Agent("acme-dashboard", AgentState.Waiting),
            Agent("billing-service", AgentState.Ready),
            Agent("docs-site", AgentState.Working),
        };

        var details = MenuDetails.Load(_model, agents);

        Assert.Equal((Loc.Format("Agents.Waiting", 2), false), details.Hints[_slice]);
        Assert.Equal(AgentState.Waiting, details.States[_slice]); // the dot shows the most urgent state
        var items = details.Children[_slice];
        Assert.Equal(["weather-api", "acme-dashboard", "billing-service", "docs-site"], items.Select(i => i.Label));
        Assert.Equal(
            [
                Loc.Get("Agents.Permission"),
                Loc.Get("Agents.Question"),
                Loc.Format("Agents.Ready", RelativeTime.Describe(_since)),
                Loc.Format("Agents.WorkingFor", Loc.Format("Duration.Minutes", 5)),
            ],
            items.Select(i => i.Hint));
        Assert.Equal(agents.Select(a => (AgentState?)a.State), items.Select(i => i.State));
        Assert.Equal(agents.Select(a => a.Id), items.Select(i => i.ActivityId));
        Assert.All(items, i => Assert.Empty(i.Children));
    }

    [Fact]
    public void TheArcHoldsTheMostUrgentAgentsAndTheSliceCountsThemAll()
    {
        var many = Enumerable.Range(0, 9).Select(i => Agent($"project-{i}", AgentState.Working)).ToArray();

        var details = MenuDetails.Load(_model, many);

        Assert.Equal(MenuBuilder.MaxActiveAgents, details.Children[_slice].Count);
        Assert.Equal((Loc.Format("Agents.Count", 9), false), details.Hints[_slice]);
        Assert.Equal(Loc.Get("Agents.One"), MenuDetails.Load(_model, [many[0]]).Hints[_slice].Hint);
    }

    [Fact]
    public void WorkingTimeIsADurationAndNotShownUnderAMinute()
    {
        Assert.Null(RelativeTime.Duration(TimeSpan.FromSeconds(40)));
        Assert.Equal(Loc.Format("Duration.Minutes", 12), RelativeTime.Duration(TimeSpan.FromMinutes(12.5)));
        Assert.Equal(Loc.Format("Duration.Hours", 2), RelativeTime.Duration(TimeSpan.FromMinutes(150)));
        Assert.Equal(Loc.Get("Agents.Working"), MenuBuilder.DescribeState(Agent("x", AgentState.Working) with { Since = DateTime.UtcNow }));
    }
}
