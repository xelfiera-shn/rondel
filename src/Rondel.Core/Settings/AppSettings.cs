using System.Text.Json;
using System.Text.Json.Serialization;
using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Menus;
using Rondel.Core.Sessions;
using Rondel.Core.Themes;

namespace Rondel.Core.Settings;

public enum TerminalKind { Auto, WindowsTerminal, Console }

public enum ShellKind { Auto, Pwsh, WindowsPowerShell }

/// <summary>
/// Contents of settings.json. Anything a user may get wrong in a hand-edited file is repaired by
/// <see cref="Normalize"/>, which also brings older files up to <see cref="CurrentSchemaVersion"/>.
/// </summary>
public sealed class AppSettings
{
    public const int MinHoldMs = 150;
    public const int MaxHoldMs = 2000;

    /// <summary>%USERPROFILE%\Projects on Windows, ~/Projects elsewhere.</summary>
    public static string DefaultProjectsRoot { get; } = OperatingSystem.IsWindows() ? @"%USERPROFILE%\Projects" : "~/Projects";

    /// <summary>
    /// Bumped whenever a default changes, so <see cref="Normalize"/> can migrate saved files.
    /// A migration only touches values that are still at the old default.
    /// </summary>
    public const int CurrentSchemaVersion = 4;

    /// <summary>0 for a file written by hand without the field.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>"auto", "en" or "tr".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Target of the desktop actions. Environment variables (%NAME%) and a leading ~ are expanded.</summary>
    public string ProjectsRoot { get; set; } = DefaultProjectsRoot;

    public int HoldMs { get; set; } = 300;

    /// <summary>e.g. "Ctrl+Alt+Space"; empty = disabled.</summary>
    public string Hotkey { get; set; } = "";

    public TerminalKind Terminal { get; set; } = TerminalKind.Auto;

    public ShellKind Shell { get; set; } = ShellKind.Auto;

    public bool Paused { get; set; }

    public bool DebugLogging { get; set; }

    /// <summary>Agent used by actions that do not name one.</summary>
    public string DefaultAgent { get; set; } = BuiltInAgents.ClaudeCodeId;

    public List<AgentProfile> Agents { get; set; } = BuiltInAgents.Create();

    /// <summary>The menu's theme, dark or light mode, and accent colors.</summary>
    public AppearanceOptions Appearance { get; set; } = new();

    /// <summary>How the New Project card starts: the choice made last time.</summary>
    public NewProjectOptions NewProject { get; set; } = new();

    /// <summary>Which options the New Project card offers (all of them unless turned off in Settings).</summary>
    public NewProjectCardOptions NewProjectCard { get; set; } = new();

    /// <summary>Context name (DesktopEmpty, ExplorerEmpty, FolderItem, FileItem) → action ids, clockwise from the top.</summary>
    public Dictionary<string, List<string>> Menus { get; set; } = DefaultMenus();

    public List<ActionDefinition> Actions { get; set; } = DefaultActions();

    /// <summary>Set by <see cref="Normalize"/> when the file was upgraded and should be saved back.</summary>
    [JsonIgnore]
    public bool WasMigrated { get; private set; }

    public string ResolveProjectsRoot()
    {
        string raw = string.IsNullOrWhiteSpace(ProjectsRoot) ? DefaultProjectsRoot : ProjectsRoot.Trim();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (raw == "~" || raw.StartsWith("~/", StringComparison.Ordinal) || raw.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            raw = home + raw[1..];
        string expanded = Environment.ExpandEnvironmentVariables(raw);
        if (!Path.IsPathRooted(expanded))
            expanded = Path.Combine(home, expanded);
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return expanded;
        }
    }

