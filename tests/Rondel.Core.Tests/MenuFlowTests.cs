using Rondel.Core.Activity;
using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Menus;
using Rondel.Core.Platform;
using Rondel.Core.Projects;
using Rondel.Core.Prompts;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;

namespace Rondel.Core.Tests;

/// <summary>The shared flow every platform relies on: what choosing a menu item starts, and where.</summary>
public class MenuFlowTests
{
    readonly FakeHost _host = new();
    readonly FakeLauncher _launcher = new();
    readonly AppSettings _settings = new();
    readonly MenuFlow _flow;

    public MenuFlowTests()
    {
        _settings.Normalize();
        _flow = new MenuFlow(_host, _launcher, new RulesOnly(), () => _settings);
    }

    [Fact]
    public void ARecentProjectResumesItsLatestSessionInItsOwnFolder()
    {
        var model = _flow.Build(new TriggerContext(ContextKind.DesktopEmpty, Path.GetTempPath(), null, "test"))!;
        var recents = model.Items.Single(i => i.Action.Id == "recents");
        string alpha = Path.Combine(Path.GetTempPath(), "work", "alpha");
        var project = MenuBuilder.ProjectItems(recents, [new FolderActivity(alpha, DateTime.UtcNow, 1, "s1")], _ => [])[0];

        _flow.Invoke(model, recents); // the slice itself only opens its items
        Assert.Empty(_launcher.Plans);

        _flow.Invoke(model, project);
        var plan = Assert.Single(_launcher.Plans);
        Assert.Equal(alpha, plan.Directory);
        Assert.Equal(["--resume", "s1"], plan.Arguments);
        Assert.Equal(["close False"], _host.Calls);
    }

