using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Rondel.Core.Activity;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Settings;
using Rondel.MacOS.Interop;
using Path = System.IO.Path;

namespace Rondel.MacOS.UI;

/// <summary>
/// Shows the agents card (<see cref="AgentsCardWindow"/>) and keeps it where the user wants it, as on Windows.
/// <list type="bullet">
/// <item>It appears when the first agent starts and hides when none is left.</item>
/// <item>Collapsed to its badge, it opens while the mouse is over it, toward the free side of the screen; pinned, it
/// stays open. It is dragged by its header or its badge, and its place is kept per screen.</item>
/// <item>Above other windows, or on the desktop: right above its icons, behind every window. On every Space, and over
/// full-screen apps only when the user wants it there.</item>
/// </list>
/// It never takes the focus: it is a non-activating panel, like the menu.
/// </summary>
internal sealed class AgentsCard : IDisposable
{
    static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(450);
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>The collapsed window, with the margin for the shadow (points).</summary>
    const double BadgeSize = AgentsCardWindow.BadgeDiameter + 2 * AgentsCardWindow.ShadowMargin;

    /// <summary>Room left to the screen's edges when the card is placed the first time.</summary>
    const double EdgeGap = 24;

    readonly Func<AgentsCardOptions> _options;
    readonly Action<Action<AgentsCardOptions>> _change;
    readonly string? _stateFile;
    readonly AgentsCardWindow _window;
    readonly DispatcherTimer _collapse, _refresh, _save;
    readonly Dictionary<string, PixelPoint> _positions = [];
    IReadOnlyList<ActiveAgent> _agents = [];
    string? _lastScreen;
    PixelPoint? _anchor;
    bool _enabled, _manualShown, _manualHidden, _hovering, _placing, _flipX, _flipY;
    nint _nsWindow;

