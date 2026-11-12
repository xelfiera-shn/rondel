using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Rondel.Core.Activity;
using Rondel.Core.Localization;
using Rondel.Core.Menus;
using Rondel.Core.Settings;

namespace Rondel.Windows.UI;

/// <summary>
/// The agents card: the agents Rondel follows, waiting ones first, each with a dot in its state's color and what it
/// is doing; a click on one brings its terminal to the front. Collapsed, it is Rondel's mark with the number of
/// agents that need the user. The header holds the quick settings and the pin that keeps it open. With the usage limits
/// on, the open card lists them under the agents, and collapsed it is a pill with a short bar for each.
/// </summary>
public partial class AgentsCardWindow : Window
{
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    RondelPalette _palette = RondelPalette.For(new ThemeColors(true));
    bool _pinned;
    bool _collapsed;
    bool _usageOn;
    UsageLimits? _usage;
    DateTimeOffset _usageNow;

    internal AgentsCardWindow()
    {
        InitializeComponent();
        Mark.Source = AppIconRenderer.Render(36);
        BadgeMark.Source = AppIconRenderer.Render(60);
        PillMark.Source = BadgeMark.Source;
        MenuGlyph.Text = ""; // More
        MenuButton.Click += (_, _) => MenuRequested?.Invoke();
        PinButton.Click += (_, _) => PinToggled?.Invoke(!_pinned);
        SetPinned(false);
        ApplyLook(_palette);
    }

    /// <summary>An agent was clicked: bring its terminal to the front.</summary>
    internal event Action<string>? AgentClicked;

    /// <summary>The menu button in the header: show the quick settings.</summary>
    internal event Action? MenuRequested;

    /// <summary>The pin was clicked; true: keep the card open.</summary>
    internal event Action<bool>? PinToggled;

    internal bool IsCollapsed => _collapsed;

    /// <summary>The header and the collapsed badge or pill, where the card is dragged from.</summary>
    internal FrameworkElement DragHandle => !_collapsed ? Header : _usageOn ? Pill : Badge;

    /// <summary>
    /// The collapsed card's size with the margin for its shadow: the round badge, or the pill while the usage limits
    /// are on (device-independent pixels).
    /// </summary>
    internal Size CollapsedSize
    {
        get
        {
            double margin = Root.Margin.Left + Root.Margin.Right;
            if (!_usageOn)
                return new Size(Badge.Width + margin, Badge.Height + margin);
            PillContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = PillContent.DesiredSize;
            return new Size(size.Width + margin, size.Height + margin);
        }
    }

    internal void ApplyLook(RondelPalette palette)
    {
        _palette = palette;
        Card.Background = palette.Card;
        Card.BorderBrush = palette.Brand;
        CardShadow.Opacity = palette.ShadowOpacity;
        BadgeBack.Fill = palette.Card;
        BadgeBack.Stroke = palette.Brand;
        BadgeShadow.Opacity = palette.ShadowOpacity;
        PillBack.Background = palette.Card;
        PillBack.BorderBrush = palette.Brand;
        PillShadow.Opacity = palette.ShadowOpacity;
        UsageDivider.Background = palette.TileStroke;
        UsageTitle.Foreground = palette.SubText;
        UsageAsOf.Foreground = palette.SubText;
        UsageEmpty.Foreground = palette.SubText;
        Divider.Background = palette.TileStroke;
        TitleText.Foreground = palette.Text;
        EmptyText.Foreground = palette.SubText;
        MenuGlyph.Foreground = palette.SubText;
        PinGlyph.Foreground = _pinned ? palette.Brand : palette.SubText;
        Resources["HoverBrush"] = Hover(palette);
        if (_usageOn)
            ShowUsage(true, _usage, _usageNow);
    }

