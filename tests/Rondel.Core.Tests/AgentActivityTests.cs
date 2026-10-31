using System.Text;
using Rondel.Core.Activity;
using Rondel.Core.Localization;

namespace Rondel.Core.Tests;

/// <summary>What a followed agent's state becomes, and which changes are worth a notification.</summary>
public class AgentActivityTests
{
    readonly ManualTime _time = new();
    readonly AgentActivity _activity;
    readonly List<AgentChange> _changes = [];
    readonly string _folder = Path.Combine(Path.GetTempPath(), "work", "weather-api");

    public AgentActivityTests()
    {
        _activity = new AgentActivity(_time);
        _activity.Changed += _changes.Add;
    }

    static HookEvent Event(string name) => new(name) { SessionId = "s1" };

    AgentChange Last => _changes[^1];

    [Fact]
    public void APermissionIsAnnouncedOnceWhenTheReminderComesThenWorkGoesOnAndFinishes()
    {
        var agent = _activity.Start(_folder, "Claude Code", working: true);
        Assert.Equal(AgentState.Working, agent.State);
        Assert.Equal("weather-api", agent.Name);

        _activity.Apply(agent.Id, Event("PermissionRequest") with { ToolName = "Write" });
        Assert.Equal(AgentState.Waiting, Last.Agent.State);
        Assert.Equal("Write", Last.Agent.Tool);
        Assert.Null(Last.Notice); // the user may answer right away; the reminder decides

        _activity.Apply(agent.Id, Event("Notification") with { NotificationType = "permission_prompt", Message = "Claude needs your permission" });
        Assert.Equal(AgentNotice.Asks, Last.Notice);
        Assert.Equal("Write", Last.Agent.Tool);
        int count = _changes.Count;
        _activity.Apply(agent.Id, Event("Notification") with { NotificationType = "permission_prompt" });
        Assert.Equal(count, _changes.Count); // the same wait is not announced twice

        _activity.Apply(agent.Id, Event("PostToolUse") with { ToolName = "Write" });
        Assert.Equal(AgentState.Working, Last.Agent.State);
        Assert.Null(Last.Agent.Tool);

        _activity.Apply(agent.Id, Event("Stop") with { LastAssistantMessage = "I created `note.txt`.\n\nDone." });
        Assert.Equal(AgentState.Ready, Last.Agent.State);
        Assert.Equal(AgentNotice.Finished, Last.Notice);
        Assert.Equal("I created `note.txt`. Done.", Last.Agent.Message);
        Assert.Equal("s1", Last.Agent.SessionId);

        _activity.Apply(agent.Id, Event("SessionEnd") with { Reason = "prompt_input_exit" });
        Assert.True(Last.Ended);
        Assert.Empty(_activity.Agents);
    }

    [Fact]
    public void AQuestionIsAnnouncedAtOnceWithItsText()
    {
        var agent = _activity.Start(_folder, "Claude Code", working: true);

        _activity.Apply(agent.Id, Event("PreToolUse") with { ToolName = "AskUserQuestion", Question = "Which database?" });
        Assert.Equal(AgentState.Waiting, Last.Agent.State);
        Assert.Equal(AgentNotice.Asks, Last.Notice);
        Assert.Equal("Which database?", Last.Agent.Message);

        int count = _changes.Count;
        _activity.Apply(agent.Id, Event("Notification") with { NotificationType = "elicitation_dialog" });
        Assert.Equal(count, _changes.Count);
    }

    [Fact]
    public void AnInterruptedAgentIsReadyOnceItHasBeenIdle()
    {
        // No Stop event comes when the user interrupts; Claude Code's idle reminder does, a minute later.
        var agent = _activity.Start(_folder, "Claude Code", working: true);

        _activity.Apply(agent.Id, Event("Notification") with { NotificationType = "idle_prompt" });

        Assert.Equal(AgentState.Ready, Last.Agent.State);
        Assert.Null(Last.Notice);
    }

    [Fact]
    public void AnErrorIsAnnouncedAndShown()
    {
        var agent = _activity.Start(_folder, "Claude Code", working: true);

        _activity.Apply(agent.Id, Event("StopFailure") with { Message = "API Error: 529 Overloaded" });

        Assert.Equal(AgentState.Failed, Last.Agent.State);
        Assert.Equal(AgentNotice.Failed, Last.Notice);
        Assert.Equal("API Error: 529 Overloaded", Last.Agent.Message);
    }

    [Theory]
    [InlineData("clear", false)]
    [InlineData("resume", false)]
    [InlineData("other", true)] // also when the window is closed
    [InlineData("logout", true)]
    public void ClearAndResumeGoOnInTheSameWindow(string reason, bool ended)
    {
        var agent = _activity.Start(_folder, "Claude Code", working: false);

        _activity.Apply(agent.Id, Event("SessionEnd") with { Reason = reason });

        Assert.Equal(ended, Last.Ended);
        Assert.Equal(ended ? 0 : 1, _activity.Agents.Count);
    }

    [Fact]
    public void ASessionFromBeforeARestartIsFollowedAgainFromItsFolder()
    {
        _activity.Apply("0123456789ab", Event("SessionEnd") with { Cwd = _folder, Reason = "other" });
        Assert.Empty(_changes); // nothing to pick up

        _activity.Apply("0123456789ab", Event("Stop") with { Cwd = _folder, LastAssistantMessage = "Done." });

        var agent = Assert.Single(_activity.Agents);
        Assert.Equal("0123456789ab", agent.Id);
        Assert.Equal("weather-api", agent.Name);
        Assert.Null(agent.AgentName);
        Assert.Equal(AgentState.Ready, agent.State);
        Assert.Equal(AgentNotice.Finished, Last.Notice);
    }