    /// <param name="change">Changes the card's options (saved in settings.json, so they are back after a restart).</param>
    /// <param name="stateFile">Where the card's place is kept; null for none (developer aids).</param>
    public AgentsCard(Func<AgentsCardOptions> options, Action<Action<AgentsCardOptions>> change, MenuPalette palette, string? stateFile)
    {
        _options = options;
        _change = change;
        _stateFile = stateFile;
        ReadPositions();
        _window = new AgentsCardWindow(palette);
        _collapse = new DispatcherTimer(CollapseDelay, DispatcherPriority.Normal, (_, _) => Collapse());
        _collapse.Stop();
        _refresh = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => _window.ShowAgents(_agents));
        _refresh.Stop();
        _save = new DispatcherTimer(SaveDelay, DispatcherPriority.Background, (_, _) => KeepPlace());
        _save.Stop();
        _window.Opened += (_, _) =>
        {
            _nsWindow = _window.TryGetPlatformHandle()?.Handle ?? 0;
            MenuPanel.Configure(_nsWindow);
            SetLayer();
        };
        _window.PointerEntered += (_, _) => OnPointerEntered();
        _window.PointerExited += (_, _) => _collapse.Start();
        _window.DragStarted += e => _window.BeginMoveDrag(e);
        _window.PositionChanged += (_, _) =>
        {
            if (!_placing && _window.IsVisible)
                _save.Start(); // dragged: kept once it rests
        };
        _window.AgentClicked += id => AgentClicked?.Invoke(id);
        _window.PinToggled += pinned => _change(o => o.Pinned = pinned);
        _window.OptionChanged += change =>
        {
            _change(change);
            Layout();
        };
        _window.HideRequested += Toggle;
        _window.SettingsRequested += () => SettingsRequested?.Invoke();
        _window.SizeChanged += (_, _) =>
        {
            if (_window.IsVisible)
                Place(!_window.IsCollapsed);
        };
    }

    /// <summary>An agent was clicked on the card.</summary>
    public event Action<string>? AgentClicked;

    /// <summary>Settings… in the card's options.</summary>
    public event Action? SettingsRequested;

    /// <summary>The user hid the card ("Hide the card").</summary>
    public event Action? HiddenByUser;

    /// <summary>The card is on screen (open or collapsed): its badge tells what needs the user.</summary>
    public bool IsShown { get; private set; }

    public void ApplyLook(MenuPalette palette)
    {
        _window.ApplyLook(palette);
        _window.ShowAgents(_agents);
    }

    /// <summary>The agents, or the options, changed.</summary>
    /// <param name="enabled">Whether Rondel follows agents at all.</param>
    public void Update(IReadOnlyList<ActiveAgent> agents, bool enabled)
    {
        // When the last agent is gone, a card the user hid comes back with the next one.
        if (_agents.Count > 0 && agents.Count == 0)
            _manualHidden = false;
        if (!_options().Show)
            _manualShown = _manualHidden = false;
        _agents = agents;
        _enabled = enabled;
        _window.ShowAgents(agents);
        Layout();
    }

    /// <summary>The menu bar's "Show the agents card": brings back a card the user hid, or shows it with no agent.</summary>
    public void ShowAgain()
    {
        if (!_enabled || !_options().Show)
            return;
        _manualHidden = false;
        _manualShown = true;
        Layout();
    }

    /// <summary>Whether "Show the agents card" has something to do: the card is turned on, but not on screen.</summary>
    public bool CanShowAgain => _enabled && _options().Show && !IsShown;

    /// <summary>"Hide the card": hides a card on screen, or shows it, also with no agent.</summary>
    public void Toggle()
    {
        if (!_enabled || !_options().Show)
            return;
        bool hide = IsShown;
        _manualHidden = hide;
        _manualShown = !hide;
        Layout();
        if (hide)
            HiddenByUser?.Invoke();
    }

    /// <summary>Shows, hides, opens or collapses the card, and puts it in its place and layer.</summary>
    void Layout()
    {
        var options = _options();
        bool visible = _enabled && options.Show && !_manualHidden && (_agents.Count > 0 || _manualShown);
        if (!visible)
        {
            Hide();
            return;
        }
        bool open = options.Pinned || _hovering || _window.OptionsOpen;
        _window.SetPinned(options.Pinned);
        _window.SetOptions(options);
        _window.SetCollapsed(!open);
        if (!_window.IsVisible)
            _window.Show();
        SetLayer();
        Place(open);
        IsShown = true;
        _refresh.Start();
    }

    void Hide()
    {
        if (_window.IsVisible)
            _window.Hide();
        _window.CloseOptions();
        IsShown = false;
        _hovering = false;
        _refresh.Stop();
    }

    void SetLayer()
    {
        var options = _options();
        MenuPanel.SetCardLayer(_nsWindow, options.Placement == CardPlacement.OnDesktop, options.OverFullScreen);
    }

    // ---- place ----

    /// <summary>The badge at its anchor; the open card from there toward the free side of the screen, kept on the screen.</summary>
    void Place(bool open)
    {
        var anchor = _anchor ??= StartingPlace();
        double scale = _window.DesktopScaling;
        var size = PixelSize.FromSize(_window.ClientSize, scale);
        int badge = (int)Math.Round(BadgeSize * scale);
        var work = WorkArea(new PixelPoint(anchor.X + badge / 2, anchor.Y + badge / 2));
        int x = anchor.X, y = anchor.Y;
        _flipX = _flipY = false;
        if (open)
        {
            _flipX = anchor.X + size.Width > work.Right && anchor.X + badge - size.Width >= work.X;
            _flipY = anchor.Y + size.Height > work.Bottom && anchor.Y + badge - size.Height >= work.Y;
            if (_flipX)
                x = anchor.X + badge - size.Width;
            if (_flipY)
                y = anchor.Y + badge - size.Height;
        }
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.Right - size.Width));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - size.Height));
        _placing = true;
        _window.Position = new PixelPoint(x, y);
        _placing = false;
    }

    PixelRect WorkArea(PixelPoint point) =>
        (_window.Screens.ScreenFromPoint(point) ?? _window.Screens.Primary)?.WorkingArea ?? new PixelRect(0, 0, 1440, 900);

    /// <summary>Where the card was last on a screen that is still there, or the bottom right of the main one.</summary>
    PixelPoint StartingPlace()
    {
        if (_lastScreen is { } key && _positions.TryGetValue(key, out var saved) && ScreenKey(saved) == key)
            return saved;
        var work = _window.Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1440, 900);
        int offset = (int)Math.Round((BadgeSize + EdgeGap) * _window.DesktopScaling);
        return new PixelPoint(work.Right - offset, work.Bottom - offset);
    }

    /// <summary>After a drag: the badge's corner the card opened from is where it goes back to.</summary>
    void KeepPlace()
    {
        _save.Stop();
        var position = _window.Position;
        var size = PixelSize.FromSize(_window.ClientSize, _window.DesktopScaling);
        int badge = (int)Math.Round(BadgeSize * _window.DesktopScaling);
        bool open = !_window.IsCollapsed;
        var anchor = new PixelPoint(open && _flipX ? position.X + size.Width - badge : position.X,
            open && _flipY ? position.Y + size.Height - badge : position.Y);
        if (_anchor == anchor)
            return;
        _anchor = anchor;
        SavePosition(anchor);
    }

    // ---- open, collapse ----

    void OnPointerEntered()
    {
        _collapse.Stop();
        if (_hovering)
            return;
        _hovering = true;
        Layout();
    }

    void Collapse()
    {
        _collapse.Stop();
        if (_window.IsPointerOver || _window.OptionsOpen)
            return;
        _hovering = false;
        Layout();
    }

    // ---- the card's place, per screen ----

    string? ScreenKey(PixelPoint point) =>
        _window.Screens.ScreenFromPoint(point)?.Bounds is { } b ? $"{b.X},{b.Y},{b.Right},{b.Bottom}" : null;

    void SavePosition(PixelPoint anchor)
    {
        if (ScreenKey(anchor) is not { } key)
            return;
        _positions[key] = anchor;
        _lastScreen = key;
        if (_stateFile is null)
            return;
        var positions = new JsonObject();
        foreach (var (screen, point) in _positions)
            positions[screen] = new JsonArray(point.X, point.Y);
        var json = new JsonObject { ["Last"] = _lastScreen, ["Positions"] = positions };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, json.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Saving the agents card's place to {_stateFile} failed", ex);
        }
    }

    void ReadPositions()
    {
        if (_stateFile is null || !File.Exists(_stateFile))
            return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(_stateFile)) is not JsonObject json)
                return;
            _lastScreen = json["Last"]?.GetValue<string>();
            if (json["Positions"] is JsonObject positions)
            {
                foreach (var (screen, value) in positions)
                {
                    if (value is JsonArray { Count: 2 } point)
                        _positions[screen] = new PixelPoint(point[0]!.GetValue<int>(), point[1]!.GetValue<int>());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            Log.Warn($"Reading {_stateFile} failed", ex);
        }
    }

    /// <summary>Developer aid (--render-card): the card's window, to draw.</summary>
    internal AgentsCardWindow Window => _window;

    public void Dispose()
    {
        _collapse.Stop();
        _refresh.Stop();
        _save.Stop();
        _window.Close();
    }
}

