using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Rondel.Core.Agents;
using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Menus;
using Rondel.Core.Settings;
using Rondel.Core.Themes;

namespace Rondel.Core.Tests;

/// <summary>
/// settings.json is read and written by generated code (no reflection, for NativeAOT). These tests compare it
/// with the reflection-based serializer it replaced, set up exactly as before, so a file reads and writes the
/// same. The intended differences: Glyph is no longer written, and unknown enum values no longer make the
/// file unreadable.
/// </summary>
public class SettingsJsonTests
{
    /// <summary>The options settings.json used before the generated serializer.</summary>
    static readonly JsonSerializerOptions Reflection = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    static AppSettings Customized()
    {
        var settings = new AppSettings
        {
            Language = "tr",
            ProjectsRoot = Path.Combine(Path.GetTempPath(), "Projeler", "Öğrenci İşleri"),
            HoldMs = 450,
            Hotkey = "Ctrl+Alt+Space",
            Terminal = TerminalKind.Console,
            Shell = ShellKind.WindowsPowerShell,
            Paused = true,
            Appearance = new AppearanceOptions { Theme = "graphite", Mode = ColorMode.Light, AccentFrom = "#12AB34" },
            NewProject = new NewProjectOptions { Prompt = true, Git = true, Templates = ["web", "şablon"] },
        };
        settings.Agents.Add(new AgentProfile
        {
            Id = "other",
            Name = "Diğer Ajan",
            Command = "other --flag \"a b\"",
            Capabilities = { ["Open"] = [], ["Prompt"] = ["-p", "{prompt}"] },
        });
        settings.Actions.Add(new ActionDefinition
        {
            Id = "plan",
            Label = "Plan",
            Icon = "E8FD",
            Input = ActionInput.Prompt,
            Capability = AgentCapability.Prompt,
            Agent = "other",
            ExtraArgs = ["--permission-mode", "plan"],
            Source = ActionSource.RecentProjects,
            MentionFile = true,
        });
        settings.Menus["FolderItem"] = ["open", "plan"];
        settings.Normalize();
        return settings;
    }

    public static TheoryData<string> Files => ["default", "customized"];