    /// <summary>Shows the agents, in the order <see cref="AgentActivity.Agents"/> gives them (waiting ones first).</summary>
    internal void ShowAgents(IReadOnlyList<ActiveAgent> agents)
    {
        TitleText.Text = Loc.Get("Card.Title");
        EmptyText.Text = Loc.Get("Card.Empty");
        var (summary, _, urgent) = MenuBuilder.AgentsSummary(agents);
        SummaryText.Text = agents.Count == 0 ? "" : summary;
        SummaryText.Foreground = urgent == AgentState.Waiting ? new SolidColorBrush(NoticeIconRenderer.Waiting) : _palette.SubText;

        Rows.Children.Clear();
        foreach (var agent in agents)
            Rows.Children.Add(Row(agent));
        EmptyText.Visibility = agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Collapsed, the badge counts the agents that need the user: waiting for an answer, or stopped by an error. It is
        // red as soon as one of them failed, amber while they only wait.
        int waiting = agents.Count(a => a.State == AgentState.Waiting), failed = agents.Count(a => a.State == AgentState.Failed);
        CountText.Text = (waiting + failed).ToString(Loc.Culture);
        CountBack.Background = new SolidColorBrush(failed > 0 ? NoticeIconRenderer.Failed : NoticeIconRenderer.Waiting);
        CountBack.Visibility = waiting + failed > 0 ? Visibility.Visible : Visibility.Collapsed;
        PillCountText.Text = CountText.Text;
        PillCountBack.Background = CountBack.Background;
        PillCountBack.Visibility = CountBack.Visibility;
        Badge.ToolTip = Pill.ToolTip = agents.Count == 0 ? Loc.Get("Card.Empty") : summary;
    }