    [Fact]
    public void ANewProjectCombinesItsTemplatesAndStartsTheWayItWasAskedTo()
    {
        var root = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            string templates = Path.Combine(root.FullName, "templates");
            void Template(string name, string file, string text) =>
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(templates, name, Path.GetDirectoryName(file)!)).FullName,
                    Path.GetFileName(file)), text);
            Template("a-claude", "CLAUDE.md", "from a");
            Template("a-claude", Path.Combine(".git", "HEAD"), "a template kept in git");
            Template("b-python", "CLAUDE.md", "from b");
            Template("b-python", "main.py", "print()");
            string parent = Directory.CreateDirectory(Path.Combine(root.FullName, "projects")).FullName;
            var flow = new MenuFlow(_host, _launcher, new RulesOnly(), () => _settings, change => change(_settings), new ProjectTemplates(templates));
            var model = flow.Build(new TriggerContext(ContextKind.ExplorerEmpty, parent, null, "test"))!;

            flow.Invoke(model, model.Items.Single(i => i.Action.Id == "new-project"));
            var card = Assert.IsType<NewProjectSpec>(_host.NewProject);
            Assert.Equal(["a-claude", "b-python"], card.Templates);

            var options = new NewProjectOptions { Prompt = true, Git = true, Plan = true, Templates = ["b-python", "a-claude"] };
            Assert.Null(card.Submit(new NewProjectRequest("demo", options, "build it")));

            string project = Path.Combine(parent, "demo");
            Assert.Equal("from b", File.ReadAllText(Path.Combine(project, "CLAUDE.md"))); // copied in the offered order: the later wins
            Assert.True(File.Exists(Path.Combine(project, "main.py")));
            Assert.False(Directory.Exists(Path.Combine(project, ".git"))); // a template's own repository stays behind
            var plan = Assert.Single(_launcher.Plans);
            Assert.Equal(project, plan.Directory);
            Assert.Equal(["--permission-mode", "plan", "build it"], plan.Arguments);
            Assert.True(plan.InitGit);
            Assert.Equal(["b-python", "a-claude"], _settings.NewProject.Templates); // remembered for the next time
            Assert.NotNull(card.Submit(new NewProjectRequest("demo", options, "again"))); // exists now: shown inline
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void AnOptionTurnedOffInSettingsIsNeitherOfferedNorApplied()
    {
        var parent = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            _settings.NewProjectCard.Git = false;
            _settings.NewProjectCard.Plan = false;
            var model = _flow.Build(new TriggerContext(ContextKind.ExplorerEmpty, parent.FullName, null, "test"))!;

            _flow.Invoke(model, model.Items.Single(i => i.Action.Id == "new-project"));
            var card = Assert.IsType<NewProjectSpec>(_host.NewProject);
            Assert.False(card.CanGit);
            Assert.False(card.CanPlan);

            Assert.Null(card.Submit(new NewProjectRequest("demo", new NewProjectOptions { Git = true, Plan = true }, null)));
            var plan = Assert.Single(_launcher.Plans);
            Assert.False(plan.InitGit);
            Assert.Empty(plan.Arguments);
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    public void AskingAboutAFileStartsInItsFolderWithTheFileMentioned()
    {
        string folder = Path.Combine(Path.GetTempPath(), "work");
        string file = Path.Combine(folder, "Proje Notları.md");
        var model = _flow.Build(new TriggerContext(ContextKind.FileItem, folder, "Proje Notları.md", "test") { FilePath = file })!;

        _flow.Invoke(model, model.Items.Single(i => i.Action.Id == "ask-file"));
        var input = Assert.IsType<InputSpec>(_host.Input);
        Assert.False(input.AskName);
        Assert.Equal(file, input.Location);

        Assert.Null(input.Submit("", "summarize this"));
        var plan = Assert.Single(_launcher.Plans);
        Assert.Equal(folder, plan.Directory);
        Assert.Equal(["@\"Proje Notları.md\" summarize this"], plan.Arguments);
    }

    [Fact]
    public void AskingAboutSeveralSelectedFilesMentionsEachOfThem()
    {
        string folder = Path.Combine(Path.GetTempPath(), "work");
        string[] files = [Path.Combine(folder, "Program.cs"), Path.Combine(folder, "Proje Notları.md")];
        var context = new TriggerContext(ContextKind.FileItem, folder, "Program.cs", "test") { FilePath = files[0], SelectedFiles = files };
        var model = _flow.Build(context)!;

        Assert.Equal(Loc.Format("Context.Files", 2), model.Title);
        var ask = model.Items.Single(i => i.Action.Id == "ask-file");
        Assert.Equal(Loc.Get("Action.AskFiles"), ask.Label);
        _flow.Invoke(model, ask);
        var input = Assert.IsType<InputSpec>(_host.Input);
        Assert.Equal($"{folder} · Program.cs, Proje Notları.md", input.Location);

        Assert.Null(input.Submit("", "compare these"));
        var plan = Assert.Single(_launcher.Plans);
        Assert.Equal(folder, plan.Directory);
        Assert.Equal(["@Program.cs @\"Proje Notları.md\" compare these"], plan.Arguments);
    }

    [Fact]
    public void OnlyTheSelectedFilesBesideTheHeldOneAreAskedAbout()
    {
        string folder = Path.Combine(Path.GetTempPath(), "work");
        string a = Path.Combine(folder, "a.cs"), b = Path.Combine(folder, "b.md"), sub = Path.Combine(folder, "sub");
        string elsewhere = Path.Combine(Path.GetTempPath(), "Public", "c.txt");
        bool IsFile(string path) => path != sub;

        Assert.Equal([a, b], FileSelection.Around(b, [sub, a, elsewhere, b], IsFile)); // in the selection's order
        Assert.Empty(FileSelection.Around(a, [b, sub], IsFile)); // the held file is not selected: it alone
        Assert.Empty(FileSelection.Around(a, [a, sub, elsewhere], IsFile)); // no other file beside it
        Assert.Equal([a], new TriggerContext(ContextKind.FileItem, folder, "a.cs", "test") { FilePath = a }.Files);
    }

    [Fact]
    public void SavedPromptsArePinnedToTheAskSlicesAndListedOnTheirCard()
    {
        var root = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            void Prompt(string file, string text) => File.WriteAllText(Path.Combine(root.FullName, file), text);
            Prompt("Review.md", "\nReview the changes.\n\n");
            Prompt("Explain.md", "Explain it.");
            Prompt("Empty.md", "  \n");
            Prompt("notes.txt", "not a prompt");
            Prompt(".draft.md", "not yet");
            var prompts = new SavedPrompts(root.FullName);
            var flow = new MenuFlow(_host, _launcher, new RulesOnly(), () => _settings, prompts: prompts);
            _settings.Prompts = new SavedPromptsOptions { Enabled = true, Pinned = ["review", "Gone", "Explain"] };
            string folder = Path.Combine(Path.GetTempPath(), "work");
            var context = new TriggerContext(ContextKind.FileItem, folder, "Program.cs", "test") { FilePath = Path.Combine(folder, "Program.cs") };
            var model = flow.Build(context)!;

            // Only the Ask slice opens into them: in the order pinned, named as their files are, without the missing one.
            Assert.Equal(["ask-file"], model.Items.Where(MenuBuilder.OpensPrompts).Select(i => i.Action.Id));
            Assert.True(MenuDetails.Needed(model));
            var ask = model.Items.Single(i => i.Action.Id == "ask-file");
            var arc = MenuDetails.Load(model, prompts: prompts).Children[model.Items.ToList().IndexOf(ask)];
            Assert.Equal(["Review", "Explain"], arc.Select(i => i.Label));
            Assert.Empty(MenuDetails.Load(model).Children[model.Items.ToList().IndexOf(ask)]); // a platform that offers none

            // Picked on the arc: it starts right away, after the file it is about.
            flow.Invoke(model, arc[0]);
            var plan = Assert.Single(_launcher.Plans);
            Assert.Equal(folder, plan.Directory);
            Assert.Equal(["@Program.cs Review the changes."], plan.Arguments);
            Assert.Equal(["close False"], _host.Calls);

            // Its file went away meanwhile: nothing starts, and it says why.
            File.Delete(Path.Combine(root.FullName, "Explain.md"));
            flow.Invoke(model, arc[1]);
            Assert.Single(_launcher.Plans);
            Assert.StartsWith("warn ", _host.Calls[^1]);

            // The Ask card lists every saved prompt; an empty one is listed but cannot be used.
            flow.Invoke(model, ask);
            var input = Assert.IsType<InputSpec>(_host.Input);
            Assert.Equal(["Empty", "Review"], input.PromptNames);
            Assert.Null(input.SavedPrompts!.Read("Empty"));

            // Turned off: Ask is as it was.
            _settings.Prompts.Enabled = false;
            model = flow.Build(context)!;
            Assert.DoesNotContain(model.Items, MenuBuilder.OpensPrompts);
            flow.Invoke(model, model.Items.Single(i => i.Action.Id == "ask-file"));
            Assert.Null(_host.Input!.SavedPrompts);
            Assert.Empty(_host.Input.PromptNames);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void AskingAboutAFileNeedsAFile()
    {
        var model = _flow.Build(new TriggerContext(ContextKind.ExplorerEmpty, Path.GetTempPath(), null, "test"))!;

        Assert.DoesNotContain(model.Items, i => i.Action.MentionFile);
    }

    [Theory]
    [InlineData("Program.cs", "@Program.cs")]
    [InlineData("Çizelge_v2 (1).txt", "@\"Çizelge_v2 (1).txt\"")]
    [InlineData("rapor(1).md", "@rapor(1).md")]
    [InlineData("a&b.txt", "@\"a&b.txt\"")]
    public void FileMentionQuotesNamesThatWouldEndEarly(string name, string expected)
    {
        Assert.Equal(expected, FileMention.Format("@{file}", name));
    }

    [Fact]
    public void AFollowedAgentGetsTheHooksFileAndAnIdUnlessFollowingIsOff()
    {
        string hooks = Path.Combine(Path.GetTempPath(), "Rondel", "agent-hooks.json");
        var activity = new AgentActivity { HooksFile = hooks };
        var flow = new MenuFlow(_host, _launcher, new RulesOnly(), () => _settings, activity: activity);
        string folder = Path.Combine(Path.GetTempPath(), "work", "weather-api");
        var model = flow.Build(new TriggerContext(ContextKind.FolderItem, folder, "weather-api", "test"))!;
        var open = model.Items.Single(i => i.Action.Id == "open");

        flow.Invoke(model, open);
        var plan = Assert.Single(_launcher.Plans);
        Assert.Equal(["--settings", hooks], plan.Arguments);
        var agent = Assert.Single(activity.Agents);
        Assert.Equal(agent.Id, plan.ActivityId);
        Assert.Equal(folder, agent.Directory);
        Assert.Equal(AgentState.Ready, agent.State); // no prompt: it waits for the user

        _settings.ActiveAgents.Follow = false;
        flow.Invoke(model, open);
        Assert.Null(_launcher.Plans[1].ActivityId);
        Assert.Empty(_launcher.Plans[1].Arguments);

        _settings.ActiveAgents.Follow = true;
        _launcher.Failure = new LaunchException("no terminal");
        flow.Invoke(model, open);
        Assert.Single(activity.Agents); // the one that could not start is not followed
    }

    [Fact]
    public void ChoosingAnActiveAgentBringsItsTerminalToTheFront()
    {
        string folder = Path.Combine(Path.GetTempPath(), "work", "weather-api");
        var agents = new[] { new ActiveAgent("0123456789ab", folder, "Claude Code") { State = AgentState.Waiting, Tool = "Bash" } };
        var model = _flow.Build(new TriggerContext(ContextKind.DesktopEmpty, Path.GetTempPath(), null, "test"))!;
        var slice = model.Items.Single(MenuBuilder.ListsActiveAgents);
        var details = MenuDetails.Load(model, agents);
        int index = model.Items.ToList().IndexOf(slice);

        _flow.Invoke(model, slice); // the slice itself only opens its agents
        Assert.Empty(_host.Calls);

        _flow.Invoke(model, details.Children[index][0]);
        Assert.Equal(["close False", "focus 0123456789ab"], _host.Calls);
        Assert.Empty(_launcher.Plans);
    }

    [Fact]
    public void TheAgentsShortcutOpensTheDesktopMenuAtItsActiveAgentsSlice()
    {
        var (model, slice) = _flow.AgentsMenu();
        Assert.Equal(ContextKind.DesktopEmpty, model.Context.Kind);
        Assert.Equal("agents", model.Items[slice].Action.Id);

        // A desktop menu without the slice gets it, for this time.
        _settings.Menus[nameof(ContextKind.DesktopEmpty)] = ["new-project"];
        (model, slice) = _flow.AgentsMenu();
        Assert.Equal(["new-project", "agents"], model.Items.Select(i => i.Action.Id));
        Assert.Equal(1, slice);
    }

    sealed class FakeHost : IMenuHost
    {
        public List<string> Calls { get; } = [];

        public InputSpec? Input { get; private set; }

        public void CloseMenu(bool restoreFocus) => Calls.Add($"close {restoreFocus}");

        public void StartInput(InputSpec spec) => Input = spec;

        public NewProjectSpec? NewProject { get; private set; }

        public void StartNewProject(NewProjectSpec spec) => NewProject = spec;

        public void ShowWarning(string message) => Calls.Add($"warn {message}");

        public void FocusAgent(string id) => Calls.Add($"focus {id}");
    }

    sealed class FakeLauncher : ITerminalLauncher
    {
        public List<LaunchPlan> Plans { get; } = [];

        public LaunchException? Failure { get; set; }

        public void Launch(LaunchPlan plan, AppSettings settings)
        {
            if (Failure is not null)
                throw Failure;
            Plans.Add(plan);
        }
    }

    sealed class RulesOnly : IFolderNameValidator
    {
        public string? Validate(string parent, string name) => FolderNameRules.Validate(parent, name, FolderNameRules.LongPathLimit);
    }
}
