using System.ComponentModel;
using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Platform;
using Rondel.Core.Settings;

namespace Rondel.Core.Flow;

/// <summary>
/// The part of "trigger → context → menu → action → launch" that is the same on every platform. The platform
/// turns a trigger into a <see cref="TriggerContext"/>, shows the menu this class builds (filling in
/// <see cref="MenuDetails"/> when they arrive) and reports the chosen item; this class decides what happens
/// next. Call it on the UI thread.
/// </summary>
/// <param name="settings">The current settings; read on every call, so changes apply at once.</param>
public sealed class MenuFlow(IMenuHost host, ITerminalLauncher launcher, IFolderNameValidator folderNames, Func<AppSettings> settings)
{
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

    void Start(AgentProfile agent, ActionDefinition action, string directory, string? prompt, string? sessionId = null)
    {
        if (LaunchPlanner.Create(agent, action, directory, prompt, sessionId) is not { } plan)
        {
            Log.Warn($"Agent '{agent.Id}' does not support {action.Capability}");
            return;
        }
        try
        {
            launcher.Launch(plan, settings());
        }
        catch (Exception ex) when (ex is LaunchException or Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error($"Launching {agent.Name} in '{directory}' failed", ex);
            host.ShowWarning(Loc.Format("Launch.Failed", agent.Name, ex.Message));
        }
    }
}
