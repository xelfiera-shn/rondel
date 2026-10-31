using Rondel.Core.Menus;
using Rondel.Core.Sessions;

namespace Rondel.Core.Flow;

/// <summary>
/// What the session stores add to a menu: the hint under each item and the items each one opens into
/// (a Resume slice's sessions, Recent Projects' folders and their sessions). Looked up after the menu is
/// shown and filled in when it arrives, so it never delays the menu.
/// </summary>
/// <param name="Hints">One per menu item, in order.</param>
/// <param name="Children">One per menu item, in order; empty for an item that does not open further.</param>
public sealed record MenuDetails(IReadOnlyList<(string? Hint, bool Dimmed)> Hints, IReadOnlyList<IReadOnlyList<MenuItemModel>> Children)
{
    /// <summary>Whether any item of the menu has something to look up; if not, <see cref="Load"/> can be skipped.</summary>
    public static bool Needed(MenuModel model) =>
        model.Items.Any(item => UsesSessions(item) && SessionStores.Get(item.Agent.Sessions) is not null);

    /// <summary>Reads the session stores, one pass per store. Blocking file I/O: call it off the UI thread.</summary>
    public static MenuDetails Load(MenuModel model)
    {
        string? directory = model.Context.TargetDir;
        var byStore = model.Items
            .Where(UsesSessions)
            .GroupBy(item => item.Agent.Sessions ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group => (Id: group.Key, Store: SessionStores.Get(group.Key),
                Expands: group.Any(MenuBuilder.CanExpand), ListsProjects: group.Any(MenuBuilder.ListsRecentProjects)))
            .Where(s => s.Store is not null)
            .ToDictionary(s => s.Id, s => LookUp(s.Store!, directory, s.Expands, s.ListsProjects), StringComparer.OrdinalIgnoreCase);

        StoreDetails? DetailsFor(MenuItemModel item) =>
            byStore.TryGetValue(item.Agent.Sessions ?? "", out var details) ? details : null;

        var hints = model.Items
            .Select(item => SessionHints.Describe(item.Action.Hint, DetailsFor(item)?.Info))
            .ToList();
        var children = model.Items
            .Select(item => DetailsFor(item) is not { } details ? []
                : MenuBuilder.CanExpand(item) ? MenuBuilder.SessionItems(item, details.Recent)
                : MenuBuilder.ListsRecentProjects(item) ? MenuBuilder.ProjectItems(item, details.Folders,
                    folder => details.FolderSessions.TryGetValue(folder, out var sessions) ? sessions : [])
                : [])
            .ToList();
        return new MenuDetails(hints, children);
    }

    static bool UsesSessions(MenuItemModel item) =>
        item.Action.Hint != SessionHint.None || MenuBuilder.CanExpand(item) || MenuBuilder.ListsRecentProjects(item);

    /// <summary>What one session store knows: about the menu's folder, and about the recent projects.</summary>
    sealed record StoreDetails(SessionInfo? Info, IReadOnlyList<SessionEntry> Recent,
        IReadOnlyList<FolderActivity> Folders, IReadOnlyDictionary<string, IReadOnlyList<SessionEntry>> FolderSessions);

    static StoreDetails LookUp(ISessionStore store, string? directory, bool recentSessions, bool recentProjects)
    {
        var folders = recentProjects ? store.RecentFolders(MenuBuilder.MaxRecentProjects) : [];
        // Only projects that will open further need their session list.
        var folderSessions = folders
            .Where(f => f.SessionCount >= MenuBuilder.MinSessionsToExpand)
            .ToDictionary(f => f.Directory, f => store.Recent(f.Directory, MenuBuilder.MaxSessionItems), StringComparer.OrdinalIgnoreCase);
        return new StoreDetails(
            directory is null ? null : store.Lookup(directory),
            directory is not null && recentSessions ? store.Recent(directory, MenuBuilder.MaxSessionItems) : [],
            folders,
            folderSessions);
    }
}
