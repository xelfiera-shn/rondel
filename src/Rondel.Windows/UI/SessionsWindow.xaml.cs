using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Sessions;
using Rondel.Windows.Interop;

namespace Rondel.Windows.UI;

/// <summary>
/// Claude Code's saved sessions, by project, with what each takes on disk: pick the ones not needed any more and delete
/// them, into the Recycle Bin unless the question's "Delete permanently" is ticked. Sessions in use cannot be picked,
/// and a project's memory goes only when its own box is ticked.
/// </summary>
public partial class SessionsWindow : Window
{
    /// <summary>A session as "project key|session id", which is how the selection keeps it.</summary>
    static string Key(StoredProject project, StoredSession session) => project.Key + "|" + session.Id;

    readonly Func<IReadOnlyList<StoredProject>> _list;
    readonly Func<IReadOnlyList<CleanupRequest>, Action<IReadOnlyList<string>>?, CleanupResult> _delete;
    readonly Action<string, string>? _resume;
    readonly HashSet<string> _selected = [];
    readonly HashSet<string> _memory = [];
    IReadOnlyList<StoredProject> _projects = [];
    bool _building;

    /// <param name="list">Reads the sessions (blocking file I/O: called off the UI thread).</param>
    /// <param name="delete">Deletes what was asked for; into the Recycle Bin through the action, for good without it.</param>
    /// <param name="resume">Resumes a session in its folder (folder, id).</param>
    /// <param name="cleanupDays">After how many days Claude Code deletes sessions by itself.</param>
    /// <param name="mode">Light or dark; the system's by default (developer aids fix it).</param>
    internal SessionsWindow(Func<IReadOnlyList<StoredProject>> list, Func<IReadOnlyList<CleanupRequest>, Action<IReadOnlyList<string>>?, CleanupResult> delete,
        Action<string, string>? resume, int cleanupDays, ThemeColors theme, ThemeMode? mode = null)
    {
        _list = list;
        _delete = delete;
        _resume = resume;
        ThemeMode = mode ?? ThemeMode.System; // Fluent styles, like Settings
        InitializeComponent();
        Icon = AppIconRenderer.Render(64);
        HeaderIcon.Source = AppIconRenderer.Render(72);
        ApplyBrand(theme);
        CleanupText.Text = Loc.Format("Sessions.Cleanup", cleanupDays);
        Loaded += (_, _) => Refresh();
    }

    internal void ApplyBrand(ThemeColors theme)
    {
        FluentBrand.Apply(this, theme);
        BrandLine.Background = FluentBrand.Line(theme);
    }

    /// <summary>Reads the sessions again, off the UI thread, keeping what is still there selected.</summary>
    internal void Refresh()
    {
        SummaryText.Text = Loc.Get("Sessions.Loading");
        RefreshButton.IsEnabled = DeleteButton.IsEnabled = false;
        Task.Run(_list).ContinueWith(read => Dispatcher.BeginInvoke(() =>
        {
            RefreshButton.IsEnabled = true;
            Show(read.IsCompletedSuccessfully ? read.Result : []);
        }));
    }

    /// <summary>Shows <paramref name="projects"/> (also for developer aids, which make them up).</summary>
    internal void Show(IReadOnlyList<StoredProject> projects)
    {
        _projects = projects;
        var sessions = new HashSet<string>(projects.SelectMany(p => p.Sessions.Where(s => !s.Running).Select(s => Key(p, s))));
        _selected.IntersectWith(sessions);
        _memory.IntersectWith(projects.Where(p => p.MemoryBytes > 0).Select(p => p.Key));
        SummaryText.Text = Loc.Format("Sessions.Summary", Count("Sessions.Count", projects.Sum(p => p.Sessions.Count)),
            Count("Sessions.Projects", projects.Count(p => p.Sessions.Count > 0)), Size(projects.Sum(p => p.Bytes)));
        Build();
    }

    // ---- the list ----

