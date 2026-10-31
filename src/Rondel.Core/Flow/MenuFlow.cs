using System.ComponentModel;
using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Platform;
using Rondel.Core.Projects;
using Rondel.Core.Settings;

namespace Rondel.Core.Flow;

/// <summary>
/// The part of "trigger → context → menu → action → launch" that is the same on every platform. The platform
/// turns a trigger into a <see cref="TriggerContext"/>, shows the menu this class builds (filling in
/// <see cref="MenuDetails"/> when they arrive) and reports the chosen item; this class decides what happens
/// next. Call it on the UI thread.
/// </summary>
/// <param name="settings">The current settings; read on every call, so changes apply at once.</param>
/// <param name="updateSettings">Changes and saves the settings (the New Project card's last choice).</param>
/// <param name="templates">Where new projects' templates come from; <see cref="ProjectTemplates.Default"/> when null.</param>
public sealed class MenuFlow(IMenuHost host, ITerminalLauncher launcher, IFolderNameValidator folderNames, Func<AppSettings> settings,
    Action<Action<AppSettings>>? updateSettings = null, ProjectTemplates? templates = null)
{
    readonly ProjectTemplates _templates = templates ?? ProjectTemplates.Default;

    /// <returns>The menu for <paramref name="context"/>, or null when no action is configured for it.</returns>
    public MenuModel? Build(TriggerContext context)
    {
        var model = MenuBuilder.Build(context, settings());
        if (model.Items.Count > 0)
            return model;
        Log.Warn($"No actions configured for {context.Kind}; check Menus in settings.json");
        return null;
    }

    /// <summary>A context for the projects folder, for when nothing useful is under the pointer (the hotkey's fallback).</summary>
    public TriggerContext ProjectsContext(ContextKind kind, string reason)
    {
        string projects = settings().ResolveProjectsRoot();
        return new TriggerContext(kind, projects, Path.GetFileName(projects), reason);
    }

    /// <summary>An item was chosen: start its agent, or ask for a folder name (and a prompt) first.</summary>
    public void Invoke(MenuModel model, MenuItemModel item)
    {
        var action = item.Action;
        // Recent projects and their sessions carry their own folder.
        string? directory = item.Directory ?? model.Context.TargetDir;
        if (action.Source == ActionSource.RecentProjects && item.Directory is null)
            return; // the Recent Projects slice itself only opens its items
        Log.Info($"Action '{action.Id}' ({item.Agent.Id}) for {model.Context.Kind} '{directory}'");
        if (directory is null)
        {
            host.CloseMenu(restoreFocus: true);
            return;
        }

        if (action.Input == ActionInput.None)
        {
            host.CloseMenu(restoreFocus: false);
            Start(item.Agent, action, directory, prompt: null, item.SessionId);
            return;
        }

        if (action.Input == ActionInput.Prompt)
        {
            string? file = action.MentionFile ? model.Context.FilePath : null;
            host.StartInput(new InputSpec(
                item.Label,
                item.Glyph,
                directory,
                item.Agent.Name,
                AskPrompt: true,
                ValidateName: _ => null,
                Submit: (_, prompt) =>
                {
                    host.CloseMenu(restoreFocus: false);
                    Start(item.Agent, action, directory, WithMention(item.Agent, file, prompt));
                    return null;
                })
            { AskName = false, Location = file ?? directory });
            return;
        }

        if (action.Input == ActionInput.NewProject)
        {
            var agent = item.Agent;
            var card = settings().NewProjectCard;
            host.StartNewProject(new NewProjectSpec(
                item.Label,
                item.Glyph,
                directory,
                agent.Name,
                card.Templates ? _templates.List() : [],
                settings().NewProject.Clone(),
                CanPrompt: card.Prompt && agent.Supports(AgentCapability.Prompt),
                CanPlan: card.Plan && agent.Supports(AgentCapability.Plan),
                ValidateName: name => folderNames.Validate(directory, name),
                Submit: request => CreateProject(agent, action, directory, request))
            {
                CanGit = card.Git,
                OffersTemplates = card.Templates,
                TemplatesDirectory = card.Templates ? _templates.Directory : null,
            });
            return;
        }

        host.StartInput(new InputSpec(
            item.Label,
            item.Glyph,
            directory,
            item.Agent.Name,
            AskPrompt: action.Input == ActionInput.FolderNameAndPrompt,
            ValidateName: name => folderNames.Validate(directory, name),
            Submit: (name, prompt) => CreateFolderAndStart(item.Agent, action, directory, name, prompt)));
    }

    /// <returns>An inline error message, or null when the folder exists and the agent was started.</returns>
    string? CreateFolderAndStart(AgentProfile agent, ActionDefinition action, string parent, string name, string? prompt)
    {
        if (folderNames.Validate(parent, name) is { } error)
            return error;
        string path = Path.Combine(parent, name);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"Creating '{path}' failed", ex);
            return Loc.Format("Input.CreateFailed", ex.Message);
        }
        Log.Info($"Created '{path}'");
        host.CloseMenu(restoreFocus: false);
        Start(agent, action, path, prompt);
        return null;
    }

    /// <returns>An inline error message, or null when the project exists and the agent was started.</returns>
    string? CreateProject(AgentProfile agent, ActionDefinition action, string parent, NewProjectRequest request)
    {
        if (folderNames.Validate(parent, request.Name) is { } error)
            return error;
        string path = Path.Combine(parent, request.Name);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"Creating '{path}' failed", ex);
            return Loc.Format("Input.CreateFailed", ex.Message);
        }
        // The choice is remembered as made; what the card no longer offers is not applied.
        var options = request.Options;
        var card = settings().NewProjectCard;
        bool usePrompt = options.Prompt && card.Prompt, git = options.Git && card.Git, plan = options.Plan && card.Plan;
        // In the order they are offered, so a later template's file wins.
        var chosen = card.Templates
            ? _templates.List().Where(name => options.Templates.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList()
            : [];
        Log.Info($"Created '{path}' (prompt {usePrompt}, git {git}, plan {plan}, templates [{string.Join(", ", chosen)}])");
        try
        {
            _templates.CopyInto(path, chosen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The project exists by now: start it anyway and say what is missing.
            Log.Warn($"Copying templates into '{path}' failed", ex);
            host.ShowWarning(Loc.Format("NewProject.TemplatesFailed", request.Name, ex.Message));
        }

        updateSettings?.Invoke(s => s.NewProject = options.Clone());
        host.CloseMenu(restoreFocus: false);
        string? prompt = usePrompt && !string.IsNullOrWhiteSpace(request.Prompt) ? request.Prompt : null;
        var start = prompt is null ? action : new ActionDefinition { Id = action.Id, Capability = AgentCapability.Prompt, ExtraArgs = action.ExtraArgs };
        Start(agent, start, path, prompt, planMode: plan, initGit: git);
        return null;
    }

    /// <summary>The prompt, after a mention of <paramref name="file"/> in the agent's syntax ("@Program.cs explain this").</summary>
    static string? WithMention(AgentProfile agent, string? file, string? prompt) =>
        file is null || string.IsNullOrEmpty(agent.FileMention)
            ? prompt
            : $"{FileMention.Format(agent.FileMention, Path.GetFileName(file))} {prompt}".TrimEnd();

    void Start(AgentProfile agent, ActionDefinition action, string directory, string? prompt, string? sessionId = null,
        bool planMode = false, bool initGit = false)
    {
        if (LaunchPlanner.Create(agent, action, directory, prompt, sessionId, planMode) is not { } plan)
        {
            Log.Warn($"Agent '{agent.Id}' does not support {action.Capability}");
            return;
        }
        try
        {
            launcher.Launch(plan with { InitGit = initGit }, settings());
        }
        catch (Exception ex) when (ex is LaunchException or Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error($"Launching {agent.Name} in '{directory}' failed", ex);
            host.ShowWarning(Loc.Format("Launch.Failed", agent.Name, ex.Message));
        }
    }
}
