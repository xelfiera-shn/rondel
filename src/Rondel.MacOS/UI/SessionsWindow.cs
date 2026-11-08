using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Sessions;
using Path = System.IO.Path;

namespace Rondel.MacOS.UI;

/// <summary>
/// Claude Code's saved sessions, by project, with what each takes on disk, as on Windows: pick the ones not needed any
/// more and delete them, to the Trash unless the question's "Delete permanently" is ticked. Sessions in use cannot be
/// picked, and a project's memory goes only when its own box is ticked.
/// </summary>
internal sealed class SessionsWindow : Window
{
    /// <summary>A session as "project key|session id", which is how the selection keeps it.</summary>
    static string Key(StoredProject project, StoredSession session) => project.Key + "|" + session.Id;

    readonly Func<IReadOnlyList<StoredProject>> _list;
    readonly Func<IReadOnlyList<CleanupRequest>, Action<IReadOnlyList<string>>?, CleanupResult> _delete;
    readonly Action<string, string>? _resume;
    readonly int _cleanupDays;
    readonly HashSet<string> _selected = [];
    readonly HashSet<string> _memory = [];
    IReadOnlyList<StoredProject> _projects = [];
    PageLook _look = PageLook.Dark;
    bool _building, _loaded;

    TextBlock _summary = null!, _status = null!, _empty = null!, _selectedText = null!, _confirmTitle = null!, _confirmText = null!, _confirmMemory = null!;
    TextBox _search = null!;
    ComboBox _show = null!, _sort = null!;
    StackPanel _projectList = null!;
    Button _refresh = null!, _deleteButton = null!, _confirmButton = null!;
    CheckBox _forGood = null!;
    Panel _confirmLayer = null!;

    /// <param name="list">Reads the sessions (blocking file I/O: called off the main thread).</param>
    /// <param name="delete">Deletes what was asked for; to the Trash through the action, for good without it.</param>
    /// <param name="resume">Resumes a session in its folder (folder, id).</param>
    /// <param name="cleanupDays">After how many days Claude Code deletes sessions by itself.</param>
    public SessionsWindow(Func<IReadOnlyList<StoredProject>> list, Func<IReadOnlyList<CleanupRequest>, Action<IReadOnlyList<string>>?, CleanupResult> delete,
        Action<string, string>? resume, int cleanupDays)
    {
        _list = list;
        _delete = delete;
        _resume = resume;
        _cleanupDays = cleanupDays;
        Width = 780;
        Height = 720;
        MinWidth = 640;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily(".AppleSystemUIFont");
        Build();
        Loc.Changed += Rebuild;
        ActualThemeVariantChanged += (_, _) => Rebuild();
        Closed += (_, _) => Loc.Changed -= Rebuild;
        Opened += (_, _) =>
        {
            _loaded = true;
            Refresh();
        };
    }

    void Rebuild()
    {
        Build();
        Show(_projects);
    }