    static AppSettings Sample(string name)
    {
        if (name == "customized")
            return Customized();
        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void WritesWhatTheReflectionSerializerWrote(string name)
    {
        var settings = Sample(name);

        string generated = SettingsJson.Serialize(settings);
        string reflection = JsonSerializer.Serialize(settings, Reflection);

        Assert.Equal(reflection, generated);
        Assert.DoesNotContain("\"Glyph\"", generated, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ReadsWhatTheReflectionSerializerRead(string name)
    {
        string json = JsonSerializer.Serialize(Sample(name), Reflection);

        var generated = SettingsJson.Deserialize(json)!;
        var reflection = JsonSerializer.Deserialize<AppSettings>(json, Reflection)!;

        Assert.Equal(SettingsJson.Serialize(reflection), SettingsJson.Serialize(generated));
    }

    [Fact]
    public void ReadsFilesThatStillHaveGlyph()
    {
        // Written before Glyph was left out.
        const string json = """{ "Actions": [ { "Id": "open", "Icon": "E756", "Capability": "Open", "Glyph": "\ue756" } ] }""";

        var settings = SettingsJson.Deserialize(json)!;

        Assert.Equal('\uE756', settings.FindAction("open")!.Glyph);
        Assert.DoesNotContain("\"Glyph\"", SettingsJson.Serialize(settings), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsAHandEditedFileLikeBefore()
    {
        const string json = """
            {
              // comments, trailing commas, any case for names and enum values, numbers for enums
              "terminal": "console",
              "SHELL": 2,
              "projectsRoot": "~/Projeler/Öğrenci İşleri",
              "appearance": { "mode": "dark", },
              "actions": [ { "id": "x", "capability": "prompt", "input": 3, "hint": "lastSession" }, ],
            }
            """;

        var generated = SettingsJson.Deserialize(json)!;
        var reflection = JsonSerializer.Deserialize<AppSettings>(json, Reflection)!;

        Assert.Equal(TerminalKind.Console, generated.Terminal);
        Assert.Equal(ShellKind.WindowsPowerShell, generated.Shell);
        Assert.Equal(ColorMode.Dark, generated.Appearance.Mode);
        Assert.Equal(ActionInput.Prompt, generated.Actions[0].Input);
        Assert.Equal(SettingsJson.Serialize(reflection), SettingsJson.Serialize(generated));
    }

    [Fact]
    public void WritesTurkishCharactersAndEnumNamesAsTheyAre()
    {
        var settings = Customized();

        string json = SettingsJson.Serialize(settings);

        Assert.Contains("Öğrenci İşleri", json, StringComparison.Ordinal);
        Assert.Contains("\"Diğer Ajan\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Terminal\": \"Console\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Mode\": \"Light\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEnumValuesReadAsTheDefault()
    {
        // Written by a newer Rondel: values this version does not know, next to ones it does.
        const string json = """
            {
              "HoldMs": 500,
              "Terminal": "Ghostty",
              "Shell": 42,
              "Appearance": { "Mode": "Sepia", "Theme": "graphite" },
              "Actions": [ { "Id": "x", "Capability": "Teleport", "Input": "Dance", "Hint": null, "Source": { "Kind": "Cloud" } } ]
            }
            """;
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<AppSettings>(json, Reflection)); // before: unreadable

        var settings = SettingsJson.Deserialize(json)!;
        settings.Normalize();

        Assert.Equal(500, settings.HoldMs);
        Assert.Equal(TerminalKind.Auto, settings.Terminal);
        Assert.Equal(ShellKind.Auto, settings.Shell);
        Assert.Equal(ColorMode.Auto, settings.Appearance.Mode);
        Assert.Equal("graphite", settings.Appearance.Theme);
        var action = settings.FindAction("x")!;
        Assert.Equal(AgentCapability.Open, action.Capability);
        Assert.Equal(ActionInput.None, action.Input);
        Assert.Equal(ActionSource.Context, action.Source);
    }

    [Fact]
    public void EveryEnumPropertyDefaultsToItsFirstValue()
    {
        // An unknown value reads as default(T), which must be what a missing value means.
        var settings = new AppSettings();
        var action = new ActionDefinition();

        Assert.Equal(default, settings.Terminal);
        Assert.Equal(default, settings.Shell);
        Assert.Equal(default, settings.Appearance.Mode);
        Assert.Equal(default, action.Input);
        Assert.Equal(default, action.Capability);
        Assert.Equal(default, action.Hint);
        Assert.Equal(default, action.Source);
    }

    [Fact]
    public void EveryEnumInTheSettingsHasAConverter()
    {
        // Without one, a new enum property would be written as a number.
        var enums = new HashSet<Type>();
        var seen = new HashSet<Type>();
        void Walk(Type type)
        {
            if (!seen.Add(type))
                return;
            if (type.IsEnum)
                enums.Add(type);
            else if (type.IsGenericType)
                type.GetGenericArguments().ToList().ForEach(Walk);
            else if (type.Namespace?.StartsWith("Rondel.", StringComparison.Ordinal) == true)
                type.GetProperties().Where(p => p.CanWrite).ToList().ForEach(p => Walk(p.PropertyType));
        }
        Walk(typeof(AppSettings));

        Assert.Contains(typeof(TerminalKind), enums);
        Assert.Contains(typeof(AgentCapability), enums); // inside the list of actions
        Assert.Contains(typeof(ColorMode), enums); // inside Appearance
        Assert.All(enums, type => Assert.Contains(SettingsJson.Options.Converters, c => c.CanConvert(type)));
    }

    [Fact]
    public void DictionariesIgnoreCaseAfterNormalize()
    {
        const string json = """{ "Agents": [ { "Id": "claude-code", "Capabilities": { "open": [], "PROMPT": ["{prompt}"] } } ], "Menus": { "folderitem": ["open"] } }""";

        var settings = SettingsJson.Deserialize(json)!;
        settings.Normalize();

        var agent = settings.FindAgent(BuiltInAgents.ClaudeCodeId)!;
        Assert.True(agent.Supports(AgentCapability.Open));
        Assert.True(agent.Supports(AgentCapability.Prompt));
        Assert.Equal(["open"], settings.Menus["FolderItem"]);
    }

    [Fact]
    public void CloneIsADeepCopy()
    {
        var settings = Customized();

        var clone = settings.Clone();
        clone.Menus["FolderItem"].Add("ask");

        Assert.Equal(SettingsJson.Serialize(settings), SettingsJson.Serialize(Customized()));
        Assert.NotEqual(SettingsJson.Serialize(settings), SettingsJson.Serialize(clone));
    }

    [Theory]
    [InlineData(@"C:\\Users\\\u015ferif", @"C:\Users\şerif")]
    [InlineData("/Users/ay\u015fe/Projeler", "/Users/ayşe/Projeler")]
    [InlineData(@"bad \q escape", null)]
    [InlineData(@"\ud800", null)]
    public void SessionFolderIsUnescapedWithoutReflection(string escaped, string? expected) =>
        Assert.Equal(expected, ClaudeCodeSessionStore.UnescapeJsonString(escaped));
}
