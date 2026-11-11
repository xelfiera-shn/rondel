using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Platform;
using Rondel.Core.Settings;

namespace Rondel.Core.Tests;

/// <summary>A Quick Conversation answers its first question on its card, and goes on in a terminal when asked to.</summary>
public sealed class QuickAnswerTests : IDisposable
{
    const string Session = "ef645cbe-38d4-48b0-90a2-de0b15b9071c";

    /// <summary>Claude Code's print-mode output, cut down: two messages, a tool in between, and lines of other kinds.</summary>
    static readonly string[] Answered =
    [
        $$"""{"type":"system","subtype":"init","session_id":"{{Session}}","tools":["Read","WebSearch"]}""",
        """{"type":"stream_event","event":{"type":"message_start","message":{}}}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"2+2 "}}}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"is **4**."}}}""",
        """{"type":"assistant","message":{"content":[{"type":"text","text":"2+2 is **4**."}]}}""",
        """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","name":"WebSearch"}}}""",
        """{"type":"rate_limit_event","rate_limit_info":{}}""",
        "not an event",
        """{"type":"stream_event","event":{"type":"message_start","message":{}}}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Anything else?"}}}""",
        $$"""{"type":"result","subtype":"success","is_error":false,"result":"Anything else?","session_id":"{{Session}}"}""",
    ];

    readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("rondel-tests-");
    readonly AppSettings _settings = new();
    readonly Host _host = new();
    readonly Terminal _terminal = new();
    readonly Answers _answers = new();

    public QuickAnswerTests() => _settings.Normalize();

    public void Dispose() => _root.Delete(recursive: true);

    MenuFlow Flow(IAnswerLauncher? answers, TimeProvider? time = null) =>
        new(_host, _terminal, new Rules(), () => _settings, quickDirectory: Path.Combine(_root.FullName, "Quick"), answers: answers,
            time: time);

    /// <summary>Picks Quick Conversation on the desktop menu and sends <paramref name="question"/> from its card.</summary>
    static void Ask(MenuFlow flow, Host host, string question)
    {
        var model = flow.Build(new TriggerContext(ContextKind.DesktopEmpty, Path.GetTempPath(), null, "test"))!;
        flow.Invoke(model, model.Items.Single(i => i.Action.Ephemeral));
        Assert.Null(host.Input!.Submit("", question));
    }