    void Build()
    {
        _look = ActualThemeVariant == ThemeVariant.Light ? PageLook.Light : PageLook.Dark;
        Title = Loc.Get("Sessions.Title");
        Background = _look.Background;

        _summary = Description("", new Thickness(0));
        var header = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 0),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        PageLook.Logo(),
                        new StackPanel
                        {
                            Margin = new Thickness(12, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Children = { new TextBlock { Text = Loc.Get("Sessions.Title"), FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = _look.Text }, _summary },
                        },
                    },
                },
                new Border { Height = 2, Margin = new Thickness(0, 14, 0, 0), CornerRadius = new CornerRadius(1), Background = _look.Brand },
            },
        };

        // Search, what to show and in which order.
        _search = new TextBox { PlaceholderText = Loc.Get("Sessions.Search"), VerticalContentAlignment = VerticalAlignment.Center };
        _search.TextChanged += (_, _) => OnFilterChanged();
        _show = Combo(230, ("All", "Sessions.Show.All"), ("Day", "Sessions.Show.Day"), ("Week", "Sessions.Show.Week"), ("Gone", "Sessions.Show.Gone"));
        _sort = Combo(170, ("Size", "Sessions.Sort.Size"), ("Newest", "Sessions.Sort.Newest"), ("Oldest", "Sessions.Sort.Oldest"));
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Children = { _search, _show, _sort } };
        Grid.SetColumn(_show, 1);
        Grid.SetColumn(_sort, 2);
        _status = Description("", new Thickness(2, 10, 0, 0));
        _status.IsVisible = false;
        var tools = new StackPanel { Margin = new Thickness(24, 14, 24, 10), Children = { filters, _status } };

        // One card per project: its sessions, and its memory when it has one.
        _projectList = new StackPanel();
        _empty = Description("", new Thickness(0, 40, 0, 0));
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.IsVisible = false;
        var scroll = new ScrollViewer
        {
            Padding = new Thickness(24, 0, 24, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Children = { _projectList, _empty } },
        };

        _selectedText = new TextBlock { FontWeight = FontWeight.SemiBold, Foreground = _look.Text };
        _refresh = Button(Loc.Get("Sessions.Refresh"), Refresh);
        _deleteButton = Button(Loc.Get("Sessions.Delete"), () => ShowConfirm());
        _deleteButton.Classes.Add("accent");
        _deleteButton.MinWidth = 130;
        _deleteButton.Margin = new Thickness(8, 0, 0, 0);
        var footer = new Border
        {
            Padding = new Thickness(24, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = _look.CardStroke,
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 16, 0),
                        Children = { _selectedText, Description(Loc.Format("Sessions.Cleanup", _cleanupDays)) },
                    },
                    new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _refresh, _deleteButton } },
                },
            },
        };
        Grid.SetColumn(((Grid)footer.Child!).Children[1], 1);

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Children = { header, tools, scroll, footer } };
        Grid.SetRow(tools, 1);
        Grid.SetRow(scroll, 2);
        Grid.SetRow(footer, 3);
        _confirmLayer = Confirm();
        Grid.SetRowSpan(_confirmLayer, 4);
        layout.Children.Add(_confirmLayer);
        Content = layout;
    }

    /// <summary>The question before deleting, over the window.</summary>
    Panel Confirm()
    {
        _confirmTitle = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = _look.Text };
        _confirmText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = _look.Text };
        _confirmMemory = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = _look.Text, IsVisible = false };
        _forGood = new CheckBox { Content = Loc.Get("Sessions.Confirm.DeleteForGood"), Margin = new Thickness(0, 16, 0, 0), Foreground = _look.Text };
        _forGood.Click += (_, _) => UpdateConfirmText();
        var cancel = Button(Loc.Get("Sessions.Confirm.Cancel"), () => _confirmLayer.IsVisible = false);
        _confirmButton = Button(Loc.Get("Sessions.Confirm.Delete"), Delete);
        _confirmButton.Classes.Add("accent");
        _confirmButton.Margin = new Thickness(8, 0, 0, 0);
        _confirmButton.IsDefault = true;
        cancel.IsCancel = true;
        return new Panel
        {
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
            IsVisible = false,
            Children =
            {
                new Border
                {
                    Width = 440,
                    Padding = new Thickness(24, 20),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    Background = _look.Card,
                    BorderBrush = _look.CardStroke,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new StackPanel
                    {
                        Children =
                        {
                            _confirmTitle, _confirmText, _confirmMemory, _forGood,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                Margin = new Thickness(0, 20, 0, 0),
                                Children = { cancel, _confirmButton },
                            },
                        },
                    },
                },
            },
        };
    }

    /// <summary>Reads the sessions again, off the main thread, keeping what is still there selected.</summary>
    public void Refresh()
    {
        _summary.Text = Loc.Get("Sessions.Loading");
        _refresh.IsEnabled = _deleteButton.IsEnabled = false;
        Task.Run(_list).ContinueWith(read => Dispatcher.UIThread.Post(() =>
        {
            _refresh.IsEnabled = true;
            Show(read.IsCompletedSuccessfully ? read.Result : []);
        }));
    }

    /// <summary>Shows <paramref name="projects"/> (also for developer aids, which make them up).</summary>
    public void Show(IReadOnlyList<StoredProject> projects)
    {
        _projects = projects;
        var sessions = new HashSet<string>(projects.SelectMany(p => p.Sessions.Where(s => !s.Running).Select(s => Key(p, s))));
        _selected.IntersectWith(sessions);
        _memory.IntersectWith(projects.Where(p => p.MemoryBytes > 0).Select(p => p.Key));
        _summary.Text = Loc.Format("Sessions.Summary", Count("Sessions.Count", projects.Sum(p => p.Sessions.Count)),
            Count("Sessions.Projects", projects.Count(p => p.Sessions.Count > 0)), Size(projects.Sum(p => p.Bytes)));
        BuildList();
    }

    // ---- the list ----

    void OnFilterChanged()
    {
        if (_loaded)
            BuildList();
    }

    static string Choice(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>The projects and sessions the search and the filter let through, in the chosen order.</summary>
    List<(StoredProject Project, List<StoredSession> Sessions)> Visible()
    {
        string search = (_search.Text ?? "").Trim();
        string show = Choice(_show);
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
        return Choice(_sort) switch
        {
            "Newest" => visible.Select(v => (v.Item1, v.Item2.OrderByDescending(s => s.LastActivityUtc).ToList()))
                .OrderByDescending(v => v.Item2.Count == 0 ? DateTime.MinValue : v.Item2[0].LastActivityUtc).ToList(),
            "Oldest" => visible.Select(v => (v.Item1, v.Item2.OrderBy(s => s.LastActivityUtc).ToList()))
                .OrderBy(v => v.Item2.Count == 0 ? DateTime.MaxValue : v.Item2[0].LastActivityUtc).ToList(),
            _ => visible.Select(v => (v.Item1, v.Item2.OrderByDescending(s => s.Bytes).ToList()))
                .OrderByDescending(v => v.Item2.Sum(s => s.Bytes) + v.Item1.MemoryBytes).ToList(),
        };
    }

    void BuildList()
    {
        _building = true;
        try
        {
            _projectList.Children.Clear();
            var visible = Visible();
            foreach (var (project, sessions) in visible)
                _projectList.Children.Add(ProjectCard(project, sessions));
            _empty.Text = Loc.Get(_projects.Count == 0 ? "Sessions.Empty" : "Sessions.NoMatch");
            _empty.IsVisible = visible.Count == 0;
        }
        finally
        {
            _building = false;
        }
        UpdateSelection();
    }

    Border ProjectCard(StoredProject project, List<StoredSession> sessions)
    {
        var card = new StackPanel();
        var pickable = sessions.Where(s => !s.Running).ToList();

        // The project: a box for all its sessions shown here, its name and folder, and what they take.
        var all = new CheckBox
        {
            IsEnabled = pickable.Count > 0,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
            Margin = new Thickness(0, 0, 8, 0),
            IsThreeState = true,
            IsChecked = pickable.Count == 0 ? false
                : pickable.All(s => _selected.Contains(Key(project, s))) ? true
                : pickable.Any(s => _selected.Contains(Key(project, s))) ? null
                : false,
        };
        AutomationProperties.SetName(all, ProjectName(project));
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
            BuildList();
        };
        var name = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = ProjectName(project), FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = _look.Text },
                Trimmed(Description(project.Directory ?? project.Key, new Thickness(0, 1, 0, 0)), project.Directory ?? project.Key),
            },
        };
        if (project.Directory is not null && !project.DirectoryExists)
        {
            name.Children.Add(new TextBlock
            {
                Text = "⚠ " + Loc.Get("Sessions.FolderGone"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24)),
            });
        }
        var size = new TextBlock { Text = Size(sessions.Sum(s => s.Bytes)), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Foreground = _look.Text };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { all, name, size } };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(size, 2);
        card.Children.Add(header);

        if (project.Sessions.Count == 0)
            card.Children.Add(Description(Loc.Get("Sessions.MemoryOnly"), new Thickness(30, 8, 0, 0)));
        if (project.MemoryBytes > 0)
        {
            var memory = new CheckBox
            {
                Content = Loc.Format("Sessions.Memory", Size(project.MemoryBytes)),
                IsChecked = _memory.Contains(project.Key),
                FontSize = 12,
                Margin = new Thickness(30, 8, 0, 0),
                MinWidth = 0,
                Foreground = _look.Text,
            };
            ToolTip.SetTip(memory, Loc.Get("Sessions.Memory.Tip"));
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
        return new Border
        {
            Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = _look.Card,
            BorderBrush = _look.CardStroke,
            Child = card,
        };
    }

    Grid SessionRow(StoredProject project, StoredSession session)
    {
        var box = new CheckBox
        {
            IsChecked = _selected.Contains(Key(project, session)),
            IsEnabled = !session.Running,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
            Margin = new Thickness(0, 0, 8, 0),
        };
        string title = session.Title ?? Loc.Get("Session.Untitled");
        AutomationProperties.SetName(box, title);
        box.Click += (_, _) =>
        {
            if (box.IsChecked == true)
                _selected.Add(Key(project, session));
            else
                _selected.Remove(Key(project, session));
            BuildList();
        };
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Trimmed(new TextBlock { Text = title, Foreground = _look.Text }, title),
                Description(RelativeTime.Describe(session.LastActivityUtc) + (session.Running ? " · " + Loc.Get("Sessions.InUse") : ""), new Thickness(0, 1, 0, 0)),
            },
        };
        var size = new TextBlock { Text = Size(session.Bytes), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Foreground = _look.Text };
        Control action;
        if (_resume is not null && project.DirectoryExists && project.Directory is { } directory && !session.Running)
        {
            var mask = SymbolIcons.Mask("play", 12);
            var resume = new Button
            {
                Width = 32, Height = 28, Padding = new Thickness(0), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Content = new Rectangle
                {
                    Width = 12, Height = 12, Fill = _look.Text,
                    OpacityMask = mask is null ? null : new ImageBrush(mask) { Stretch = Stretch.Uniform },
                },
            };
            ToolTip.SetTip(resume, Loc.Get("Sessions.Resume"));
            AutomationProperties.SetName(resume, Loc.Get("Sessions.Resume") + ": " + title);
            resume.Click += (_, _) => _resume(directory, session.Id);
            action = resume;
        }
        else
        {
            action = new Border { Width = 42 }; // keeps the sizes in one column whether a row can be resumed or not
        }
        var row = new Grid { Margin = new Thickness(30, 10, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Children = { box, text, size, action } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(size, 2);
        Grid.SetColumn(action, 3);
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
        _selectedText.Text = sessions + memories == 0 ? Loc.Get("Sessions.NoneSelected") : Loc.Format("Sessions.Selected", sessions, Size(bytes));
        _deleteButton.IsEnabled = sessions + memories > 0;
    }

    // ---- deleting ----

    /// <summary>The question before deleting: how many, how much, and where they go.</summary>
    public void ShowConfirm(bool forGood = false)
    {
        var (sessions, memories, _) = Selection();
        if (sessions + memories == 0)
            return;
        _confirmTitle.Text = sessions == 0 ? Count("Sessions.Confirm.TitleMemory", memories)
            : sessions == 1 ? Loc.Get("Sessions.Confirm.TitleOne")
            : Loc.Format("Sessions.Confirm.Title", sessions);
        _confirmMemory.Text = Count("Sessions.Confirm.WithMemory", memories);
        _confirmMemory.IsVisible = sessions > 0 && memories > 0;
        _forGood.IsChecked = forGood;
        UpdateConfirmText();
        _confirmLayer.IsVisible = true;
        _confirmButton.Focus();
    }

    void UpdateConfirmText()
    {
        var (_, _, bytes) = Selection();
        _confirmText.Text = Loc.Format(_forGood.IsChecked == true ? "Sessions.Confirm.Permanent" : "Sessions.Confirm.Recycle.Mac", Size(bytes));
    }

    void Delete()
    {
        _confirmLayer.IsVisible = false;
        bool forGood = _forGood.IsChecked == true;
        var requests = _projects
            .Select(p => new CleanupRequest(p.Key, [.. p.Sessions.Where(s => _selected.Contains(Key(p, s))).Select(s => s.Id)], _memory.Contains(p.Key)))
            .Where(r => r.SessionIds.Count > 0 || r.Memory)
            .ToList();
        _refresh.IsEnabled = _deleteButton.IsEnabled = false;
        Task.Run(() =>
        {
            CleanupResult? result = null;
            string? error = null;
            try
            {
                result = _delete(requests, forGood ? null : Interop.Trash.Move);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Deleting sessions failed", ex);
                error = ex.Message;
            }
            Dispatcher.UIThread.Post(() => Done(result, error, forGood));
        });
    }

    void Done(CleanupResult? result, string? error, bool forGood)
    {
        if (result is null)
        {
            ShowStatus(Loc.Format("Sessions.Failed", error), isError: true);
        }
        else
        {
            string text = Count(forGood ? "Sessions.Done.Permanent" : "Sessions.Done.Recycle.Mac", result.Sessions, Size(result.Bytes));
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
        _status.Text = text;
        _status.Foreground = isError ? new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A)) : _look.SubText;
        _status.IsVisible = true;
    }

    // ---- developer aids ----

    /// <summary>Selects sessions as a click would, by project index and session index (--render-sessions).</summary>
    public void SelectForPreview(IEnumerable<(int Project, int Session)> picks, IEnumerable<int> memories)
    {
        foreach (var (p, s) in picks)
        {
            var project = _projects[p];
            if (!project.Sessions[s].Running)
                _selected.Add(Key(project, project.Sessions[s]));
        }
        foreach (int p in memories)
            _memory.Add(_projects[p].Key);
        BuildList();
    }

    // ---- controls and text ----

    ComboBox Combo(double width, params (string Tag, string Key)[] items)
    {
        var box = new ComboBox { Width = width, Margin = new Thickness(10, 0, 0, 0) };
        foreach (var (tag, key) in items)
            box.Items.Add(new ComboBoxItem { Tag = tag, Content = Loc.Get(key) });
        box.SelectedIndex = 0;
        box.SelectionChanged += (_, _) => OnFilterChanged();
        return box;
    }

    static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 5) };
        button.Click += (_, _) => click();
        return button;
    }

    TextBlock Description(string text, Thickness? margin = null) => new()
    {
        Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 2, 0, 0), Foreground = _look.SubText,
    };

    /// <summary>One line, ending in an ellipsis, the whole text on hover.</summary>
    static TextBlock Trimmed(TextBlock block, string tip)
    {
        block.TextWrapping = TextWrapping.NoWrap;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(block, tip);
        return block;
    }

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