    /// <summary>The card open, or collapsed to its badge (the pill while the usage limits are on).</summary>
    internal void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        Card.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Badge.Visibility = collapsed && !_usageOn ? Visibility.Visible : Visibility.Collapsed;
        Pill.Visibility = collapsed && _usageOn ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the plan's usage limits (<paramref name="on"/>) or takes them away: in the open card under the agents, and
    /// collapsed as the pill. <paramref name="now"/> decides what has reset and what is old.
    /// </summary>
    internal void ShowUsage(bool on, UsageLimits? limits, DateTimeOffset now)
    {
        _usageOn = on;
        _usage = limits;
        _usageNow = now;
        UsagePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SetCollapsed(_collapsed);
        UsageRows.Children.Clear();
        PillRows.Children.Clear();
        PillRows.RowDefinitions.Clear();
        PillRows.ColumnDefinitions.Clear();
        if (!on)
            return;

        UsageTitle.Text = Loc.Get("Card.Usage");
        bool none = limits?.FiveHour is null && limits?.Weekly is null;
        UsageAsOf.Text = limits is null || none ? "" : UsageText.AsOf(limits, now) ?? "";
        UsageEmpty.Text = Loc.Get("Card.Usage.None");
        UsageEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        if (!none)
        {
            UsageRows.Children.Add(UsageRow(Loc.Get("Card.Usage.FiveHour"), limits!.FiveHour, now, countdown: true));
            UsageRows.Children.Add(UsageRow(Loc.Get("Card.Usage.Weekly"), limits.Weekly, now, countdown: false));
        }

        PillRows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 38 });
        PillRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        PillRows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 36 });
        PillRow(0, Loc.Get("Card.Usage.FiveHour.Short"), limits?.FiveHour, now);
        PillRow(1, Loc.Get("Card.Usage.Weekly.Short"), limits?.Weekly, now);
    }

    /// <summary>One limit in the open card: its name and share, a bar, and when it starts over.</summary>
    StackPanel UsageRow(string label, UsageWindow? window, DateTimeOffset now, bool countdown)
    {
        double? share = UsageText.Share(window, now);
        var line = new Grid();
        line.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = _palette.Text });
        line.Children.Add(new TextBlock
        {
            Text = UsageText.Percent(window, now),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush?)ShareBrush(share) ?? _palette.Text,
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        var row = new StackPanel { Margin = new Thickness(14, 5, 14, 3) };
        row.Children.Add(line);
        var bar = Bar(share, 6);
        bar.Margin = new Thickness(0, 5, 0, 4);
        row.Children.Add(bar);
        if (window is not null)
            row.Children.Add(new TextBlock { Text = UsageText.Resets(window, now, countdown), FontSize = 11, Foreground = _palette.SubText, TextWrapping = TextWrapping.Wrap });
        return row;
    }

    /// <summary>One limit in the pill: a short name, a short bar, the share.</summary>
    void PillRow(int index, string label, UsageWindow? window, DateTimeOffset now)
    {
        PillRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(17) });
        var name = new TextBlock { Text = label, FontSize = 10.5, Foreground = _palette.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var bar = Bar(UsageText.Share(window, now), 5);
        bar.VerticalAlignment = VerticalAlignment.Center;
        var share = new TextBlock
        {
            Text = UsageText.Percent(window, now),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = _palette.Text,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetRow(name, index);
        Grid.SetRow(bar, index);
        Grid.SetRow(share, index);
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(share, 2);
        PillRows.Children.Add(name);
        PillRows.Children.Add(bar);
        PillRows.Children.Add(share);
    }

    /// <summary>A bar filled to <paramref name="share"/> percent (empty without one), as wide as its place.</summary>
    Grid Bar(double? share, double height)
    {
        double filled = Math.Clamp(share ?? 0, 0, 100);
        var bar = new Grid { Height = height };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(filled, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - filled, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(height / 2), Background = Track(_palette) };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        if (filled > 0)
        {
            bar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(height / 2),
                MinWidth = height,
                Background = (Brush?)ShareBrush(filled) ?? new LinearGradientBrush(_palette.BrandFrom, _palette.BrandTo, 0),
            });
        }
        return bar;
    }

    /// <summary>Amber from <see cref="UsageText.High"/>, red from <see cref="UsageText.Critical"/>; null below.</summary>
    static SolidColorBrush? ShareBrush(double? share) => share switch
    {
        >= UsageText.Critical => new SolidColorBrush(NoticeIconRenderer.Failed),
        >= UsageText.High => new SolidColorBrush(NoticeIconRenderer.Waiting),
        _ => null,
    };

    static SolidColorBrush Track(RondelPalette palette)
    {
        var sub = palette.SubText is SolidColorBrush solid ? solid.Color : Colors.Gray;
        return new SolidColorBrush(Color.FromArgb(0x40, sub.R, sub.G, sub.B));
    }

    internal void SetPinned(bool pinned)
    {
        _pinned = pinned;
        PinGlyph.Text = pinned ? "" : ""; // Pinned, Pin
        PinGlyph.Foreground = pinned ? _palette.Brand : _palette.SubText;
        PinButton.ToolTip = Loc.Get(pinned ? "Card.Unpin" : "Card.Pin");
        MenuButton.ToolTip = Loc.Get("Card.Options");
    }

    /// <summary>One agent: its state's dot, its folder's name, what it does, and its question or error.</summary>
    Border Row(ActiveAgent agent)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = StateBrush(agent.State), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
        var name = new TextBlock { Text = agent.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _palette.Text, TextTrimming = TextTrimming.CharacterEllipsis };
        var state = new TextBlock { Text = Describe(agent), FontSize = 11.5, Foreground = _palette.SubText, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(name);
        text.Children.Add(state);
        if (agent.State is AgentState.Waiting or AgentState.Failed && agent.Tool is null && !string.IsNullOrWhiteSpace(agent.Message))
        {
            text.Children.Add(new TextBlock
            {
                Text = agent.Message,
                FontSize = 11.5,
                FontStyle = FontStyles.Italic,
                Foreground = _palette.SubText,
                Opacity = 0.85,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1);
        grid.Children.Add(dot);
        grid.Children.Add(text);

        var row = new Border { Child = grid, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = agent.Directory };
        row.MouseEnter += (_, _) => row.Background = (Brush)Resources["HoverBrush"];
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => AgentClicked?.Invoke(agent.Id);
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
    /// The quick settings the header's menu button shows: where the card stays, over full-screen apps, notifications
    /// while it is on top, hiding it, and Settings.
    /// </summary>
    internal Border QuickSettings(AgentsCardOptions options, string? hideShortcut, Action<Action<AgentsCardOptions>> change,
        Action hide, Action openSettings)
    {
        var list = new StackPanel();
        list.Children.Add(new TextBlock { Text = Loc.Get("Card.Placement"), FontSize = 11, Foreground = _palette.SubText, Margin = new Thickness(10, 2, 10, 4) });
        list.Children.Add(MenuRow(Loc.Get("Card.OnTop"), options.Placement == CardPlacement.OnTop, null, () => change(o => o.Placement = CardPlacement.OnTop)));
        list.Children.Add(MenuRow(Loc.Get("Card.OnDesktop"), options.Placement == CardPlacement.OnDesktop, null, () => change(o => o.Placement = CardPlacement.OnDesktop)));
        list.Children.Add(Separator());
        list.Children.Add(MenuRow(Loc.Get("Card.OverFullScreen"), options.OverFullScreen, null, () => change(o => o.OverFullScreen = !o.OverFullScreen)));
        list.Children.Add(MenuRow(Loc.Get("Card.Mute"), options.MuteNotifications, null, () => change(o => o.MuteNotifications = !o.MuteNotifications)));
        list.Children.Add(MenuRow(Loc.Get("Card.Usage.Show"), options.ShowUsage, null, () => change(o => o.ShowUsage = !o.ShowUsage)));
        list.Children.Add(Separator());
        list.Children.Add(MenuRow(Loc.Get("Card.Hide"), null, hideShortcut, hide));
        list.Children.Add(MenuRow(Loc.Get("Tray.Settings"), null, null, openSettings));
        return new Border
        {
            Child = list,
            Background = _palette.Card,
            BorderBrush = _palette.TileStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4, 6, 4, 6),
            MinWidth = 240,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = _palette.ShadowOpacity },
        };
    }

    /// <param name="check">Null for a command; otherwise whether the option is on (a check mark).</param>
    /// <param name="shortcut">Shown on the right, if any.</param>
    Border MenuRow(string text, bool? check, string? shortcut, Action click)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (check == true)
            grid.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 11, Foreground = _palette.Brand, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontSize = 12.5, Foreground = _palette.Text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (!string.IsNullOrEmpty(shortcut))
        {
            var keys = new TextBlock { Text = shortcut, FontSize = 11, Foreground = _palette.SubText, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(keys, 2);
            grid.Children.Add(keys);
        }
        var row = new Border { Child = grid, Padding = new Thickness(8, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => row.Background = Hover(_palette);
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => click();
        return row;
    }

    Border Separator() => new() { Height = 1, Margin = new Thickness(10, 5, 10, 5), Background = _palette.TileStroke };

    static Brush Hover(RondelPalette palette)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x26, palette.BrandFrom.R, palette.BrandFrom.G, palette.BrandFrom.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>The state colors of the menu and the notifications; a working agent's dot pulses in the theme's accent.</summary>
    Brush StateBrush(AgentState state)
    {
        if (state != AgentState.Working)
        {
            var solid = new SolidColorBrush(state switch
            {
                AgentState.Waiting => NoticeIconRenderer.Waiting,
                AgentState.Ready => NoticeIconRenderer.Done,
                _ => NoticeIconRenderer.Failed,
            });
            solid.Freeze();
            return solid;
        }
        var working = new SolidColorBrush(_palette.BrandFrom);
        if (SystemParameters.ClientAreaAnimation)
        {
            working.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        return working;
    }
}
