using System.Text.Json;
using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Menus;
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