    static void WaitFor(QuickAnswer answer, AnswerState state)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (answer.State != state && DateTime.UtcNow < until)
            Thread.Sleep(10);
        Assert.Equal(state, answer.State);
    }

    [Fact]
    public void TheFirstAnswerIsWrittenOnTheCardAndCanGoOnInATerminal()
    {
        _settings.Quick.Model = "sonnet";
        _answers.Output = Answered;
        var flow = Flow(_answers);

        Ask(flow, _host, "What is 2+2?");

        var answer = _host.Answer!;
        WaitFor(answer, AnswerState.Done);
        Assert.Empty(_terminal.Plans); // no terminal for the first answer
        var plan = Assert.Single(_answers.Plans);
        Assert.Equal(flow.QuickDirectory, plan.Directory);
        Assert.Equal([.. BuiltInAgents.ClaudeCode().AnswerArgs!, "--model", "sonnet"], plan.Arguments);
        Assert.Empty(plan.Environment); // its transcript is written, or it could not be continued
        Assert.Equal("What is 2+2?", _answers.Process!.Question);
        Assert.Equal("What is 2+2?", answer.Question);
        Assert.Equal("2+2 is **4**.\n\nAnything else?", answer.Text);
        Assert.Null(answer.Tool);
        Assert.Equal(Session, answer.SessionId);
        Assert.Equal([Session], flow.QuickSessions); // the sweep keeps it while the card is open
        Assert.True(answer.CanContinue);

        answer.Continue();

        var resumed = Assert.Single(_terminal.Plans);
        Assert.Equal(flow.QuickDirectory, resumed.Directory);
        Assert.Equal(["--model", "sonnet", "--resume", Session], resumed.Arguments);
        Assert.Equal("1", resumed.Environment["CLAUDE_CODE_SKIP_PROMPT_HISTORY"]); // from here on nothing more is written
        Assert.Equal(Loc.Get("Quick.Window"), resumed.Name);
        Assert.Contains("close False", _host.Calls);
        Assert.False(flow.QuickAnswerOpen);
        Assert.Equal([Session], flow.QuickSessions); // until that session ends
        flow.ForgetQuickSession(Session);
        Assert.Empty(flow.QuickSessions);
    }

    [Fact]
    public void ASessionContinuedInATerminalIsKeptForAnHourAtMost()
    {
        _answers.Output = Answered;
        var time = new ManualTime();
        var flow = Flow(_answers, time);
        Ask(flow, _host, "What is 2+2?");
        var answer = _host.Answer!;
        WaitFor(answer, AnswerState.Done);

        answer.Continue();

        time.Now += MenuFlow.ContinuedKept - TimeSpan.FromSeconds(1);
        Assert.Equal([Session], flow.QuickSessions);
        time.Now += TimeSpan.FromSeconds(1);
        Assert.Empty(flow.QuickSessions); // nothing said it ended (its terminal may never have run it): the sweep may take it
    }

    [Fact]
    public void AClosedCardEndsItsSessionAndAFailedAnswerOffersTheTerminal()
    {
        _answers.Output = Answered;
        var flow = Flow(_answers);
        var ended = new List<string>();
        flow.QuickSessionEnded += ended.Add;

        Ask(flow, _host, "What is 2+2?");
        var answer = _host.Answer!;
        WaitFor(answer, AnswerState.Done);
        answer.Close();
        Assert.Equal([Session], ended); // its files go with the sweep now
        Assert.Empty(flow.QuickSessions);
        Assert.True(_answers.Process!.Killed);
        Assert.Empty(_terminal.Plans);

        _answers.Output = [$$"""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login"}"""];
        Ask(flow, _host, "And 3+3?");
        var failed = _host.Answer!;
        WaitFor(failed, AnswerState.Failed);
        Assert.Equal("Not logged in · Please run /login", failed.Error);
        Assert.False(failed.CanContinue);

        failed.OpenInTerminal();
        var plan = Assert.Single(_terminal.Plans);
        Assert.Equal(["And 3+3?"], plan.Arguments);
        Assert.Equal("1", plan.Environment["CLAUDE_CODE_SKIP_PROMPT_HISTORY"]);

        // One that ends without a result failed with what it wrote to its error output.
        _answers.Output = [];
        _answers.Errors = "claude: unknown option '--include-partial-messages'";
        Ask(flow, _host, "Hello?");
        WaitFor(_host.Answer!, AnswerState.Failed);
        Assert.Equal(_answers.Errors, _host.Answer!.Error);
    }

    [Fact]
    public void WithoutAnAnswerOnTheCardItStartsInATerminalAsBefore()
    {
        // The platform cannot start the agent without a terminal (no launcher, or an alias it cannot run).
        foreach (var flow in new[] { Flow(null), Flow(_answers) })
        {
            _answers.Output = null;
            Ask(flow, _host, "What is 2+2?");
            Assert.False(flow.QuickAnswerOpen);
        }
        Assert.Null(_host.Answer);
        Assert.Equal(2, _terminal.Plans.Count);
        Assert.All(_terminal.Plans, plan => Assert.Equal(["What is 2+2?"], plan.Arguments));

        // An agent profile with no answer arguments, too.
        _settings.FindAgent(BuiltInAgents.ClaudeCodeId)!.AnswerArgs = [];
        _answers.Output = Answered;
        Ask(Flow(_answers), _host, "What is 2+2?");
        Assert.Null(_host.Answer);
        Assert.Equal(3, _terminal.Plans.Count);
    }

    [Fact]
    public void AnOlderSettingsFileGetsTheAnswerArgumentsAndAnEmptyListTurnsThemOff()
    {
        var older = SettingsJson.Deserialize("""
            { "Agents": [ { "Id": "claude-code", "Command": "claude" }, { "Id": "other", "Command": "other", "AnswerArgs": [] } ],
              "Quick": { "Model": "  haiku " } }
            """)!;
        older.Normalize();

        Assert.Equal(BuiltInAgents.ClaudeCode().AnswerArgs, older.FindAgent("claude-code")!.AnswerArgs);
        Assert.Equal(["--model", "{model}"], older.FindAgent("claude-code")!.ModelArgs);
        Assert.Empty(older.FindAgent("other")!.AnswerArgs!);
        Assert.Equal("haiku", older.Quick.Model);
        Assert.Equal("", new AppSettings().Quick.Model); // the agent's own default
        Assert.Empty(LaunchPlanner.ModelArguments(older.FindAgent("claude-code")!, ""));
    }

    sealed class Host : IMenuHost
    {
        public List<string> Calls { get; } = [];

        public InputSpec? Input { get; private set; }

        public QuickAnswer? Answer { get; private set; }

        public void CloseMenu(bool restoreFocus) => Calls.Add($"close {restoreFocus}");

        public void StartInput(InputSpec spec) => Input = spec;

        public void ShowCard(InputSpec spec) => Input = spec;

        public void ShowAnswer(QuickAnswer answer) => Answer = answer;

        public void StartNewProject(NewProjectSpec spec)
        {
        }

        public void ShowWarning(string message) => Calls.Add($"warn {message}");

        public void FocusAgent(string id)
        {
        }
    }

    sealed class Terminal : ITerminalLauncher
    {
        public List<LaunchPlan> Plans { get; } = [];

        public void Launch(LaunchPlan plan, AppSettings settings) => Plans.Add(plan);
    }

    sealed class Rules : IFolderNameValidator
    {
        public string? Validate(string parent, string name) => null;
    }

    /// <summary>Gives every answer <see cref="Output"/>; null: the agent cannot be started without a terminal.</summary>
    sealed class Answers : IAnswerLauncher
    {
        public List<LaunchPlan> Plans { get; } = [];

        public string[]? Output { get; set; }

        public string Errors { get; set; } = "";

        public FakeProcess? Process { get; private set; }

        public IAnswerProcess? Start(LaunchPlan plan)
        {
            if (Output is null)
                return null;
            Plans.Add(plan);
            return Process = new FakeProcess(Output, Errors);
        }
    }

    sealed class FakeProcess(string[] output, string errors) : IAnswerProcess
    {
        readonly StringWriter _input = new();

        public TextWriter Input => _input;

        public TextReader Output { get; } = new StringReader(string.Join('\n', output));

        public string Errors => errors;

        public string Question => _input.ToString();

        public bool Killed { get; private set; }

        public int? WaitForExit(TimeSpan timeout) => 0;

        public void Kill() => Killed = true;
    }

    sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