    [Fact]
    public void AgentsWaitingForTheUserComeFirstThenFailedReadyAndWorkingOnes()
    {
        string Folder(string name) => Path.Combine(Path.GetTempPath(), "work", name);
        var working = _activity.Start(Folder("working"), "Claude Code", working: true);
        _time.Advance();
        var ready = _activity.Start(Folder("ready"), "Claude Code", working: false);
        _time.Advance();
        var waiting = _activity.Start(Folder("waiting"), "Claude Code", working: true);
        _activity.Apply(waiting.Id, Event("PermissionRequest") with { ToolName = "Bash" });
        _time.Advance();
        var failed = _activity.Start(Folder("failed"), "Claude Code", working: true);
        _activity.Apply(failed.Id, Event("StopFailure"));
        _time.Advance();
        var newerReady = _activity.Start(Folder("newer-ready"), "Claude Code", working: false);

        Assert.Equal(["waiting", "failed", "newer-ready", "ready", "working"], _activity.Agents.Select(a => a.Name));
        Assert.All([working, ready, newerReady], a => Assert.NotNull(_activity.Find(a.Id)));
    }

    [Fact]
    public void ForgettingASessionEndsIt()
    {
        var agent = _activity.Start(_folder, "Claude Code", working: true);

        _activity.Forget(agent.Id);

        Assert.True(Last.Ended);
        Assert.Null(_activity.Find(agent.Id));
    }

    [Fact]
    public void MessagesAreOneShortLine()
    {
        Assert.Null(AgentActivity.Brief("  \n "));
        Assert.Equal("a b c", AgentActivity.Brief(" a\n\n b\t c "));

        string brief = AgentActivity.Brief(new string('x', AgentActivity.MaxMessageLength - 2) + "😀" + new string('y', 50))!;
        Assert.Equal(AgentActivity.MaxMessageLength - 1, brief.Length);
        Assert.EndsWith("x…", brief, StringComparison.Ordinal); // the emoji is not cut in half
    }

    [Fact]
    public void EventsAreReadFromWhatClaudeCodeSends()
    {
        // As Claude Code 2.1 sends them (paths made up); other fields are ignored.
        var stop = HookEvent.Parse(Encoding.UTF8.GetBytes("""
            { "session_id": "4e7cbadf", "transcript_path": "/tmp/t.jsonl", "cwd": "/work/weather-api", "hook_event_name": "Stop",
              "permission_mode": "default", "stop_hook_active": false, "last_assistant_message": "hi", "background_tasks": [] }
            """))!;
        Assert.Equal("Stop", stop.Name);
        Assert.Equal("4e7cbadf", stop.SessionId);
        Assert.Equal("/work/weather-api", stop.Cwd);
        Assert.Equal("hi", stop.LastAssistantMessage);

        var question = HookEvent.Parse(Encoding.UTF8.GetBytes("""
            { "hook_event_name": "PreToolUse", "tool_name": "AskUserQuestion",
              "tool_input": { "questions": [ { "question": "Which database?", "header": "DB", "options": [] } ] } }
            """))!;
        Assert.Equal("Which database?", question.Question);

        // A field of an unexpected type does not lose the event.
        var failure = HookEvent.Parse(Encoding.UTF8.GetBytes("""{ "hook_event_name": "StopFailure", "error": { "code": 529 } }"""))!;
        Assert.Equal("StopFailure", failure.Name);
        Assert.Null(failure.Message);

        Assert.Null(HookEvent.Parse(Encoding.UTF8.GetBytes("""{ "type": "user" }""")));
        Assert.Null(HookEvent.Parse(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public void NotificationsSayWhatTheAgentWantsInTheProjectsName()
    {
        // The language is left as it is: tests run side by side, and the words come from the string tables.
        var agent = new ActiveAgent("0123456789ab", _folder, "Claude Code");

        Assert.Equal((Loc.Format("Agent.Permission.Title", "weather-api"), Loc.Format("Agent.Permission.Text", "Claude Code", "Bash")),
            AgentNotices.Describe(agent with { Tool = "Bash" }, AgentNotice.Asks));
        Assert.Equal((Loc.Format("Agent.Question.Title", "weather-api"), "Which database?"),
            AgentNotices.Describe(agent with { Message = "Which database?" }, AgentNotice.Asks));
        Assert.Equal((Loc.Format("Agent.Finished.Title", "weather-api"), Loc.Format("Agent.Finished.Text", "Claude Code")),
            AgentNotices.Describe(agent, AgentNotice.Finished));
        Assert.Equal(Loc.Format("Agent.Question.Text", Loc.Get("Agent.Someone")),
            AgentNotices.Describe(agent with { AgentName = null }, AgentNotice.Asks).Text); // picked up after a restart
        Assert.Equal(Loc.Get("Agent.Failed.Text"), AgentNotices.Describe(agent, AgentNotice.Failed).Text);
    }

    sealed class ManualTime : TimeProvider
    {
        DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance() => _now = _now.AddSeconds(1);
    }
}
