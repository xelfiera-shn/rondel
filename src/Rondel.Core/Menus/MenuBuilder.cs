using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;

namespace Rondel.Core.Menus;

/// <param name="Agent">The agent this action starts.</param>
/// <param name="Hint">Small secondary line, e.g. "Last: 2 h ago".</param>
/// <param name="Dimmed">Drawn de-emphasized (still clickable).</param>
public sealed record MenuItemModel(ActionDefinition Action, AgentProfile Agent, string Label, char Glyph, string? Hint, bool Dimmed)
{
    /// <summary>Session to resume, for the items a Resume slice expands into.</summary>
    public string? SessionId { get; init; }
}

public sealed record MenuModel(TriggerContext Context, string Title, string? Subtitle, IReadOnlyList<MenuItemModel> Items);

public static class MenuBuilder
{
    public const int MaxItems = 8;

    /// <summary>How many recent sessions a Resume slice expands into.</summary>
    public const int MaxSessionItems = 5;

    /// <summary>
    /// A Resume slice expands into the folder's recent sessions when its agent can resume a session
    /// by id and has a session store to list them from.
    /// </summary>
    public static bool CanExpand(MenuItemModel item) =>
        item.Action.Capability == AgentCapability.Resume
        && item.Agent.Supports(AgentCapability.ResumeById)
        && SessionStores.Get(item.Agent.Sessions) is not null;

    /// <summary>The items an expandable slice opens into: one per session, newest first.</summary>
    public static IReadOnlyList<MenuItemModel> SessionItems(MenuItemModel parent, IReadOnlyList<SessionEntry> sessions) =>
        sessions.Select(session => new MenuItemModel(
            new ActionDefinition
            {
                Id = $"{parent.Action.Id}:{session.Id}",
                Label = session.Title ?? Loc.Get("Session.Untitled"),
                Icon = parent.Action.Icon,
                Capability = AgentCapability.ResumeById,
                Agent = parent.Agent.Id,
                ExtraArgs = parent.Action.ExtraArgs,
            },
            parent.Agent,
            session.Title ?? Loc.Get("Session.Untitled"),
            parent.Glyph,
            RelativeTime.Describe(session.LastActivityUtc),
            false)
        { SessionId = session.Id })
        .ToList();

    /// <summary>The menu for a context: configured actions, minus those whose agent cannot do them.</summary>
    public static MenuModel Build(TriggerContext context, AppSettings settings)
    {
        var items = new List<MenuItemModel>();
        if (settings.Menus.TryGetValue(context.Kind.ToString(), out var ids))
        {
            foreach (string id in ids)
            {
                if (items.Count == MaxItems)
                    break;
                if (settings.FindAction(id) is not { } action || settings.AgentFor(action) is not { } agent)
                    continue;
                if (agent.Supports(action.Capability))
                    items.Add(new MenuItemModel(action, agent, Loc.Resolve(action.Label), action.Glyph, null, false));
            }
        }

        string title = context.DisplayName
            ?? (context.TargetDir is { } dir ? Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)) : null)
            ?? "";
        if (title.Length == 0 && context.TargetDir is { } root)
            title = root; // drive roots such as C:\

        string? subtitle = context.Kind switch
        {
            ContextKind.DesktopEmpty => Loc.Get("Context.Desktop"),
            ContextKind.ExplorerEmpty => Loc.Get("Context.Explorer"),
            // For a folder item, show where it lives.
            ContextKind.FolderItem => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(context.TargetDir ?? "")) is { Length: > 0 } parent
                ? Path.GetFileName(parent) is { Length: > 0 } parentName ? parentName : parent
                : null,
            _ => null,
        };
        return new MenuModel(context, title, subtitle, items);
    }
}
