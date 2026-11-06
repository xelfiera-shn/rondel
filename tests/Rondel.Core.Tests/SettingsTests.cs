using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Menus;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;

namespace Rondel.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void NormalizeRepairsAHandEditedFile()
    {
        const string json = """
            {
              // hand-edited: comments, trailing commas, lower-case names, bad values
              "holdMs": 5,
              "defaultAgent": "missing",
              "agents": [],
              "menus": { "FolderItem": ["open"], },
            }
            """;
        var settings = SettingsJson.Deserialize(json)!;

        settings.Normalize();

        Assert.Equal(AppSettings.MinHoldMs, settings.HoldMs);
        Assert.Equal(BuiltInAgents.ClaudeCodeId, settings.DefaultAgent);
        Assert.NotNull(settings.FindAgent(BuiltInAgents.ClaudeCodeId));
        Assert.All(AppSettings.DefaultActions(), a => Assert.NotNull(settings.FindAction(a.Id)));
        Assert.Equal(["open"], settings.Menus["folderitem"]);
        Assert.True(settings.Menus.ContainsKey(nameof(ContextKind.DesktopEmpty)));
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.WasMigrated);
    }

    [Theory]
    [InlineData("""["new-project", "new-prompt"]""", new[] { "new-project", "recents", "agents", "quick" })] // New + Prompt left with 4, Active Agents came with 5, Quick Conversation with 6
    [InlineData("""["new-prompt"]""", new[] { "new-prompt" })]
    public void SchemaTwoAddsRecentProjectsOnlyToAnUntouchedDesktopMenu(string savedMenu, string[] expected)
    {
        string json = $$"""{ "SchemaVersion": 1, "Menus": { "DesktopEmpty": {{savedMenu}} } }""";
        var settings = SettingsJson.Deserialize(json)!;

        settings.Normalize();

        Assert.Equal(expected, settings.Menus[nameof(ContextKind.DesktopEmpty)]);
        Assert.NotNull(settings.FindAction("recents"));
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Theory]
    [InlineData("""["open", "continue", "resume"]""", new[] { "open", "ask", "continue", "resume" })]
    [InlineData("""["open", "resume"]""", new[] { "open", "resume" })]
    public void SchemaThreeAddsAskOnlyToUntouchedMenus(string savedMenu, string[] expected)
    {
        string json = $$"""{ "SchemaVersion": 2, "Menus": { "FolderItem": {{savedMenu}} } }""";
        var settings = SettingsJson.Deserialize(json)!;

        settings.Normalize();

        Assert.Equal(expected, settings.Menus[nameof(ContextKind.FolderItem)]);
        Assert.Equal(AppSettings.DefaultMenus()[nameof(ContextKind.FileItem)], settings.Menus[nameof(ContextKind.FileItem)]);
        Assert.Equal("@{file}", settings.FindAgent(BuiltInAgents.ClaudeCodeId)!.FileMention);
    }

    [Fact]
    public void SchemaFourMovesNewWithPromptIntoTheNewProjectCard()
    {
        const string json = """
            {
              "SchemaVersion": 3,
              "Agents": [ { "Id": "claude-code", "Capabilities": { "Open": [], "Prompt": ["{prompt}"] } } ],
              "Actions": [ { "Id": "new-project", "Label": "@Action.NewProject", "Input": "FolderName", "Capability": "Open" } ],
              "Menus": { "DesktopEmpty": ["new-project", "new-prompt", "recents"], "ExplorerEmpty": ["new-prompt", "open-here"] }
            }
            """;
        var settings = SettingsJson.Deserialize(json)!;

        settings.Normalize();

        Assert.Equal(ActionInput.NewProject, settings.FindAction("new-project")!.Input);
        Assert.Equal(["new-project", "recents", "agents", "quick"], settings.Menus[nameof(ContextKind.DesktopEmpty)]); // Active Agents with 5, Quick Conversation with 6
        Assert.Equal(["new-prompt", "open-here"], settings.Menus[nameof(ContextKind.ExplorerEmpty)]); // changed by the user: kept
        Assert.True(settings.FindAgent(BuiltInAgents.ClaudeCodeId)!.Supports(AgentCapability.Plan));
        Assert.True(settings.NewProjectCard.Templates);
    }

    [Theory]
    [InlineData("""["new-project", "recents"]""", new[] { "new-project", "recents", "agents", "quick" })]
    [InlineData("""["recents", "new-project"]""", new[] { "recents", "new-project" })]
    public void SchemaFiveAddsActiveAgentsOnlyToAnUntouchedDesktopMenu(string savedMenu, string[] expected)
    {
        string json = $$"""{ "SchemaVersion": 4, "Menus": { "DesktopEmpty": {{savedMenu}} } }""";
        var settings = SettingsJson.Deserialize(json)!;

        settings.Normalize();

        Assert.Equal(expected, settings.Menus[nameof(ContextKind.DesktopEmpty)]);
        Assert.Equal(ActionSource.ActiveAgents, settings.FindAction("agents")!.Source);
        Assert.Equal("", settings.ActiveAgents.Hotkey);
    }

    [Theory]
    [InlineData("""["new-project", "recents", "agents"]""", new[] { "new-project", "recents", "agents", "quick" })]
    [InlineData("""["new-project", "agents"]""", new[] { "new-project", "agents" })]
    public void SchemaSixAddsQuickConversationOnlyToAnUntouchedDesktopMenu(string savedMenu, string[] expected)
    {
        string json = $$"""{ "SchemaVersion": 5, "Agents": [ { "Id": "claude-code" } ], "Menus": { "DesktopEmpty": {{savedMenu}} } }""";
        var settings = SettingsJson.Deserialize(json)!;

        settings.Normalize();

        Assert.Equal(expected, settings.Menus[nameof(ContextKind.DesktopEmpty)]);
        Assert.True(settings.FindAction("quick")!.Ephemeral);
        Assert.True(settings.Quick.PromptCard);
        // A profile saved before it gets the built-in way to leave no trace.
        Assert.Equal("1", settings.FindAgent(BuiltInAgents.ClaudeCodeId)!.EphemeralEnvironment!["CLAUDE_CODE_SKIP_PROMPT_HISTORY"]);
    }

    [Fact]
    public void SchemaSevenLetsTheCardsAnswerSearchTheWebOnlyWhereItsArgumentsAreStillTheOldDefault()
    {
        const string old = """
            ["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
             "--tools", "Read,WebSearch,WebFetch", "--permission-mode", "dontAsk"]
            """;
        string json = $$"""
            { "SchemaVersion": 6, "Agents": [
              { "Id": "claude-code", "Command": "claude", "AnswerArgs": {{old}} },
              { "Id": "other", "Command": "other", "AnswerArgs": ["-p", "--permission-mode", "dontAsk"] } ] }
            """;
        var settings = SettingsJson.Deserialize(json)!;

        settings.Normalize();

        var answer = settings.FindAgent(BuiltInAgents.ClaudeCodeId)!.AnswerArgs!;
        Assert.Equal(BuiltInAgents.ClaudeCode().AnswerArgs, answer);
        Assert.Equal("WebSearch,WebFetch", answer[answer.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("dontAsk", answer);
        Assert.Equal(["-p", "--permission-mode", "dontAsk"], settings.FindAgent("other")!.AnswerArgs);
    }

    [Fact]
    public void TheAgentsCardIsOffUntilTurnedOnAndKeepsTheUsersChoices()
    {
        var fresh = new AppSettings();
        fresh.Normalize();
        Assert.False(fresh.ActiveAgents.Card.Show);
        Assert.Equal(CardPlacement.OnTop, fresh.ActiveAgents.Card.Placement);
        Assert.True(fresh.ActiveAgents.Card.MuteNotifications);

        var saved = SettingsJson.Deserialize("""
            { "ActiveAgents": { "Card": { "Show": true, "Placement": "OnDesktop", "Pinned": true, "Hotkey": " Ctrl+Alt+K " } } }
            """)!;
        saved.Normalize();
        var card = SettingsJson.Deserialize(SettingsJson.Serialize(saved))!.ActiveAgents.Card; // as after a restart
        Assert.True(card.Show && card.Pinned);
        Assert.Equal(CardPlacement.OnDesktop, card.Placement);
        Assert.Equal("Ctrl+Alt+K", card.Hotkey);

        var unknown = SettingsJson.Deserialize("""{ "ActiveAgents": { "Card": { "Placement": "Sideways" } } }""")!;
        Assert.Equal(CardPlacement.OnTop, unknown.ActiveAgents.Card.Placement); // a newer version's place
    }

    [Fact]
    public void OnlyTheFirstRunCreatesTheSettingsFile()
    {
        var folder = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            bool CreatedNew()
            {
                using var store = new SettingsStore(new SynchronizationContext(), folder.FullName);
                store.Load();
                return store.CreatedNew;
            }

            Assert.True(CreatedNew()); // no settings.json yet: the first run, which shows how to open the menu
            Assert.False(CreatedNew()); // every later start finds the file
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void SavedPromptsAreOffUntilTurnedOnAndAtMostFiveArePinned()
    {
        var fresh = new AppSettings();
        fresh.Normalize();
        Assert.False(fresh.Prompts.Enabled);
        Assert.Empty(fresh.Prompts.Pinned);

        var saved = SettingsJson.Deserialize("""
            { "Prompts": { "Enabled": true, "Pinned": [" Review ", "review", "", "Tests", "Explain", "Docs", "Fix", "Sixth"] } }
            """)!;
        saved.Normalize();
        var prompts = SettingsJson.Deserialize(SettingsJson.Serialize(saved))!.Prompts; // as after a restart
        Assert.True(prompts.Enabled);
        Assert.Equal(["Review", "Tests", "Explain", "Docs", "Fix"], prompts.Pinned);
    }

    [Fact]
    public void KeysThatDifferOnlyInCaseAreReadAsOneAndTheLaterWins()
    {
        var folder = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "settings.json"), """
                {
                  "Menus": { "FolderItem": ["open"], "folderitem": ["ask"] },
                  "Agents": [ { "Id": "claude-code",
                                "Capabilities": { "Prompt": ["-a"], "prompt": ["-b"] },
                                "EphemeralEnvironment": { "QUIET": "1", "quiet": "2" } } ]
                }
                """);
            using var store = new SettingsStore(new SynchronizationContext(), folder.FullName);

            store.Load(); // threw before, and Rondel could not start

            Assert.Null(store.LoadError);
            Assert.Equal(["ask"], store.Current.Menus["FolderItem"]);
            var agent = store.Current.FindAgent("claude-code")!;
            Assert.Equal(["-b"], agent.Capabilities["Prompt"]);
            Assert.Equal("2", agent.EphemeralEnvironment!["QUIET"]);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ProjectsRootExpandsTheHomeFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(home, "Code"), new AppSettings { ProjectsRoot = "~/Code" }.ResolveProjectsRoot());
        Assert.Equal(Path.Combine(home, "Projects"), new AppSettings().ResolveProjectsRoot());
    }

    [Fact]
    public void SessionsAreOfferedOnlyWhenThereIsAChoice()
    {
        var settings = new AppSettings();
        settings.Normalize();
        var recents = MenuBuilder.Build(new TriggerContext(ContextKind.DesktopEmpty, @"C:\Projects", null, "test"), settings)
            .Items.Single(i => i.Action.Id == "recents");
        var now = DateTime.UtcNow;
        IReadOnlyList<SessionEntry> two = [new("s2", "Second", now), new("s1", "First", now.AddHours(-1))];
        string alpha = Path.Combine(Path.GetTempPath(), "work", "alpha");
        string beta = Path.Combine(Path.GetTempPath(), "work", "beta");

        var projects = MenuBuilder.ProjectItems(recents,
            [new FolderActivity(alpha, now, 2, "s2"), new FolderActivity(beta, now.AddHours(-2), 1, "b1")],
            directory => directory == alpha ? two : []);

        Assert.Equal(["alpha", "beta"], projects.Select(p => p.Label));
        Assert.Equal(["s2", "b1"], projects.Select(p => p.SessionId));
        Assert.Equal(["s2", "s1"], projects[0].Children.Select(c => c.SessionId));
        Assert.All(projects[0].Children, c => Assert.Equal(alpha, c.Directory));
        Assert.Empty(projects[1].Children);
        Assert.Empty(MenuBuilder.SessionItems(projects[1], [new("b1", "Only", now)]));
    }

    [Fact]
    public void OnlyTheSlicesThatHoldItemsRunNothingThemselves()
    {
        var settings = new AppSettings();
        settings.Normalize();
        var menu = MenuBuilder.Build(new TriggerContext(ContextKind.DesktopEmpty, @"C:\Projects", null, "test"), settings);
        var recents = menu.Items.Single(i => i.Action.Id == "recents");
        string alpha = Path.Combine(Path.GetTempPath(), "work", "alpha");
        var project = MenuBuilder.ProjectItems(recents, [new FolderActivity(alpha, DateTime.UtcNow, 1, "a1")], _ => [])[0];

        Assert.Equal(["new-project", "quick"], menu.Items.Where(MenuBuilder.RunsItself).Select(i => i.Action.Id));
        Assert.True(MenuBuilder.RunsItself(project)); // continues the project
        Assert.True(MenuBuilder.RunsItself(menu.Items.Single(MenuBuilder.ListsActiveAgents) with { ActivityId = "agent" }));
    }

    [Fact]
    public void MenuHidesActionsTheAgentCannotDo()
    {
        var settings = new AppSettings();
        settings.Agents.Add(new AgentProfile { Id = "minimal", Name = "Minimal", Command = "m", Capabilities = { ["Open"] = [] } });
        settings.DefaultAgent = "minimal";
        settings.Normalize();
        var context = new TriggerContext(ContextKind.ExplorerEmpty, @"C:\work", null, "test");

        var menu = MenuBuilder.Build(context, settings);

        Assert.Equal(["new-project", "open-here"], menu.Items.Select(i => i.Action.Id));
        Assert.All(menu.Items, i => Assert.Equal("minimal", i.Agent.Id));
    }

    [Fact]
    public void BuiltInAgentsCanBeFollowedUnlessTurnedOff()
    {
        const string json = """
            {
              "Agents": [
                { "Id": "claude-code", "Command": "cc" },
                { "Id": "quiet", "Command": "claude", "ActivityArgs": [] },
                { "Id": "other", "Command": "other" }
              ]
            }
            """;
        var settings = SettingsJson.Deserialize(json)!;

        settings.Normalize();

        Assert.Equal(["--settings", "{hooks}"], settings.FindAgent("claude-code")!.ActivityArgs);
        Assert.Empty(settings.FindAgent("quiet")!.ActivityArgs!);
        Assert.Null(settings.FindAgent("other")!.ActivityArgs);
        Assert.True(settings.ActiveAgents.Follow);
        Assert.True(settings.ActiveAgents.NotifyAsks);
        Assert.True(settings.ActiveAgents.NotifyFinished);
    }
}
