using System.ComponentModel;
using Rondel.Core.Activity;
using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Platform;
using Rondel.Core.Projects;
using Rondel.Core.Prompts;
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
/// <param name="activity">Follows the agents this flow starts, when the platform listens for their hooks; null: none is followed.</param>
/// <param name="prompts">Where the saved prompts come from; <see cref="SavedPrompts.Default"/> when null.</param>
/// <param name="quickDirectory">Where Quick Conversations run; <see cref="AppInfo.QuickDirectory"/> when null.</param>
/// <param name="answers">Starts an agent without a terminal, so a Quick Conversation answers on its card; null: it starts in a
/// terminal.</param>
/// <param name="time">The clock for <see cref="ContinuedKept"/>; the system's when null.</param>
public sealed class MenuFlow(IMenuHost host, ITerminalLauncher launcher, IFolderNameValidator folderNames, Func<AppSettings> settings,
    Action<Action<AppSettings>>? updateSettings = null, ProjectTemplates? templates = null, AgentActivity? activity = null,
    SavedPrompts? prompts = null, string? quickDirectory = null, IAnswerLauncher? answers = null, TimeProvider? time = null)
{
    readonly ProjectTemplates _templates = templates ?? ProjectTemplates.Default;
    readonly SavedPrompts _prompts = prompts ?? SavedPrompts.Default;
    readonly object _quickGate = new();
    readonly List<QuickAnswer> _openAnswers = [];
    readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Quick Conversation sessions continued in a terminal, and when.</summary>
    readonly Dictionary<string, DateTimeOffset> _continued = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a session continued in a terminal is kept at most when nothing says it ended. Its terminal may never run it
    /// (closed at Claude Code's trust question, say), and nothing else would tell the sweep to take it before Rondel restarts.
    /// One that runs by then is kept by Claude Code's list of the sessions it runs.
    /// </summary>
    public static readonly TimeSpan ContinuedKept = TimeSpan.FromHours(1);

    /// <summary>The folder Quick Conversations run in; everything Claude Code keeps for it is left over from one.</summary>
    public string QuickDirectory { get; } = quickDirectory ?? AppInfo.QuickDirectory;

    /// <summary>A Quick Conversation is about to start: the platform sweeps what earlier ones left.</summary>
    public event Action? QuickConversationStarting;

    /// <summary>
    /// A Quick Conversation's card closed without it going on in a terminal: that session (its id) is over, and what it
    /// kept goes with the sweep. Raised on the UI thread.
    /// </summary>
    public event Action<string>? QuickSessionEnded;

    /// <summary>
    /// The Quick Conversation sessions the sweep keeps although no terminal runs them: answers on an open card, and
    /// those continued in a terminal until <see cref="ForgetQuickSession"/>, or for <see cref="ContinuedKept"/> at most.
    /// Safe to read from any thread.
    /// </summary>
    public IReadOnlyCollection<string> QuickSessions
    {
        get
        {
            lock (_quickGate)
            {
                var now = _time.GetUtcNow();
                foreach (string id in _continued.Where(c => now - c.Value >= ContinuedKept).Select(c => c.Key).ToList())
                {
                    _continued.Remove(id);
                    Log.Info($"Quick Conversation {id} was continued in a terminal an hour ago and is not kept any more");
                }
                return [.. _openAnswers.Select(a => a.SessionId).OfType<string>().Concat(_continued.Keys)];
            }
        }
    }

    /// <summary>A card answers a Quick Conversation right now (its id may not be known yet): the folder's files stay.</summary>
    public bool QuickAnswerOpen
    {
        get
        {
            lock (_quickGate)
                return _openAnswers.Count > 0;
        }
    }

    /// <summary>A Quick Conversation continued in a terminal has ended: the sweep need not keep it any more.</summary>
    public void ForgetQuickSession(string sessionId)
    {
        lock (_quickGate)
            _continued.Remove(sessionId);
    }

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

    /// <summary>
    /// The menu the Active Agents shortcut opens: the desktop menu, with the slice to open right away. A desktop menu
    /// without an Active Agents slice gets one, for this time.
    /// </summary>
    public (MenuModel Model, int Slice) AgentsMenu()
    {
        var current = settings();
        var model = MenuBuilder.Build(ProjectsContext(ContextKind.DesktopEmpty, "agents shortcut"), current);
        int slice = model.Items.ToList().FindIndex(MenuBuilder.ListsActiveAgents);
        if (slice >= 0)
            return (model, slice);
        var action = current.Actions.First(a => a.Source == ActionSource.ActiveAgents); // a built-in action, never missing
        var agent = current.AgentFor(action) ?? current.Agents[0];
        var item = new MenuItemModel(action, agent, Loc.Resolve(action.Label), action.Glyph, null, false);
        return (model with { Items = [.. model.Items.Take(MenuBuilder.MaxItems - 1), item] }, Math.Min(model.Items.Count, MenuBuilder.MaxItems - 1));
    }

    /// <summary>Resumes a saved session in its folder with the default agent (the Sessions window's Resume).</summary>
    public void Resume(string directory, string sessionId)
    {
        var current = settings();
        if (current.FindAgent(current.DefaultAgent) is not { } agent)
            return;
        Log.Info($"Resuming session {sessionId} in '{directory}'");
        Start(agent, new ActionDefinition { Id = "resume-session", Capability = AgentCapability.ResumeById, Agent = agent.Id }, directory,
            prompt: null, sessionId);
    }

    /// <summary>
    /// Ask About a Screenshot: the Quick Conversation card with <paramref name="png"/> on it, which starts a Quick Conversation
    /// with the image in the first message. The image is only in memory until then: it is written into the Quick
    /// Conversation folder when the conversation starts, and goes with the folder's sweep.
    /// </summary>
    public void AskAboutScreenshot(byte[] png)
    {
        var current = settings();
        if (current.FindAgent(current.DefaultAgent) is not { } agent || agent.EphemeralEnvironment is not { Count: > 0 }
            || !agent.Supports(AgentCapability.Prompt) || string.IsNullOrEmpty(agent.FileMention))
        {
            Log.Warn($"Agent '{current.DefaultAgent}' cannot start a Quick Conversation about a screenshot");
            host.ShowWarning(Loc.Get("Screenshot.Unsupported"));
            return;
        }
        var action = new ActionDefinition { Id = "screenshot", Icon = "E722", Capability = AgentCapability.Prompt, Agent = agent.Id, Ephemeral = true };
        Log.Info($"Asking about a screenshot ({png.Length} bytes)");
        host.ShowCard(new InputSpec(Loc.Get("Action.Screenshot"), action.Glyph, QuickDirectory, agent.Name, AskPrompt: true,
            ValidateName: _ => null,
            Submit: (_, prompt) =>
            {
                string file;
                try
                {
                    file = SaveScreenshot(png);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Writing the screenshot into '{QuickDirectory}' failed", ex);
                    return Loc.Format("Screenshot.SaveFailed", ex.Message);
                }
                string first = WithMentions(agent, [file], prompt)!;
                if (Answer(agent, action, prompt ?? "", first) is { } answer)
                {
                    host.ShowAnswer(answer);
                    return null;
                }
                host.CloseMenu(restoreFocus: false);
                Start(agent, action, QuickDirectory, first);
                return null;
            })
        {
            AskName = false,
            Image = png,
            Placeholder = Loc.Get("Screenshot.Placeholder"),
            Location = Loc.Get("Quick.Location"),
            SavedPrompts = current.Prompts.Enabled ? _prompts : null,
            PromptNames = current.Prompts.Enabled ? _prompts.List() : [],
        });
    }

    /// <summary>Writes a screenshot into the Quick Conversation folder, under a name of its own; returns its path.</summary>
    string SaveScreenshot(byte[] png)
    {
        Directory.CreateDirectory(QuickDirectory);
        string name = $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}";
        string file = Path.Combine(QuickDirectory, name + ".png");
        for (int i = 2; File.Exists(file); i++)
            file = Path.Combine(QuickDirectory, $"{name}-{i}.png");
        File.WriteAllBytes(file, png);
        return file;
    }

    /// <summary>An item was chosen: start its agent, or ask for a folder name (and a prompt) first.</summary>
    public void Invoke(MenuModel model, MenuItemModel item)
    {
        var action = item.Action;
        // Recent projects and their sessions carry their own folder; a Quick Conversation has its own, wherever the menu opened.
        string? directory = action.Ephemeral ? QuickFolder() : item.Directory ?? model.Context.TargetDir;
        if (action.Source == ActionSource.RecentProjects && item.Directory is null)
            return; // the Recent Projects slice itself only opens its items
        if (action.Source == ActionSource.ActiveAgents)
        {
            // The slice itself only opens its agents; an agent is brought to the front, nothing is started.
            if (item.ActivityId is { } id)
            {
                Log.Info($"Bringing agent {id} in '{item.Directory}' to the front");
                host.CloseMenu(restoreFocus: false);
                host.FocusAgent(id);
            }
            return;
        }
        Log.Info($"Action '{action.Id}' ({item.Agent.Id}) for {model.Context.Kind} '{directory}'");
        if (directory is null)
        {
            host.CloseMenu(restoreFocus: true);
            return;
        }

        if (item.SavedPrompt is { } saved)
        {
            // A saved prompt on an Ask slice's arc starts right away; the files it is about come first, as typed ones do.
            host.CloseMenu(restoreFocus: false);
            if (_prompts.Read(saved) is not { } text)
            {
                Log.Warn($"Saved prompt '{saved}' in '{_prompts.Directory}' is missing, empty or too large");
                host.ShowWarning(Loc.Format("Prompts.Unusable", saved, SavedPrompts.MaxBytes / 1024));
                return;
            }
            Start(item.Agent, action, directory, WithMentions(item.Agent, action.MentionFile ? model.Context.Files : [], text));
            return;
        }

        if (action.Ephemeral && !settings().Quick.PromptCard)
        {
            // No card: the agent opens right away, and the first message is typed in its terminal.
            host.CloseMenu(restoreFocus: false);
            Start(item.Agent, new ActionDefinition { Id = action.Id, Capability = AgentCapability.Open, ExtraArgs = action.ExtraArgs, Ephemeral = true },
                directory, prompt: null);
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
            IReadOnlyList<string> files = action.MentionFile ? model.Context.Files : [];
            host.StartInput(new InputSpec(
                item.Label,
                item.Glyph,
                directory,
                item.Agent.Name,
                AskPrompt: true,
                ValidateName: _ => null,
                Submit: (_, prompt) =>
                {
                    string? first = WithMentions(item.Agent, files, prompt);
                    if (action.Ephemeral && Answer(item.Agent, action, prompt ?? "", first ?? "") is { } answer)
                    {
                        host.ShowAnswer(answer);
                        return null;
                    }
                    host.CloseMenu(restoreFocus: false);
                    Start(item.Agent, action, directory, first);
                    return null;
                })
            {
                AskName = false,
                Placeholder = action.Ephemeral ? Loc.Get("Quick.Placeholder") : null,
                Location = action.Ephemeral ? Loc.Get("Quick.Location") : files.Count switch
                {
                    0 => directory,
                    1 => files[0],
                    _ => $"{directory} · {string.Join(", ", files.Select(Path.GetFileName))}",
                },
                SavedPrompts = settings().Prompts.Enabled ? _prompts : null,
                PromptNames = settings().Prompts.Enabled ? _prompts.List() : [],
            });
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

    /// <summary>
    /// The prompt, after a mention of each of <paramref name="files"/> in the agent's syntax
    /// ("@Program.cs @README.md explain these").
    /// </summary>
    static string? WithMentions(AgentProfile agent, IReadOnlyList<string> files, string? prompt) =>
        files.Count == 0 || string.IsNullOrEmpty(agent.FileMention)
            ? prompt
            : $"{string.Join(" ", files.Select(file => FileMention.Format(agent.FileMention, Path.GetFileName(file))))} {prompt}".TrimEnd();

    /// <summary>
    /// A Quick Conversation's first question, answered on its card without a terminal (<see cref="AgentProfile.AnswerArgs"/>,
    /// with the model from Settings). Its session's files stay while the card is open; closed, they go with the sweep, and
    /// "Continue in terminal" resumes the session there as a Quick Conversation.
    /// </summary>
    /// <param name="question">What the user typed, shown on the card.</param>
    /// <param name="prompt">What the agent gets: the question after its file mentions.</param>
    /// <returns>The answer, or null when the agent cannot answer that way: start it in a terminal then.</returns>
    QuickAnswer? Answer(AgentProfile agent, ActionDefinition action, string question, string prompt)
    {
        if (answers is null || LaunchPlanner.Answer(agent, QuickDirectory, settings().Quick.Model) is not { } plan)
            return null;
        var answer = new QuickAnswer(question, agent.Supports(AgentCapability.ResumeById),
            onContinue: a => ContinueInTerminal(agent, action, a),
            onTerminal: a =>
            {
                Release(a);
                host.CloseMenu(restoreFocus: false);
                Start(agent, action, QuickDirectory, prompt);
            },
            onClosed: a =>
            {
                Release(a);
                if (a.SessionId is { } id)
                    QuickSessionEnded?.Invoke(id);
            });
        lock (_quickGate)
            _openAnswers.Add(answer);
        // Sweeps what earlier ones left; while this answer is open, the folder's files (a screenshot) stay.
        QuickConversationStarting?.Invoke();
        IAnswerProcess? process = null;
        try
        {
            process = answers.Start(plan);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Starting {agent.Name} for an answer on the card failed; it starts in a terminal", ex);
        }
        if (process is null)
        {
            Release(answer);
            return null;
        }
        string model = settings().Quick.Model;
        Log.Info($"Answering a Quick Conversation on its card with {agent.Name}" + (model.Length > 0 ? $" ({model})" : ""));
        answer.Run(process, prompt);
        return answer;
    }

    void Release(QuickAnswer answer)
    {
        lock (_quickGate)
            _openAnswers.Remove(answer);
    }

    /// <summary>"Continue in terminal": the answer's session, resumed in a terminal as a Quick Conversation.</summary>
    void ContinueInTerminal(AgentProfile agent, ActionDefinition action, QuickAnswer answer)
    {
        string id = answer.SessionId!;
        lock (_quickGate)
        {
            _openAnswers.Remove(answer);
            _continued[id] = _time.GetUtcNow(); // kept until that session ends (ForgetQuickSession), or for ContinuedKept
        }
        host.CloseMenu(restoreFocus: false);
        Log.Info($"Continuing Quick Conversation {id} in a terminal");
        var resume = new ActionDefinition
        {
            Id = action.Id,
            Icon = action.Icon,
            Capability = AgentCapability.ResumeById,
            Agent = agent.Id,
            Ephemeral = true,
            ExtraArgs = LaunchPlanner.ModelArguments(agent, settings().Quick.Model),
        };
        Start(agent, resume, QuickDirectory, prompt: null, id);
    }

    /// <summary>The Quick Conversation folder, created when missing; null (with a warning) when it cannot be.</summary>
    string? QuickFolder()
    {
        try
        {
            Directory.CreateDirectory(QuickDirectory);
            return QuickDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Creating the Quick Conversation folder '{QuickDirectory}' failed", ex);
            host.ShowWarning(Loc.Format("Input.CreateFailed", ex.Message));
            return null;
        }
    }

    void Start(AgentProfile agent, ActionDefinition action, string directory, string? prompt, string? sessionId = null,
        bool planMode = false, bool initGit = false)
    {
        if (LaunchPlanner.Create(agent, action, directory, prompt, sessionId, planMode) is not { } plan)
        {
            Log.Warn($"Agent '{agent.Id}' does not support {action.Capability}");
            return;
        }
        var current = settings();
        ActiveAgent? followed = null;
        if (activity?.HooksFile is { } hooks && current.ActiveAgents.Follow && agent.ActivityArgs is { Count: > 0 })
        {
            followed = activity.Start(directory, agent.Name, working: !string.IsNullOrEmpty(prompt));
            plan = LaunchPlanner.Follow(plan, agent, hooks, followed.Id)!;
        }
        if (action.Ephemeral)
        {
            // Nothing of it is saved: the agent's own setting for that, and a window title that says so. The sweep comes
            // once this one is followed, so what was written for it (a screenshot) stays.
            plan = plan with { Environment = agent.EphemeralEnvironment ?? [], Name = Loc.Get("Quick.Window") };
            QuickConversationStarting?.Invoke();
        }
        try
        {
            launcher.Launch(plan with { InitGit = initGit }, current);
        }
        catch (Exception ex) when (ex is LaunchException or Win32Exception or InvalidOperationException or IOException)
        {
            if (followed is not null)
                activity!.Forget(followed.Id);
            Log.Error($"Launching {agent.Name} in '{directory}' failed", ex);
            host.ShowWarning(Loc.Format("Launch.Failed", agent.Name, ex.Message));
        }
    }
}
