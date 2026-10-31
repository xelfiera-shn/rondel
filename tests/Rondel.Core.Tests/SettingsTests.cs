using System.Text.Json;
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
        var settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options)!;

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
    [InlineData("""["new-project", "new-prompt"]""", new[] { "new-project", "new-prompt", "recents" })]
    [InlineData("""["new-prompt"]""", new[] { "new-prompt" })]
    public void SchemaTwoAddsRecentProjectsOnlyToAnUntouchedDesktopMenu(string savedMenu, string[] expected)
    {
        string json = $$"""{ "SchemaVersion": 1, "Menus": { "DesktopEmpty": {{savedMenu}} } }""";
        var settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options)!;

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
        var settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options)!;

        settings.Normalize();

        Assert.Equal(expected, settings.Menus[nameof(ContextKind.FolderItem)]);
        Assert.Equal(AppSettings.DefaultMenus()[nameof(ContextKind.FileItem)], settings.Menus[nameof(ContextKind.FileItem)]);
        Assert.Equal("@{file}", settings.FindAgent(BuiltInAgents.ClaudeCodeId)!.FileMention);
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
}
