using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Settings;

namespace Rondel.Core.Menus;

/// <param name="Agent">The agent this action starts.</param>
/// <param name="Hint">Small secondary line, e.g. "Last: 2 h ago".</param>
/// <param name="Dimmed">Drawn de-emphasized (still clickable).</param>
public sealed record MenuItemModel(ActionDefinition Action, AgentProfile Agent, string Label, char Glyph, string? Hint, bool Dimmed);

public sealed record MenuModel(TriggerContext Context, string Title, string? Subtitle, IReadOnlyList<MenuItemModel> Items);

public static class MenuBuilder
{
    public const int MaxItems = 8;

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