/// <summary>
/// The agents card's window, as on Windows: the agents Rondel follows, waiting ones first, each with a dot in its
/// state's color and what it is doing; a click on one brings its terminal to the front. Collapsed, it is Rondel's mark
/// with the number of agents that need the user. The header holds the card's options and the pin that keeps it open.
/// </summary>
internal sealed class AgentsCardWindow : Window
{
    public const double ShadowMargin = 14, BadgeDiameter = 50;

    readonly Border _card = new() { Width = 300, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.2), Padding = new Thickness(0, 0, 0, 6) };
    readonly Grid _header = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(12, 9, 7, 6), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll) };
    readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _summary = new() { FontSize = 11.5, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Border _menuButton, _pinButton;
    readonly Rectangle _menuGlyph = new() { Width = 13, Height = 13 }, _pinGlyph = new() { Width = 13, Height = 13 };
    readonly Border _divider = new() { Height = 1, Margin = new Thickness(12, 0, 12, 4) };
    readonly StackPanel _rows = new();
    readonly StackPanel _optionsPanel = new() { IsVisible = false, Margin = new Thickness(4, 0, 4, 0) };
    readonly TextBlock _empty = new() { Margin = new Thickness(14, 6, 14, 6), FontSize = 12, IsVisible = false };
    readonly Panel _badge = new() { Width = BadgeDiameter, Height = BadgeDiameter, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsVisible = false, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll) };
    readonly Ellipse _badgeBack = new() { StrokeThickness = 1.2 };
    readonly Border _countBack = new() { MinWidth = 20, Height = 20, CornerRadius = new CornerRadius(10), Padding = new Thickness(5, 0), Margin = new Thickness(0, -5, -5, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _count = new() { FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    MenuPalette _palette;
    AgentsCardOptions _options = new();
    bool _pinned;

    public AgentsCardWindow(MenuPalette palette)
    {
        _palette = palette;
        Title = "Rondel";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily(".AppleSystemUIFont");

        _menuButton = IconButton(_menuGlyph, () =>
        {
            _optionsPanel.IsVisible = !_optionsPanel.IsVisible;
            _rows.IsVisible = !_optionsPanel.IsVisible;
            _empty.IsVisible = !_optionsPanel.IsVisible && _rows.Children.Count == 0;
            if (_optionsPanel.IsVisible)
                BuildOptions();
        });
        _pinButton = IconButton(_pinGlyph, () => PinToggled?.Invoke(!_pinned));
        var mark = new AppIcon.Mark { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _title, _summary } };
        _header.Children.Add(mark);
        _header.Children.Add(heading);
        _header.Children.Add(_menuButton);
        _header.Children.Add(_pinButton);
        Grid.SetColumn(heading, 1);
        Grid.SetColumn(_menuButton, 2);
        Grid.SetColumn(_pinButton, 3);
        _header.PointerPressed += (_, e) => StartDrag(e);
        _card.Child = new StackPanel { Children = { _header, _divider, _rows, _empty, _optionsPanel } };
        _card.Effect = new DropShadowEffect { BlurRadius = 22, OffsetY = 4, Color = Colors.Black, Opacity = 0.35 };

        _badgeBack.Effect = new DropShadowEffect { BlurRadius = 16, OffsetY = 3, Color = Colors.Black, Opacity = 0.35 };
        _countBack.Child = _count;
        _badge.Children.Add(_badgeBack);
        _badge.Children.Add(new AppIcon.Mark { Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        _badge.Children.Add(_countBack);
        _badge.PointerPressed += (_, e) => StartDrag(e);

        Content = new Panel { Margin = new Thickness(ShadowMargin), Children = { _card, _badge } };
        SetPinned(false);
        ApplyLook(palette);
    }

    /// <summary>An agent was clicked: bring its terminal to the front.</summary>
    public event Action<string>? AgentClicked;

    /// <summary>The pin was clicked; true: keep the card open.</summary>
    public event Action<bool>? PinToggled;

    /// <summary>The header or the badge was pressed: the card follows the mouse.</summary>
    public event Action<PointerPressedEventArgs>? DragStarted;

    /// <summary>One of the card's options was changed from its own menu.</summary>
    public event Action<Action<AgentsCardOptions>>? OptionChanged;

    public event Action? HideRequested;

    public event Action? SettingsRequested;

    public bool IsCollapsed => _badge.IsVisible;

    public bool OptionsOpen => _optionsPanel.IsVisible;

    public void ApplyLook(MenuPalette palette)
    {
        _palette = palette;
        _card.Background = palette.Card;
        _card.BorderBrush = palette.Brand;
        _badgeBack.Fill = palette.Card;
        _badgeBack.Stroke = palette.Brand;
        _divider.Background = palette.TileStroke;
        _title.Foreground = palette.Text;
        _empty.Foreground = palette.SubText;
        _menuGlyph.Fill = palette.SubText;
        SetSymbol(_menuGlyph, "ellipsis");
        SetPinned(_pinned);
        if (OptionsOpen)
            BuildOptions();
    }

    /// <summary>Shows the agents, in the order <see cref="AgentActivity.Agents"/> gives them (waiting ones first).</summary>
    public void ShowAgents(IReadOnlyList<ActiveAgent> agents)
    {
        _title.Text = Loc.Get("Card.Title");
        _empty.Text = Loc.Get("Card.Empty");
        var (summary, _, urgent) = MenuBuilder.AgentsSummary(agents);
        _summary.Text = agents.Count == 0 ? "" : summary;
        _summary.Foreground = urgent == AgentState.Waiting ? StateBrush(AgentState.Waiting) : _palette.SubText;
        _rows.Children.Clear();
        foreach (var agent in agents)
            _rows.Children.Add(Row(agent));
        _empty.IsVisible = agents.Count == 0 && !OptionsOpen;
        // Collapsed, the badge counts the agents that need the user: waiting for an answer, or stopped by an error. It is
        // red as soon as one of them failed, amber while they only wait.
        int waiting = agents.Count(a => a.State == AgentState.Waiting), failed = agents.Count(a => a.State == AgentState.Failed);
        _count.Text = (waiting + failed).ToString(Loc.Culture);
        _countBack.Background = StateBrush(failed > 0 ? AgentState.Failed : AgentState.Waiting);
        _countBack.IsVisible = waiting + failed > 0;
        ToolTip.SetTip(_badge, agents.Count == 0 ? Loc.Get("Card.Empty") : summary);
    }

    /// <summary>The card open, or collapsed to its badge.</summary>
    public void SetCollapsed(bool collapsed)
    {
        _card.IsVisible = !collapsed;
        _badge.IsVisible = collapsed;
        if (collapsed)
            CloseOptions();
    }

    public void SetPinned(bool pinned)
    {
        _pinned = pinned;
        SetSymbol(_pinGlyph, pinned ? "pin.fill" : "pin");
        _pinGlyph.Fill = pinned ? _palette.Accent : _palette.SubText;
        ToolTip.SetTip(_pinButton, Loc.Get(pinned ? "Card.Unpin" : "Card.Pin"));
        ToolTip.SetTip(_menuButton, Loc.Get("Card.Options"));
    }

    public void SetOptions(AgentsCardOptions options)
    {
        _options = options;
        if (OptionsOpen)
            BuildOptions();
    }

    public void CloseOptions()
    {
        if (!OptionsOpen)
            return;
        _optionsPanel.IsVisible = false;
        _rows.IsVisible = true;
        _empty.IsVisible = _rows.Children.Count == 0;
    }

    void StartDrag(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !_menuButton.IsPointerOver && !_pinButton.IsPointerOver)
            DragStarted?.Invoke(e);
    }

    /// <summary>One agent: its state's dot, its folder's name, what it does, and its question or error.</summary>
    Border Row(ActiveAgent agent)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = StateBrush(agent.State), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
        var text = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            Children =
            {
                new TextBlock { Text = agent.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = _palette.Text, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = Describe(agent), FontSize = 11.5, Foreground = _palette.SubText, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        if (agent.State is AgentState.Waiting or AgentState.Failed && agent.Tool is null && !string.IsNullOrWhiteSpace(agent.Message))
        {
            text.Children.Add(new TextBlock
            {
                Text = agent.Message, FontSize = 11.5, Foreground = _palette.SubText, Opacity = 0.85, // the system font has no italic here
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0),
            });
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { dot, text } };
        Grid.SetColumn(text, 1);
        var row = new Border
        {
            Child = grid, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(row, agent.Directory);
        row.PointerEntered += (_, _) => row.Background = Hover();
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerReleased += (_, _) => AgentClicked?.Invoke(agent.Id);
        return row;
    }

    /// <summary>What an agent does, a little fuller than on a menu tile: "Needs a permission · Bash", "Asks a question" …</summary>
    static string Describe(ActiveAgent agent) => agent.State switch
    {
        AgentState.Waiting when agent.Tool is not null => Loc.Format("Card.Permission", agent.Tool),
        AgentState.Waiting => Loc.Get("Card.Question"),
        _ => MenuBuilder.DescribeState(agent),
    };

    /// <summary>
    /// The card's options, inside the card where its agents were (a menu of its own would need the focus the card never
    /// takes): where it stays, over full-screen apps, notifications while it is shown, hiding it, and Settings.
    /// </summary>
    void BuildOptions()
    {
        var options = _options;
        _optionsPanel.Children.Clear();
        _optionsPanel.Children.Add(new TextBlock { Text = Loc.Get("Card.Placement"), FontSize = 11, Foreground = _palette.SubText, Margin = new Thickness(10, 2, 10, 4) });
        _optionsPanel.Children.Add(MenuRow(Loc.Get("Card.OnTop"), options.Placement == CardPlacement.OnTop, () => OptionChanged?.Invoke(o => o.Placement = CardPlacement.OnTop)));
        _optionsPanel.Children.Add(MenuRow(Loc.Get("Card.OnDesktop"), options.Placement == CardPlacement.OnDesktop, () => OptionChanged?.Invoke(o => o.Placement = CardPlacement.OnDesktop)));
        _optionsPanel.Children.Add(Separator());
        _optionsPanel.Children.Add(MenuRow(Loc.Get("Card.OverFullScreen"), options.OverFullScreen, () => OptionChanged?.Invoke(o => o.OverFullScreen = !o.OverFullScreen)));
        _optionsPanel.Children.Add(MenuRow(Loc.Get("Card.Mute"), options.MuteNotifications, () => OptionChanged?.Invoke(o => o.MuteNotifications = !o.MuteNotifications)));
        _optionsPanel.Children.Add(Separator());
        _optionsPanel.Children.Add(MenuRow(Loc.Get("Card.Hide"), null, () => HideRequested?.Invoke()));
        _optionsPanel.Children.Add(MenuRow(Loc.Get("Tray.Settings"), null, () => SettingsRequested?.Invoke()));
    }

    /// <param name="check">Null for a command; otherwise whether the option is on (a check mark).</param>
    Border MenuRow(string text, bool? check, Action click)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };
        if (check == true)
            grid.Children.Add(new TextBlock { Text = "✓", FontSize = 12, Foreground = _palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontSize = 12.5, Foreground = _palette.Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var row = new Border { Child = grid, Padding = new Thickness(8, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        row.PointerEntered += (_, _) => row.Background = Hover();
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerReleased += (_, _) => click();
        return row;
    }

    Border Separator() => new() { Height = 1, Margin = new Thickness(10, 5, 10, 5), Background = _palette.TileStroke };

    Border IconButton(Rectangle glyph, Action click)
    {
        var button = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center, Child = glyph,
        };
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        button.PointerEntered += (_, _) => button.Background = Hover();
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        button.PointerPressed += (_, e) => e.Handled = true; // not a drag
        button.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    static void SetSymbol(Rectangle target, string symbol)
    {
        var mask = SymbolIcons.Mask(symbol, 13);
        target.OpacityMask = mask is null ? null : new ImageBrush(mask) { Stretch = Stretch.Uniform };
    }

    IBrush Hover() => new SolidColorBrush(Color.FromArgb(0x26, _palette.BrandFrom.R, _palette.BrandFrom.G, _palette.BrandFrom.B));

    /// <summary>The state colors of the menu and the notifications.</summary>
    IBrush StateBrush(AgentState state) => state switch
    {
        AgentState.Waiting => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        AgentState.Ready => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
        AgentState.Working => _palette.Accent,
        _ => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
    };

    /// <summary>Developer aid (--render-card): the open card, or the badge, drawn into a bitmap at 2x without showing it.</summary>
    public RenderTargetBitmap RenderPreview(Color background, bool collapsed, bool options)
    {
        SetCollapsed(collapsed);
        if (options && !collapsed)
        {
            _optionsPanel.IsVisible = true;
            _rows.IsVisible = _empty.IsVisible = false;
            BuildOptions();
        }
        // Drawn from a panel inside the window, at a fixed size, then cut to what it holds; the shadows are left out, as
        // the render target draws what a shadowed control holds at twice its size.
        const double Width = 340, Height = 700;
        var content = (Panel)Content!;
        Content = null;
        _card.Effect = _badgeBack.Effect = null;
        content.VerticalAlignment = VerticalAlignment.Top;
        content.HorizontalAlignment = HorizontalAlignment.Left;
        var root = new Panel { Width = Width, Height = Height, Background = new SolidColorBrush(background), Children = { content } };
        Content = root;
        SizeToContent = SizeToContent.Manual;
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Measure(new Size(Width, Height));
            Arrange(new Rect(0, 0, Width, Height));
        }
        double width = Math.Ceiling(content.Bounds.Right + ShadowMargin), height = Math.Ceiling(content.Bounds.Bottom + ShadowMargin);
        var bitmap = new RenderTargetBitmap(new PixelSize((int)(width * 2), (int)(height * 2)), new Vector(192, 192));
        bitmap.Render(root);
        return bitmap;
    }
}