    void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded)
            Build();
    }

    static string Choice(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>The projects and sessions the search and the filter let through, in the chosen order.</summary>
    List<(StoredProject Project, List<StoredSession> Sessions)> Visible()
    {
        string search = SearchBox.Text.Trim();
        string show = Choice(ShowBox);
        var now = DateTime.UtcNow;
        bool Matches(string? text) => search.Length == 0 || (text?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);
        var visible = new List<(StoredProject, List<StoredSession>)>();
        foreach (var project in _projects)
        {
            if (show == "Gone" && project.DirectoryExists)
                continue;
            bool projectMatches = Matches(ProjectName(project)) || Matches(project.Directory);
            var sessions = project.Sessions
                .Where(s => show switch
                {
                    "Day" => now - s.LastActivityUtc > TimeSpan.FromDays(1),
                    "Week" => now - s.LastActivityUtc > TimeSpan.FromDays(7),
                    _ => true,
                })
                .Where(s => projectMatches || Matches(s.Title))
                .ToList();
            // A project with only its memory left shows while nothing narrows the list to sessions.
            bool memoryOnly = project.Sessions.Count == 0 && show is "All" or "Gone" && projectMatches;
            if (sessions.Count > 0 || memoryOnly)
                visible.Add((project, sessions));
        }
        return Choice(SortBox) switch
        {
            "Newest" => visible.Select(v => (v.Item1, v.Item2.OrderByDescending(s => s.LastActivityUtc).ToList()))
                .OrderByDescending(v => v.Item2.Count == 0 ? DateTime.MinValue : v.Item2[0].LastActivityUtc).ToList(),
            "Oldest" => visible.Select(v => (v.Item1, v.Item2.OrderBy(s => s.LastActivityUtc).ToList()))
                .OrderBy(v => v.Item2.Count == 0 ? DateTime.MaxValue : v.Item2[0].LastActivityUtc).ToList(),
            _ => visible.Select(v => (v.Item1, v.Item2.OrderByDescending(s => s.Bytes).ToList()))
                .OrderByDescending(v => v.Item2.Sum(s => s.Bytes) + v.Item1.MemoryBytes).ToList(),
        };
    }

    void Build()
    {
        _building = true;
        try
        {
            ProjectList.Children.Clear();
            var visible = Visible();
            foreach (var (project, sessions) in visible)
                ProjectList.Children.Add(ProjectCard(project, sessions));
            EmptyText.Text = Loc.Get(_projects.Count == 0 ? "Sessions.Empty" : "Sessions.NoMatch");
            EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _building = false;
        }
        UpdateSelection();
    }

    FrameworkElement ProjectCard(StoredProject project, List<StoredSession> sessions)
    {
        var card = new StackPanel();
        var pickable = sessions.Where(s => !s.Running).ToList();

        // The project: a box for all its sessions shown here, its name and folder, and what they take.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var all = new CheckBox
        {
            IsThreeState = false,
            IsEnabled = pickable.Count > 0,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
            Margin = new Thickness(0, 0, 8, 0),
            IsChecked = pickable.Count == 0 ? false
                : pickable.All(s => _selected.Contains(Key(project, s))) ? true
                : pickable.Any(s => _selected.Contains(Key(project, s))) ? null
                : false,
        };
        all.Click += (_, _) =>
        {
            bool select = !pickable.All(s => _selected.Contains(Key(project, s)));
            foreach (var session in pickable)
            {
                if (select)
                    _selected.Add(Key(project, session));
                else
                    _selected.Remove(Key(project, session));
            }
            Build();
        };
        header.Children.Add(all);

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = ProjectName(project), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var where = new TextBlock { Text = project.Directory ?? project.Key, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = project.Directory ?? project.Key };
        where.SetResourceReference(StyleProperty, "Description");
        name.Children.Add(where);
        if (project.Directory is not null && !project.DirectoryExists)
        {
            var gone = new TextBlock { Text = "⚠ " + Loc.Get("Sessions.FolderGone"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
            gone.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            name.Children.Add(gone);
        }
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var size = new TextBlock { Text = Size(sessions.Sum(s => s.Bytes)), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(size, 2);
        header.Children.Add(size);
        card.Children.Add(header);

        if (project.Sessions.Count == 0)
        {
            var only = new TextBlock { Text = Loc.Get("Sessions.MemoryOnly"), Margin = new Thickness(30, 8, 0, 0) };
            only.SetResourceReference(StyleProperty, "Description");
            card.Children.Add(only);
        }
        if (project.MemoryBytes > 0)
        {
            var memory = new CheckBox
            {
                Content = Loc.Format("Sessions.Memory", Size(project.MemoryBytes)),
                IsChecked = _memory.Contains(project.Key),
                ToolTip = Loc.Get("Sessions.Memory.Tip"),
                FontSize = 12,
                Margin = new Thickness(30, 8, 0, 0),
                MinWidth = 0,
            };
            memory.Click += (_, _) =>
            {
                if (memory.IsChecked == true)
                    _memory.Add(project.Key);
                else
                    _memory.Remove(project.Key);
                UpdateSelection();
            };
            card.Children.Add(memory);
        }

        foreach (var session in sessions)
            card.Children.Add(SessionRow(project, session));
        return new Border { Style = (Style)FindResource("Card"), Child = card };
    }

    FrameworkElement SessionRow(StoredProject project, StoredSession session)
    {
        var row = new Grid { Margin = new Thickness(30, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new CheckBox
        {
            IsChecked = _selected.Contains(Key(project, session)),
            IsEnabled = !session.Running,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
            Margin = new Thickness(0, 0, 8, 0),
        };
        box.Click += (_, _) =>
        {
            if (box.IsChecked == true)
                _selected.Add(Key(project, session));
            else
                _selected.Remove(Key(project, session));
            Build();
        };
        row.Children.Add(box);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string title = session.Title ?? Loc.Get("Session.Untitled");
        text.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = title });
        var when = new TextBlock { Text = RelativeTime.Describe(session.LastActivityUtc) + (session.Running ? " · " + Loc.Get("Sessions.InUse") : "") };
        when.SetResourceReference(StyleProperty, "Description");
        text.Children.Add(when);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var size = new TextBlock { Text = Size(session.Bytes), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(size, 2);
        row.Children.Add(size);

        if (_resume is not null && project.DirectoryExists && project.Directory is { } directory && !session.Running)
        {
            var resume = new Button
            {
                Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
                ToolTip = Loc.Get("Sessions.Resume"),
                Width = 32,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(resume, Loc.Get("Sessions.Resume") + ": " + title);
            resume.Click += (_, _) => _resume(directory, session.Id);
            Grid.SetColumn(resume, 3);
            row.Children.Add(resume);
        }
        else
        {
            // Keeps the sizes in one column whether a row can be resumed or not.
            var room = new Border { Width = 42 };
            Grid.SetColumn(room, 3);
            row.Children.Add(room);
        }
        return row;
    }

    (int Sessions, int Memories, long Bytes) Selection()
    {
        var sessions = _projects.SelectMany(p => p.Sessions.Where(s => _selected.Contains(Key(p, s)))).ToList();
        var memories = _projects.Where(p => _memory.Contains(p.Key)).ToList();
        return (sessions.Count, memories.Count, sessions.Sum(s => s.Bytes) + memories.Sum(p => p.MemoryBytes));
    }

    void UpdateSelection()
    {
        if (_building)
            return;
        var (sessions, memories, bytes) = Selection();
        SelectedText.Text = sessions + memories == 0 ? Loc.Get("Sessions.NoneSelected") : Loc.Format("Sessions.Selected", sessions, Size(bytes));
        DeleteButton.IsEnabled = sessions + memories > 0;
    }

    // ---- deleting ----

    void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    void OnDelete(object sender, RoutedEventArgs e) => ShowConfirm();

    /// <summary>The question before deleting: how many, how much, and where they go.</summary>
    internal void ShowConfirm(bool forGood = false)
    {
        var (sessions, memories, _) = Selection();
        if (sessions + memories == 0)
            return;
        ConfirmTitle.Text = sessions == 0 ? Count("Sessions.Confirm.TitleMemory", memories)
            : sessions == 1 ? Loc.Get("Sessions.Confirm.TitleOne")
            : Loc.Format("Sessions.Confirm.Title", sessions);
        ConfirmMemory.Text = Count("Sessions.Confirm.WithMemory", memories);
        ConfirmMemory.Visibility = sessions > 0 && memories > 0 ? Visibility.Visible : Visibility.Collapsed;
        ForGoodBox.IsChecked = forGood;
        UpdateConfirmText();
        ConfirmLayer.Visibility = Visibility.Visible;
        ConfirmButton.Focus();
    }

    void UpdateConfirmText()
    {
        var (_, _, bytes) = Selection();
        ConfirmText.Text = Loc.Format(ForGoodBox.IsChecked == true ? "Sessions.Confirm.Permanent" : "Sessions.Confirm.Recycle", Size(bytes));
    }

    void OnForGoodChanged(object sender, RoutedEventArgs e) => UpdateConfirmText();

    void OnCancelDelete(object sender, RoutedEventArgs e) => ConfirmLayer.Visibility = Visibility.Collapsed;

    void OnConfirmDelete(object sender, RoutedEventArgs e)
    {
        ConfirmLayer.Visibility = Visibility.Collapsed;
        bool forGood = ForGoodBox.IsChecked == true;
        var requests = _projects
            .Select(p => new CleanupRequest(p.Key, [.. p.Sessions.Where(s => _selected.Contains(Key(p, s))).Select(s => s.Id)], _memory.Contains(p.Key)))
            .Where(r => r.SessionIds.Count > 0 || r.Memory)
            .ToList();
        nint owner = new WindowInteropHelper(this).Handle;
        RefreshButton.IsEnabled = DeleteButton.IsEnabled = false;
        // The Recycle Bin is the shell's: it wants a thread of its own kind for its question about files too large for it.
        var worker = new Thread(() =>
        {
            CleanupResult? result = null;
            string? error = null;
            try
            {
                result = _delete(requests, forGood ? null : paths => RecycleBin.Move(paths, owner));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Deleting sessions failed", ex);
                error = ex.Message;
            }
            Dispatcher.BeginInvoke(() => Done(result, error, forGood));
        })
        { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    void Done(CleanupResult? result, string? error, bool forGood)
    {
        if (result is null)
        {
            ShowStatus(Loc.Format("Sessions.Failed", error), isError: true);
        }
        else
        {
            string text = Count(forGood ? "Sessions.Done.Permanent" : "Sessions.Done.Recycle", result.Sessions, Size(result.Bytes));
            if (result.Memories > 0)
                text += Count("Sessions.Done.Memory", result.Memories);
            if (result.Skipped.Count > 0)
                text += Count("Sessions.Done.Skipped", result.Skipped.Count);
            ShowStatus(text, isError: false);
            _selected.Clear();
            _memory.Clear();
        }
        Refresh();
    }

    void ShowStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        StatusText.Visibility = Visibility.Visible;
    }

    // ---- developer aids ----

    /// <summary>Selects sessions as a click would, by project index and session index (--render-sessions).</summary>
    internal void SelectForPreview(IEnumerable<(int Project, int Session)> picks, IEnumerable<int> memories)
    {
        foreach (var (p, s) in picks)
        {
            var project = _projects[p];
            if (!project.Sessions[s].Running)
                _selected.Add(Key(project, project.Sessions[s]));
        }
        foreach (int p in memories)
            _memory.Add(_projects[p].Key);
        Build();
    }

    // ---- text ----

    /// <summary>The project's folder name, or its key when no session recorded a folder.</summary>
    static string ProjectName(StoredProject project) =>
        project.Directory is { } directory && Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) is { Length: > 0 } leaf ? leaf : project.Key;

    /// <summary>The text for <paramref name="count"/> things: the key's ".One" form for one, where English needs it.</summary>
    static string Count(string key, int count, params object?[] more) =>
        Loc.Format(count == 1 ? key + ".One" : key, [count, .. more]);

    /// <summary>A size as people read it: 14 KB, 8.5 MB, 760 MB, 1.4 GB.</summary>
    internal static string Size(long bytes)
    {
        var culture = Loc.Culture;
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return bytes switch
        {
            < 1024 => string.Format(culture, "{0} B", bytes),
            < (long)Mb => string.Format(culture, "{0:0} KB", bytes / Kb),
            < (long)(10 * Mb) => string.Format(culture, "{0:0.0} MB", bytes / Mb),
            < (long)(1000 * Mb) => string.Format(culture, "{0:0} MB", bytes / Mb),
            _ => string.Format(culture, "{0:0.0} GB", bytes / Gb),
        };
    }
}
