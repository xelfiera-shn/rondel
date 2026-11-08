using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Rondel.Core;
using Rondel.Core.Agents;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;
using Rondel.Core.Themes;
using Rondel.MacOS.Interop;
using Rondel.MacOS.Launch;
using Path = Avalonia.Controls.Shapes.Path;
using UpdateCheck = Rondel.Core.Updates.UpdateStatus;

namespace Rondel.MacOS.UI;

/// <summary>
/// The settings page, the counterpart of the Windows SettingsWindow with the same sections, minus what macOS does
/// not have yet (the keyboard shortcut, active agents) and the shell (always the login shell), plus the permissions
/// Rondel needs. Every change is saved and applied immediately; the action/menu configuration is edited in
/// settings.json directly. The page is built in code and built again when the language or the system's mode changes.
/// </summary>
internal sealed class SettingsWindow : Window
{
    const string ThemeTag = "theme", CustomTag = "custom";

    readonly AppHost _host;
    readonly DispatcherTimer _holdDebounce;
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly Dictionary<string, Image> _themeImages = [];
    ThemePreviews? _previews;
    string? _previewKey;
    PageLook _look = PageLook.Dark;
    bool _loading;
    int _commandVersion;

    ComboBox _language = null!, _mode = null!, _terminal = null!;
    TextBox _root = null!, _command = null!, _accentFrom = null!, _accentTo = null!;
    TextBlock _rootStatus = null!, _commandLabel = null!, _commandStatus = null!, _terminalStatus = null!;
    TextBlock _accentName = null!, _accentStatus = null!, _holdValue = null!, _autostartStatus = null!;
    Slider _hold = null!;
    CheckBox _cardPrompt = null!, _cardGit = null!, _cardPlan = null!, _cardTemplates = null!, _autostart = null!, _debug = null!, _quickCard = null!;
    WrapPanel _themeCards = null!;
    StackPanel _swatches = null!, _customAccent = null!;
    Button _openLoginItems = null!, _checkNow = null!;
    CheckBox _checkUpdates = null!;
    TextBlock _updateStatus = null!;
    PermissionRow _accessibility = null!, _finder = null!;
    ColorDot _accentFromDot = null!, _accentToDot = null!;
    readonly ColorPicker _picker = new();
    readonly Flyout _pickerFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    /// <summary>The hex box the open color picker edits.</summary>
    TextBox? _pickingBox;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        _holdDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (_, _) => CommitHold());
        Width = 680;
        Height = 760;
        MinWidth = 560;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily(".AppleSystemUIFont");
        Build();

        Loc.Changed += Rebuild;
        _host.PermissionsChanged += RefreshPermissions;
        ActualThemeVariantChanged += (_, _) => Rebuild();
        Activated += (_, _) =>
        {
            RefreshAutostart();
            _host.CheckFinderAccess();
            RefreshPermissions();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            // Esc closes the color picker first, then the window.
            if (_pickerFlyout.IsOpen)
                _pickerFlyout.Hide();
            else
                Close();
            e.Handled = true;
        };
        SetUpAccentPicker();
        Closed += (_, _) =>
        {
            Loc.Changed -= Rebuild;
            _host.PermissionsChanged -= RefreshPermissions;
            CommitHold();
            _previews?.Dispose();
            _previews = null;
        };
    }

    SettingsStore Store => _host.Store;

    /// <summary>The agent whose command this page edits.</summary>
    static AgentProfile? DefaultAgent(AppSettings settings) => settings.FindAgent(settings.DefaultAgent);

    static bool SystemIsDark => Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

    /// <summary>The page again, in the current language and mode, scrolled where it was.</summary>
    void Rebuild()
    {
        var offset = _scroll.Offset;
        Build();
        Dispatcher.UIThread.Post(() => _scroll.Offset = offset, DispatcherPriority.Loaded);
    }

    /// <summary>Re-reads every field from the current settings (also after edits to settings.json).</summary>
    public void LoadValues()
    {
        _loading = true;
        try
        {
            var s = Store.Current;
            Select(_language, s.Language);
            _root.Text = s.ProjectsRoot;
            _hold.Value = s.HoldMs;
            _holdValue.Text = Loc.Format("Settings.Ms", s.HoldMs);
            _commandLabel.Text = Loc.Format("Settings.Command", DefaultAgent(s)?.Name ?? "");
            _command.Text = DefaultAgent(s)?.Command ?? "";
            Select(_terminal, s.Terminal.ToString());
            _debug.IsChecked = s.DebugLogging;
            _checkUpdates.IsChecked = s.Updates.Check;
            _cardPrompt.IsChecked = s.NewProjectCard.Prompt;
            _quickCard.IsChecked = s.Quick.PromptCard;
            _cardGit.IsChecked = s.NewProjectCard.Git;
            _cardPlan.IsChecked = s.NewProjectCard.Plan;
            _cardTemplates.IsChecked = s.NewProjectCard.Templates;
            LoadAppearance(s.Appearance);
        }
        finally
        {
            _loading = false;
        }
        RefreshAutostart();
        RefreshPermissions();
        UpdateStatus();
        CheckCommand();
        ShowUpdateStatus();
    }

    /// <summary>What the update checks found, and the button that checks now (or opens the update when there is one).</summary>
    public void ShowUpdateStatus()
    {
        var updates = _host.Updates.Service;
        _updateStatus.Text = updates.Status switch
        {
            UpdateCheck.Development => Loc.Get("Settings.Updates.Development"),
            UpdateCheck.Checking => Loc.Get("Settings.Updates.Checking"),
            UpdateCheck.Available when updates.Latest is { } release => Loc.Format("Settings.Updates.Available", release.Version),
            UpdateCheck.UpToDate when updates.LastCheckUtc is { } checkedAt => Loc.Format("Settings.Updates.UpToDate", RelativeTime.Describe(checkedAt)),
            UpdateCheck.Failed => Loc.Get("Settings.Updates.Failed"),
            _ when updates.LastCheckUtc is { } checkedAt => Loc.Format("Settings.Updates.LastChecked", RelativeTime.Describe(checkedAt)),
            _ => Loc.Get("Settings.Updates.NotChecked"),
        };
        bool available = updates.Status == UpdateCheck.Available && updates.Latest is not null;
        _checkNow.Content = Loc.Get(available ? "Settings.Updates.Install" : "Settings.Updates.CheckNow");
        _checkNow.IsEnabled = updates.Status is not (UpdateCheck.Development or UpdateCheck.Checking);
    }

    void UpdateStatus()
    {
        var s = Store.Current;
        string root = s.ResolveProjectsRoot();
        SetStatus(_rootStatus, !Directory.Exists(root) ? Loc.Format("Settings.ProjectsRoot.Missing", root)
            : root != s.ProjectsRoot.Trim() ? Loc.Format("Settings.ProjectsRoot.Resolved", root)
            : null);
        SetStatus(_terminalStatus, s.Terminal == TerminalKind.ITerm && !Workspace.IsInstalled(MacTerminalLauncher.ITermId)
            ? Loc.Get("Settings.Terminal.ITermMissing") : null);
    }

    /// <summary>Looks for the command in the login shell, which takes a moment; only the latest answer is shown.</summary>
    void CheckCommand()
    {
        int version = ++_commandVersion;
        string name = CommandLine.Split(DefaultAgent(Store.Current)?.Command ?? "").Name;
        SetStatus(_commandStatus, Loc.Get("Settings.Command.Checking"));
        _ = Task.Run(() => CommandProbe.CheckAsync(name)).ContinueWith(lookup => Dispatcher.UIThread.Post(() =>
        {
            if (version != _commandVersion)
                return;
            var check = lookup.Result;
            SetStatus(_commandStatus, check.Found is { } found ? Loc.Format("Settings.Command.Found", found)
                : check.Known ? Loc.Get("Settings.Command.NotFound.Mac")
                : Loc.Get("Settings.Command.Unknown.Mac"));
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    void RefreshAutostart()
    {
        bool waiting = _host.Autostart.NeedsApproval;
        _loading = true;
        _autostart.IsChecked = _host.Autostart.IsEnabled || waiting;
        _loading = false;
        if (waiting || _autostartStatus.Text != Loc.Get("Settings.Autostart.Failed"))
            SetStatus(_autostartStatus, waiting ? Loc.Get("Settings.Autostart.Approval") : null);
        _openLoginItems.IsVisible = waiting;
    }

    void RefreshPermissions()
    {
        _accessibility.Show(Native.AXIsProcessTrusted());
        _finder.Show(_host.HasFinderAccess);
    }

    // ---- building ----

    void Build()
    {
        if (_scroll.Parent is Grid previous)
            previous.Children.Remove(_scroll); // kept across builds, for its scroll position
        _look = ActualThemeVariant == ThemeVariant.Light ? PageLook.Light : PageLook.Dark;
        Title = Loc.Get("Settings.Title");
        Background = _look.Background;
        _themeImages.Clear();
        _previewKey = null;

        var page = new StackPanel { Margin = new Thickness(24, 0, 24, 16) };
        page.Children.Add(Section("Settings.General"));
        page.Children.Add(Card(Row(Loc.Get("Settings.Language"), Loc.Get("Settings.Language.Desc"),
            _language = Combo(OnLanguageChanged, ("auto", Loc.Get("Settings.Language.Auto.Mac")), ("en", "English"), ("tr", "Türkçe")))));
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                Label(Loc.Get("Settings.ProjectsRoot")),
                Description(Loc.Get("Settings.ProjectsRoot.Desc")),
                Columns(_root = Box(CommitRoot), Button(Loc.Get("Settings.Browse"), BrowseRoot), new Thickness(0, 10, 0, 0)),
                (_rootStatus = Status()),
            },
        }));

        page.Children.Add(Section("Settings.Appearance"));
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                Label(Loc.Get("Settings.Theme")),
                Description(Loc.Get("Settings.Theme.Desc")),
                (_themeCards = new WrapPanel { Margin = new Thickness(0, 12, 0, -10) }),
            },
        }));
        page.Children.Add(Card(Row(Loc.Get("Settings.Mode"), Loc.Get("Settings.Mode.Desc.Mac"),
            _mode = Combo(OnModeChanged, ("Auto", Loc.Get("Settings.Mode.Auto.Mac")), ("Dark", Loc.Get("Settings.Mode.Dark")), ("Light", Loc.Get("Settings.Mode.Light"))))));
        _accentFrom = Box(CommitCustomAccent);
        _accentTo = Box(CommitCustomAccent);
        _accentFrom.Width = _accentTo.Width = 130;
        _accentFromDot = new ColorDot(_look);
        _accentToDot = new ColorDot(_look);
        foreach (var (box, dot) in new[] { (_accentFrom, _accentFromDot), (_accentTo, _accentToDot) })
        {
            ToolTip.SetTip(dot, Loc.Get("Settings.Accent.Pick"));
            AutomationProperties.SetName(dot, Loc.Get("Settings.Accent.Pick"));
            dot.Clicked += () => PickAccentColor(box);
            box.InnerLeftContent = dot;
            box.TextChanged += (_, _) => ShowAccentDots();
        }
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                Label(Loc.Get("Settings.Accent")),
                Description(Loc.Get("Settings.Accent.Desc")),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(-3, 10, 0, 0),
                    Children =
                    {
                        (_swatches = new StackPanel { Orientation = Orientation.Horizontal }),
                        (_accentName = new TextBlock { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = _look.Text }),
                    },
                },
                (_customAccent = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 10, 0, 0),
                    IsVisible = false,
                    Children =
                    {
                        Inline(Loc.Get("Settings.Accent.From")),
                        _accentFrom,
                        Inline(Loc.Get("Settings.Accent.To"), left: 20),
                        _accentTo,
                    },
                }),
                (_accentStatus = Status()),
            },
        }));
        _accentFrom.Margin = _accentTo.Margin = new Thickness(8, 0, 0, 0);

        page.Children.Add(Section("Settings.Trigger"));
        _hold = new Slider
        {
            Width = 180, Minimum = 150, Maximum = 1000, SmallChange = 50, LargeChange = 100, TickFrequency = 50,
            IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
        };
        _hold.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                OnHoldChanged();
        };
        _holdValue = new TextBlock { Width = 60, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Foreground = _look.Text };
        page.Children.Add(Card(Row(Loc.Get("Settings.HoldMs"), Loc.Get("Settings.HoldMs.Desc.Mac"),
            new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _hold, _holdValue } })));

        page.Children.Add(Section("Settings.Launch"));
        _commandLabel = Label("");
        _command = Box(CommitCommand);
        _command.Width = 240;
        _command.VerticalAlignment = VerticalAlignment.Center;
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                Row(_commandLabel, Loc.Get("Settings.Command.Desc.Mac"), _command),
                (_commandStatus = Status()),
            },
        }));
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                Row(Loc.Get("Settings.Terminal"), Loc.Get("Settings.Terminal.Desc"),
                    _terminal = Combo(OnTerminalChanged, ("Auto", Loc.Get("Settings.Terminal.Auto")),
                        ("TerminalApp", Loc.Get("Settings.Terminal.TerminalApp")), ("ITerm", Loc.Get("Settings.Terminal.ITerm")))),
                (_terminalStatus = Status()),
            },
        }));

        page.Children.Add(Section("Settings.NewProject"));
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                Label(Loc.Get("Settings.NewProject.Options")),
                Description(Loc.Get("Settings.NewProject.Options.Desc")),
                new WrapPanel
                {
                    Margin = new Thickness(0, 10, 0, 0),
                    Children =
                    {
                        (_cardPrompt = Check(Loc.Get("NewProject.Prompt"), OnNewProjectCardClicked, right: 24)),
                        (_cardGit = Check(Loc.Get("NewProject.Git"), OnNewProjectCardClicked, right: 24)),
                        (_cardPlan = Check(Loc.Get("NewProject.Plan"), OnNewProjectCardClicked, right: 24)),
                        (_cardTemplates = Check(Loc.Get("NewProject.Templates"), OnNewProjectCardClicked)),
                    },
                },
            },
        }));
        page.Children.Add(Card(Row(Loc.Get("Settings.Templates"), Loc.Get("Settings.Templates.Desc"),
            Button(Loc.Get("NewProject.OpenTemplates"), _host.OpenTemplatesFolder))));

        page.Children.Add(Section("Settings.Quick"));
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                (_quickCard = Check(Loc.Get("Settings.Quick.Card"), OnQuickCardClicked)),
                Description(Loc.Get("Settings.Quick.Card.Desc"), new Thickness(28, 2, 0, 0)),
            },
        }));

        page.Children.Add(Section("Settings.Permissions"));
        page.Children.Add(Card(_accessibility = new PermissionRow(Loc.Get("Settings.Permissions.Accessibility"),
            Loc.Get("Settings.Permissions.Accessibility.Desc"), _host.AllowAccessibility, _look)));
        page.Children.Add(Card(_finder = new PermissionRow(Loc.Get("Settings.Permissions.Finder"),
            Loc.Get("Settings.Permissions.Finder.Desc"), _host.AllowFinderAccess, _look)));

        page.Children.Add(Section("Settings.System"));
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                (_autostart = Check(Loc.Get("Settings.Autostart.Mac"), OnAutostartClicked)),
                (_autostartStatus = Status()),
                (_openLoginItems = Button(Loc.Get("Settings.Autostart.OpenLoginItems"), Settings.Autostart.OpenLoginItems)),
            },
        }));
        _openLoginItems.HorizontalAlignment = HorizontalAlignment.Left;
        _openLoginItems.Margin = new Thickness(0, 8, 0, 0);
        page.Children.Add(Card(Row(Loc.Get("Settings.Sessions"), Loc.Get("Settings.Sessions.Desc"),
            Button(Loc.Get("Settings.Sessions.Open"), _host.OpenSessions))));
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                (_debug = Check(Loc.Get("Settings.Debug"), OnDebugClicked)),
                Description(Loc.Get("Settings.Debug.Desc.Mac"), new Thickness(28, 2, 0, 0)),
            },
        }));

        page.Children.Add(Section("Settings.Updates"));
        _updateStatus = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = _look.SubText };
        _checkNow = Button("", OnCheckNow);
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                (_checkUpdates = Check(Loc.Get("Settings.Updates.Check"), OnCheckUpdatesClicked)),
                Description(Loc.Get("Settings.Updates.Check.Desc"), new Thickness(28, 2, 0, 0)),
                Columns(_updateStatus, _checkNow, new Thickness(0, 12, 0, 0)),
            },
        }));

        _scroll.Content = page;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(Header());
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        var footer = Footer();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        LoadValues();
    }

    /// <summary>Rondel's ring, name and tagline over a thin line in the brand gradient.</summary>
    Control Header()
    {
        var brand = _look.Brand;
        var logo = PageLook.Logo();
        return new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 0),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        logo,
                        new StackPanel
                        {
                            Margin = new Thickness(12, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = AppInfo.Name, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = _look.Text },
                                Description(Loc.Get("Settings.Tagline"), new Thickness(0)),
                            },
                        },
                    },
                },
                new Border { Height = 2, Margin = new Thickness(0, 14, 0, 0), CornerRadius = new CornerRadius(1), Background = brand },
            },
        };
    }

    Control Footer()
    {
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Button(Loc.Get("Settings.EditJson"), _host.OpenSettingsFile),
                Button(Loc.Get("Settings.OpenLogs"), _host.OpenLogFolder),
                Description(Loc.Format("Settings.Version", AppInfo.Version), new Thickness(12, 0, 0, 0)),
            },
        };
        ((Control)left.Children[1]).Margin = new Thickness(8, 0, 0, 0);
        ((Control)left.Children[2]).VerticalAlignment = VerticalAlignment.Center;
        var close = Button(Loc.Get("Settings.Close"), Close);
        close.MinWidth = 90;
        return new Border
        {
            Padding = new Thickness(24, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = _look.CardStroke,
            Child = Columns(left, close, new Thickness(0)),
        };
    }

    // ---- appearance ----

    void LoadAppearance(AppearanceOptions appearance)
    {
        BuildThemeCards(appearance);
        Select(_mode, appearance.Mode.ToString());
        BuildSwatches(appearance);
        RefreshPreviews(appearance, force: true);
    }

    void BuildThemeCards(AppearanceOptions appearance)
    {
        _themeCards.Children.Clear();
        _themeImages.Clear();
        string chosen = appearance.ResolveTheme().Id;
        var cards = new List<Choice>();
        foreach (var theme in BuiltInThemes.All)
        {
            string name = Loc.Resolve(theme.Name);
            var image = new Image { Width = 88, Height = 88, Margin = new Thickness(2, 0, 2, 6) };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            var card = new Choice(_look, round: false)
            {
                Width = 102,
                Padding = new Thickness(5, 6, 5, 8),
                Margin = new Thickness(0, 0, 10, 10),
                Child = new StackPanel
                {
                    Children =
                    {
                        image,
                        new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = _look.Text },
                    },
                },
            };
            AutomationProperties.SetName(card, name);
            card.IsChecked = theme.Id == chosen;
            card.Chosen += () =>
            {
                foreach (var other in cards)
                    other.IsChecked = other == card;
                OnThemeChosen(theme.Id);
            };
            cards.Add(card);
            _themeImages[theme.Id] = image;
            _themeCards.Children.Add(card);
        }
    }

    void OnThemeChosen(string id)
    {
        if (_loading)
            return;
        Store.Update(s => s.Appearance.Theme = id);
        BuildSwatches(Store.Current.Appearance); // "the theme's own" shows the new theme's colors
    }

    void OnModeChanged()
    {
        if (_loading || !Enum.TryParse(SelectedTag(_mode), out ColorMode mode))
            return;
        Store.Update(s => s.Appearance.Mode = mode);
        BuildSwatches(Store.Current.Appearance);
        RefreshPreviews(Store.Current.Appearance);
    }

    void BuildSwatches(AppearanceOptions appearance)
    {
        bool loading = _loading;
        _loading = true;
        try
        {
            _swatches.Children.Clear();
            var own = appearance.ResolveTheme().For(appearance.IsDark(SystemIsDark));
            string current = CurrentAccent(appearance);
            var swatches = new List<Choice>();
            void Add(string tag, string name, uint from, uint to, string? glyph = null)
            {
                var swatch = new Choice(_look, round: true)
                {
                    Width = 34,
                    Height = 34,
                    Padding = new Thickness(3),
                    Margin = new Thickness(0, 0, 4, 0),
                    Child = new Panel
                    {
                        Children =
                        {
                            new Ellipse { Fill = Gradient(from, to) },
                            new TextBlock
                            {
                                Text = glyph ?? "", FontSize = 11, Foreground = Brushes.White,
                                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                            },
                        },
                    },
                    IsChecked = tag == current,
                };
                ToolTip.SetTip(swatch, name);
                AutomationProperties.SetName(swatch, name);
                swatch.Chosen += () =>
                {
                    foreach (var other in swatches)
                        other.IsChecked = other == swatch;
                    OnAccentChosen(tag);
                };
                swatches.Add(swatch);
                _swatches.Children.Add(swatch);
            }
            Add(ThemeTag, Loc.Get("Accent.Theme"), own.AccentFrom, own.AccentTo);
            foreach (var preset in BuiltInThemes.Accents)
                Add(preset.Id, Loc.Resolve(preset.Name), preset.From, preset.To);
            uint? from = AppearanceOptions.ParseColor(appearance.AccentFrom), to = AppearanceOptions.ParseColor(appearance.AccentTo);
            Add(CustomTag, Loc.Get("Accent.Custom"), from ?? 0xFF6B7280, to ?? from ?? 0xFFD1D5DB, glyph: "✎");
            _accentFrom.Text = appearance.AccentFrom;
            _accentTo.Text = appearance.AccentTo;
            ShowAccent(current);
        }
        finally
        {
            _loading = loading;
        }
    }

    /// <summary>Which swatch the saved colors are: none (the theme's own), a ready-made pair, or custom ones.</summary>
    static string CurrentAccent(AppearanceOptions appearance)
    {
        uint? from = AppearanceOptions.ParseColor(appearance.AccentFrom), to = AppearanceOptions.ParseColor(appearance.AccentTo);
        if (from is null && to is null)
            return ThemeTag;
        (from, to) = (from ?? to, to ?? from);
        return BuiltInThemes.Accents.FirstOrDefault(p => p.From == from && p.To == to)?.Id ?? CustomTag;
    }

    void ShowAccent(string tag)
    {
        _accentName.Text = tag switch
        {
            ThemeTag => Loc.Get("Accent.Theme"),
            CustomTag => Loc.Get("Accent.Custom"),
            _ => Loc.Resolve(BuiltInThemes.Accents.First(p => p.Id == tag).Name),
        };
        _customAccent.IsVisible = tag == CustomTag;
        SetStatus(_accentStatus, null);
    }

    void OnAccentChosen(string tag)
    {
        if (_loading)
            return;
        ShowAccent(tag);
        if (tag == CustomTag)
        {
            // Start from the colors in use, so choosing Custom changes nothing until they are edited.
            var palette = Store.Current.Appearance.ResolvePalette(SystemIsDark);
            _accentFrom.Text = AppearanceOptions.FormatColor(palette.AccentFrom);
            _accentTo.Text = AppearanceOptions.FormatColor(palette.AccentTo);
            CommitCustomAccent();
            _accentFrom.Focus();
            return;
        }
        var preset = BuiltInThemes.Accents.FirstOrDefault(p => p.Id == tag);
        Store.Update(s =>
        {
            s.Appearance.AccentFrom = preset is null ? "" : AppearanceOptions.FormatColor(preset.From);
            s.Appearance.AccentTo = preset is null ? "" : AppearanceOptions.FormatColor(preset.To);
        });
        RefreshPreviews(Store.Current.Appearance);
    }

    /// <summary>
    /// The color picker of a custom accent color: it edits the hex box as it is dragged and saves the color once the
    /// mouse is released, as Enter in the box does.
    /// </summary>
    void SetUpAccentPicker()
    {
        _pickerFlyout.Content = _picker;
        _picker.Changing += color =>
        {
            if (_pickingBox is not null)
                _pickingBox.Text = AppearanceOptions.FormatColor(color);
        };
        _picker.Committed += color =>
        {
            if (_pickingBox is null)
                return;
            _pickingBox.Text = AppearanceOptions.FormatColor(color);
            CommitCustomAccent();
        };
        _pickerFlyout.Closed += (_, _) => _pickingBox = null;
    }

    /// <summary>Opens the color picker under <paramref name="box"/>, on the color in it.</summary>
    void PickAccentColor(TextBox box)
    {
        _pickingBox = box;
        _picker.SetColor(PickerStart(box));
        _pickerFlyout.ShowAt(box);
    }

    /// <summary>The box's color; an empty end is the start color once more, so the picker starts from it.</summary>
    uint PickerStart(TextBox box) =>
        AppearanceOptions.ParseColor(box.Text) ?? AppearanceOptions.ParseColor(_accentFrom.Text) ?? 0xFF6B7280;

    /// <summary>The dots show the colors as they are typed; an empty end shows the start color it repeats.</summary>
    void ShowAccentDots()
    {
        uint? from = AppearanceOptions.ParseColor(_accentFrom.Text);
        _accentFromDot.Show(from);
        _accentToDot.Show(AppearanceOptions.ParseColor(_accentTo.Text) ?? ((_accentTo.Text ?? "").Trim().Length == 0 ? from : null));
    }

    void CommitCustomAccent()
    {
        if (_loading)
            return;
        string from = _accentFrom.Text?.Trim() ?? "", to = _accentTo.Text?.Trim() ?? "";
        bool valid = AppearanceOptions.ParseColor(from) is not null && (to.Length == 0 || AppearanceOptions.ParseColor(to) is not null);
        SetStatus(_accentStatus, valid ? null : Loc.Get("Settings.Accent.Invalid"));
        if (!valid)
            return;
        var appearance = Store.Current.Appearance;
        if (AppearanceOptions.ParseColor(from) == AppearanceOptions.ParseColor(appearance.AccentFrom)
            && AppearanceOptions.ParseColor(to) == AppearanceOptions.ParseColor(appearance.AccentTo))
        {
            return;
        }
        Store.Update(s =>
        {
            s.Appearance.AccentFrom = from;
            s.Appearance.AccentTo = to;
        });
        // The custom swatch shows the new colors; the ring stays on Custom even when they match a ready-made pair.
        if (_swatches.Children.OfType<Choice>().LastOrDefault() is { Child: Panel { Children: [Ellipse dot, ..] } })
        {
            uint first = AppearanceOptions.ParseColor(from)!.Value;
            dot.Fill = Gradient(first, AppearanceOptions.ParseColor(to) ?? first);
        }
        RefreshPreviews(Store.Current.Appearance);
    }

    /// <summary>
    /// Draws the theme pictures in the current mode and accent, one at a time while the window is idle, so opening
    /// Settings never waits for them.
    /// </summary>
    void RefreshPreviews(AppearanceOptions appearance, bool force = false)
    {
        bool systemDark = SystemIsDark;
        string key = $"{appearance.IsDark(systemDark)}|{appearance.AccentFrom}|{appearance.AccentTo}|{Loc.Language}";
        if (!force && key == _previewKey)
            return;
        _previewKey = key;
        _previews ??= new ThemePreviews();
        foreach (var theme in BuiltInThemes.All)
        {
            var palette = new AppearanceOptions
            {
                Theme = theme.Id,
                Mode = appearance.Mode,
                AccentFrom = appearance.AccentFrom,
                AccentTo = appearance.AccentTo,
            }.ResolvePalette(systemDark);
            Dispatcher.UIThread.Post(() =>
            {
                if (_previewKey != key || _previews is null || !_themeImages.TryGetValue(theme.Id, out var image))
                    return;
                image.Source = _previews.Render(theme, palette);
            }, DispatcherPriority.Background);
        }
    }

    // ---- handlers ----

    void OnLanguageChanged()
    {
        if (!_loading && SelectedTag(_language) is { } language)
            Store.Update(s => s.Language = language);
    }

    void CommitRoot()
    {
        string value = _root.Text?.Trim() ?? "";
        if (!_loading && value != Store.Current.ProjectsRoot)
            Store.Update(s => s.ProjectsRoot = value);
        UpdateStatus();
    }

    async void BrowseRoot()
    {
        string current = Store.Current.ResolveProjectsRoot();
        var start = await StorageProvider.TryGetFolderFromPathAsync(Directory.Exists(current)
            ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { SuggestedStartLocation = start });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _root.Text = path;
            CommitRoot();
        }
    }

    void OnHoldChanged()
    {
        if (_holdValue is null)
            return;
        _holdValue.Text = Loc.Format("Settings.Ms", (int)_hold.Value);
        if (_loading)
            return;
        _holdDebounce.Stop();
        _holdDebounce.Start();
    }

    void CommitHold()
    {
        _holdDebounce.Stop();
        int value = (int)_hold.Value;
        if (value != Store.Current.HoldMs)
            Store.Update(s => s.HoldMs = value);
    }

    void CommitCommand()
    {
        string value = _command.Text?.Trim() ?? "";
        if (_loading || value == DefaultAgent(Store.Current)?.Command)
            return;
        Store.Update(s =>
        {
            if (DefaultAgent(s) is { } agent)
                agent.Command = value;
        });
        CheckCommand();
    }

    void OnTerminalChanged()
    {
        if (!_loading && Enum.TryParse(SelectedTag(_terminal), out TerminalKind terminal))
        {
            Store.Update(s => s.Terminal = terminal);
            UpdateStatus();
        }
    }

    void OnQuickCardClicked()
    {
        if (_loading)
            return;
        bool card = _quickCard.IsChecked == true;
        Store.Update(s => s.Quick.PromptCard = card);
    }

    void OnNewProjectCardClicked()
    {
        if (_loading)
            return;
        bool prompt = _cardPrompt.IsChecked == true, git = _cardGit.IsChecked == true;
        bool plan = _cardPlan.IsChecked == true, templates = _cardTemplates.IsChecked == true;
        Store.Update(s =>
        {
            s.NewProjectCard.Prompt = prompt;
            s.NewProjectCard.Git = git;
            s.NewProjectCard.Plan = plan;
            s.NewProjectCard.Templates = templates;
        });
    }

    void OnAutostartClicked()
    {
        if (_loading)
            return;
        try
        {
            _host.Autostart.Set(_autostart.IsChecked == true);
            SetStatus(_autostartStatus, null);
        }
        catch (InvalidOperationException ex)
        {
            Core.Logging.Log.Warn(ex.Message);
            SetStatus(_autostartStatus, Loc.Get("Settings.Autostart.Failed"));
        }
        RefreshAutostart();
    }

    void OnCheckUpdatesClicked()
    {
        bool value = _checkUpdates.IsChecked == true;
        if (!_loading)
            Store.Update(s => s.Updates.Check = value);
    }

    void OnCheckNow()
    {
        var updates = _host.Updates;
        if (updates.Service.Status == UpdateCheck.Available && updates.Service.Latest is { } release)
            updates.OpenWindow(release);
        else
            _ = updates.CheckNowAsync();
    }

    void OnDebugClicked()
    {
        bool value = _debug.IsChecked == true;
        if (!_loading)
            Store.Update(s => s.DebugLogging = value);
    }

    // ---- pieces ----

    TextBlock Section(string key) => new()
    {
        Text = Loc.Get(key), FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 18, 0, 8), Foreground = _look.Text,
    };

    Border Card(Control child) => new()
    {
        Padding = new Thickness(16, 12),
        Margin = new Thickness(0, 0, 0, 4),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        Background = _look.Card,
        BorderBrush = _look.CardStroke,
        Child = child,
    };

    TextBlock Label(string text) => new() { Text = text, FontSize = 13, Foreground = _look.Text };

    TextBlock Description(string text, Thickness? margin = null) => new()
    {
        Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 2, 0, 0), Foreground = _look.SubText,
    };

    TextBlock Status() => new()
    {
        FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = _look.SubText, IsVisible = false,
    };

    TextBlock Inline(string text, double left = 0) => new()
    {
        Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 0, 0), Foreground = _look.Text,
    };

    /// <summary>Title and description on the left, the control on the right.</summary>
    Grid Row(string title, string description, Control control) => Row(Label(title), description, control);

    Grid Row(TextBlock title, string description, Control control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Children = { title, Description(description) } };
        control.VerticalAlignment = VerticalAlignment.Center;
        return Columns(text, control, new Thickness(0));
    }

    static Grid Columns(Control left, Control right, Thickness margin)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = margin };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        if (right is Button)
            right.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(right);
        return grid;
    }

    static ComboBox Combo(Action changed, params (string Tag, string Text)[] items)
    {
        var box = new ComboBox { Width = 240 };
        foreach (var (tag, text) in items)
            box.Items.Add(new ComboBoxItem { Tag = tag, Content = text });
        box.SelectionChanged += (_, _) => changed();
        return box;
    }

    static TextBox Box(Action commit)
    {
        var box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        box.LostFocus += (_, _) => commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            commit();
        };
        return box;
    }

    static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 5) };
        button.Click += (_, _) => click();
        return button;
    }

    CheckBox Check(string text, Action click, double right = 0)
    {
        var box = new CheckBox { Content = text, Margin = new Thickness(0, 0, right, 0), Foreground = _look.Text };
        box.Click += (_, _) => click();
        return box;
    }

    static void Select(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    static void SetStatus(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.IsVisible = !string.IsNullOrEmpty(text);
    }

    static LinearGradientBrush Gradient(uint from, uint to) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(ToColor(from), 0), new GradientStop(ToColor(to), 1) },
    };

    static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>
    /// A theme or an accent to choose: its content in a frame that is lit while chosen. A click, Space or Enter chooses
    /// it; Tab moves between them.
    /// </summary>
    sealed class Choice : Border
    {
        readonly PageLook _look;
        readonly bool _round;
        bool _checked;

        public Choice(PageLook look, bool round)
        {
            _look = look;
            _round = round;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            BorderThickness = new Thickness(2);
            CornerRadius = new CornerRadius(round ? 17 : 8);
            PointerEntered += (_, _) => Paint();
            PointerExited += (_, _) => Paint();
            GotFocus += (_, _) => Paint();
            LostFocus += (_, _) => Paint();
            PointerReleased += (_, _) => Choose();
            KeyDown += (_, e) =>
            {
                if (e.Key is Key.Space or Key.Enter)
                {
                    e.Handled = true;
                    Choose();
                }
            };
            Paint();
        }

        public event Action? Chosen;

        public bool IsChecked
        {
            get => _checked;
            set
            {
                _checked = value;
                Paint();
            }
        }

        void Choose()
        {
            if (!_checked)
                Chosen?.Invoke();
        }

        void Paint()
        {
            Background = _round ? Brushes.Transparent : _look.Idle;
            BorderBrush = _checked ? (_round ? _look.Text : _look.Accent)
                : IsFocused ? _look.SubText
                : IsPointerOver ? _look.Hover
                : Brushes.Transparent;
        }
    }
}