    public ActionDefinition? FindAction(string id) =>
        Actions.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    public AgentProfile? FindAgent(string? id) =>
        Agents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The agent an action starts: its own, or the default agent.</summary>
    public AgentProfile? AgentFor(ActionDefinition action) =>
        FindAgent(string.IsNullOrWhiteSpace(action.Agent) ? DefaultAgent : action.Agent);

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsJson.Options), SettingsJson.Options)!;

    /// <summary>Repairs values a hand-edited file may get wrong and adds agents and actions introduced by newer versions.</summary>
    public void Normalize()
    {
        Language = Language?.Trim().ToLowerInvariant() is "en" or "tr" ? Language.Trim().ToLowerInvariant() : "auto";
        if (string.IsNullOrWhiteSpace(ProjectsRoot))
            ProjectsRoot = DefaultProjectsRoot;
        HoldMs = Math.Clamp(HoldMs, MinHoldMs, MaxHoldMs);
        Hotkey = Hotkey?.Trim() ?? "";
        NewProject ??= new();
        NewProjectCard ??= new();
        Appearance ??= new();
        Appearance.Normalize();
        NewProject.Templates = (NewProject.Templates ?? []).Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        NormalizeAgents();
        NormalizeActions();

        Menus = Menus is null
            ? DefaultMenus()
            : new Dictionary<string, List<string>>(Menus, StringComparer.OrdinalIgnoreCase);
        foreach (var (context, ids) in DefaultMenus())
        {
            if (!Menus.TryGetValue(context, out var list) || list is null)
                Menus[context] = ids;
        }

        Migrate();
    }

    void NormalizeAgents()
    {
        Agents ??= [];
        Agents.RemoveAll(a => a is null || string.IsNullOrWhiteSpace(a.Id));
        foreach (var agent in Agents)
        {
            var builtIn = BuiltInAgents.Find(agent.Id);
            agent.Id = agent.Id.Trim();
            agent.Name = string.IsNullOrWhiteSpace(agent.Name) ? builtIn?.Name ?? agent.Id : agent.Name.Trim();
            agent.Command = string.IsNullOrWhiteSpace(agent.Command) ? builtIn?.Command ?? agent.Id : agent.Command.Trim();
            agent.Capabilities = new Dictionary<string, List<string>>(
                (agent.Capabilities ?? builtIn?.Capabilities ?? new Dictionary<string, List<string>>()).Where(c => c.Value is not null),
                StringComparer.OrdinalIgnoreCase);
            // Missing in files written before file mentions existed; an empty string turns them off.
            agent.FileMention = (agent.FileMention ?? builtIn?.FileMention)?.Trim();
        }
        foreach (var builtIn in BuiltInAgents.Create())
        {
            if (FindAgent(builtIn.Id) is null)
                Agents.Add(builtIn);
        }
        if (FindAgent(DefaultAgent) is null)
            DefaultAgent = Agents[0].Id;
    }

    void NormalizeActions()
    {
        Actions ??= [];
        Actions.RemoveAll(a => a is null || string.IsNullOrWhiteSpace(a.Id));
        foreach (var action in Actions)
        {
            action.Label ??= action.Id;
            action.Icon ??= "";
            action.ExtraArgs ??= [];
            action.ExtraArgs.RemoveAll(a => a is null);
        }
        foreach (var builtIn in DefaultActions())
        {
            if (FindAction(builtIn.Id) is null)
                Actions.Add(builtIn);
        }
    }

    void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
            return;
        // 2: Recent Projects joined the desktop menu. Only touch a menu that is still at the old default;
        // the action itself was added by NormalizeActions like any new built-in action.
        if (SchemaVersion < 2 && Menus[nameof(ContextKind.DesktopEmpty)].SequenceEqual(["new-project", "new-prompt"], StringComparer.OrdinalIgnoreCase))
            Menus[nameof(ContextKind.DesktopEmpty)].Add("recents");
        // 3: Ask joined the folder and Explorer menus, next to Open. The file menu is new, so it comes with its default.
        if (SchemaVersion < 3)
        {
            InsertIfDefault(nameof(ContextKind.FolderItem), ["open", "continue", "resume"], "ask", after: "open");
            InsertIfDefault(nameof(ContextKind.ExplorerEmpty), ["new-project", "new-prompt", "open-here", "continue-here", "resume-here"],
                "ask-here", after: "open-here");
        }
        // 4: New Project opens the New Project card, which also asks for a prompt, so New + Prompt left the
        // default menus. Built-in agents get their plan mode, which the card can turn on.
        if (SchemaVersion < 4)
        {
            if (FindAction("new-project") is { Input: ActionInput.FolderName, Capability: AgentCapability.Open } newProject)
                newProject.Input = ActionInput.NewProject;
            RemoveIfDefault(nameof(ContextKind.DesktopEmpty), ["new-project", "new-prompt", "recents"], "new-prompt");
            RemoveIfDefault(nameof(ContextKind.ExplorerEmpty), ["new-project", "new-prompt", "open-here", "ask-here", "continue-here", "resume-here"],
                "new-prompt");
            foreach (var agent in Agents)
            {
                if (BuiltInAgents.Find(agent.Id) is { } builtIn && builtIn.TryGetArguments(AgentCapability.Plan, out var plan))
                    agent.Capabilities.TryAdd(nameof(AgentCapability.Plan), [.. plan]);
            }
        }
        SchemaVersion = CurrentSchemaVersion;
        WasMigrated = true;
    }

    void InsertIfDefault(string context, string[] oldDefault, string id, string after)
    {
        var menu = Menus[context];
        if (menu.SequenceEqual(oldDefault, StringComparer.OrdinalIgnoreCase))
            menu.Insert(menu.FindIndex(i => string.Equals(i, after, StringComparison.OrdinalIgnoreCase)) + 1, id);
    }

    void RemoveIfDefault(string context, string[] oldDefault, string id)
    {
        var menu = Menus[context];
        if (menu.SequenceEqual(oldDefault, StringComparer.OrdinalIgnoreCase))
            menu.RemoveAll(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase));
    }

    public static Dictionary<string, List<string>> DefaultMenus() => new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(ContextKind.DesktopEmpty)] = ["new-project", "recents"],
        [nameof(ContextKind.ExplorerEmpty)] = ["new-project", "open-here", "ask-here", "continue-here", "resume-here"],
        [nameof(ContextKind.FolderItem)] = ["open", "ask", "continue", "resume"],
        [nameof(ContextKind.FileItem)] = ["ask-file", "open-here", "continue-here", "resume-here"],
    };

    public static List<ActionDefinition> DefaultActions() =>
    [
        new() { Id = "new-project", Label = "@Action.NewProject", Icon = "E8F4", Input = ActionInput.NewProject, Capability = AgentCapability.Open },
        new() { Id = "new-prompt", Label = "@Action.NewWithPrompt", Icon = "E8BD", Input = ActionInput.FolderNameAndPrompt, Capability = AgentCapability.Prompt },
        new() { Id = "open-here", Label = "@Action.OpenHere", Icon = "E756", Capability = AgentCapability.Open },
        new() { Id = "ask-here", Label = "@Action.AskHere", Icon = "E8F2", Input = ActionInput.Prompt, Capability = AgentCapability.Prompt },
        new() { Id = "continue-here", Label = "@Action.ContinueHere", Icon = "E768", Capability = AgentCapability.Continue, Hint = SessionHint.LastSession },
        new() { Id = "resume-here", Label = "@Action.ResumeHere", Icon = "E81C", Capability = AgentCapability.Resume, Hint = SessionHint.SessionCount },
        new() { Id = "open", Label = "@Action.Open", Icon = "E756", Capability = AgentCapability.Open },
        new() { Id = "ask", Label = "@Action.Ask", Icon = "E8F2", Input = ActionInput.Prompt, Capability = AgentCapability.Prompt },
        new() { Id = "continue", Label = "@Action.Continue", Icon = "E768", Capability = AgentCapability.Continue, Hint = SessionHint.LastSession },
        new() { Id = "resume", Label = "@Action.Resume", Icon = "E81C", Capability = AgentCapability.Resume, Hint = SessionHint.SessionCount },
        new() { Id = "recents", Label = "@Action.Recents", Icon = "E823", Capability = AgentCapability.ResumeById, Source = ActionSource.RecentProjects },
        new() { Id = "ask-file", Label = "@Action.AskFile", Icon = "E8F2", Input = ActionInput.Prompt, Capability = AgentCapability.Prompt, MentionFile = true },
    ];
}
