using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Rondel.Core;
using Rondel.Core.Agents;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;
using Rondel.Windows.Launch;
using UpdateCheck = Rondel.Core.Updates.UpdateStatus;

namespace Rondel.Windows.UI;

/// <summary>
/// Simple settings page. Every change is saved and applied immediately; the action/menu
/// configuration is edited in settings.json directly.
/// </summary>
public partial class SettingsWindow : Window
{
    readonly AppHost _host;
    readonly DispatcherTimer _holdDebounce;
    bool _loading;

    internal SettingsWindow(AppHost host)
    {
        _host = host;
        _holdDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (_, _) => CommitHold(), Dispatcher);
        _holdDebounce.Stop();

        ThemeMode = ThemeMode.System; // Fluent styles: native look on Windows 11, follows light/dark
        InitializeComponent();
        Icon = AppIconRenderer.Render(64);
        HeaderIcon.Source = AppIconRenderer.Render(72);
        ApplyBrand(SystemTheme.Read());

        Loc.Changed += LoadValues;
        Closed += (_, _) =>
        {
            Loc.Changed -= LoadValues;
            CommitHold();
            _previews?.Dispose();
            _previews = null;
        };
        Activated += (_, _) => RefreshAutostart();
        LoadValues();
    }

    SettingsStore Store => _host.Store;

    /// <summary>Rondel's teal for the window's controls (<see cref="FluentBrand"/>), and the header's brand line.</summary>
    internal void ApplyBrand(ThemeColors theme)
    {
        FluentBrand.Apply(this, theme);
        BrandLine.Background = FluentBrand.Line(theme);
    }

    /// <summary>The agent whose command this page edits.</summary>
    static AgentProfile? DefaultAgent(AppSettings settings) => settings.FindAgent(settings.DefaultAgent);

    /// <summary>Re-reads every field from the current settings (also after external edits).</summary>
    internal void LoadValues()
    {
        _loading = true;
        try
        {
            var s = Store.Current;
            Select(LanguageBox, s.Language);
            RootBox.Text = s.ProjectsRoot;
            HoldSlider.Value = s.HoldMs;
            HoldValue.Text = Loc.Format("Settings.Ms", s.HoldMs);
            HotkeyBox.Text = string.IsNullOrEmpty(s.Hotkey) ? Loc.Get("Settings.Hotkey.Off") : s.Hotkey;
            AgentsHotkeyBox.Text = string.IsNullOrEmpty(s.ActiveAgents.Hotkey) ? Loc.Get("Settings.Hotkey.Off") : s.ActiveAgents.Hotkey;
            CommandLabel.Text = Loc.Format("Settings.Command", DefaultAgent(s)?.Name ?? "");
            CommandBox.Text = DefaultAgent(s)?.Command ?? "";
            Select(TerminalBox, s.Terminal.ToString());
            Select(ShellBox, s.Shell.ToString());
            DebugBox.IsChecked = s.DebugLogging;
            FollowAgentsBox.IsChecked = s.ActiveAgents.Follow;
            NotifyAsksBox.IsChecked = s.ActiveAgents.NotifyAsks;
            NotifyFinishedBox.IsChecked = s.ActiveAgents.NotifyFinished;
            AgentNotificationsPanel.IsEnabled = s.ActiveAgents.Follow;
            CardPromptBox.IsChecked = s.NewProjectCard.Prompt;
            CardGitBox.IsChecked = s.NewProjectCard.Git;
            CardPlanBox.IsChecked = s.NewProjectCard.Plan;
            CardTemplatesBox.IsChecked = s.NewProjectCard.Templates;
            AutostartBox.IsChecked = _host.Autostart.IsEnabled;
            CheckUpdatesBox.IsChecked = s.Updates.Check;
            LoadAppearance(s);
            VersionText.Text = Loc.Format("Settings.Version", AppInfo.Version);
        }
        finally
        {
            _loading = false;
        }
        UpdateStatus();
        ShowUpdateStatus();
    }

    /// <summary>What the update checks found, and the button that checks now (or opens the update when there is one).</summary>
    internal void ShowUpdateStatus()
    {
        var updates = _host.Updates;
        UpdateStatusText.Text = updates.Status switch
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
        CheckNowButton.Content = Loc.Get(available ? "Settings.Updates.Install" : "Settings.Updates.CheckNow");
        CheckNowButton.IsEnabled = updates.Status is not (UpdateCheck.Development or UpdateCheck.Checking);
    }

    void UpdateStatus()
    {
        var s = Store.Current;

        string root = s.ResolveProjectsRoot();
        SetStatus(RootStatus, !Directory.Exists(root) ? Loc.Format("Settings.ProjectsRoot.Missing", root)
            : !string.Equals(root, s.ProjectsRoot.Trim(), StringComparison.OrdinalIgnoreCase) ? Loc.Format("Settings.ProjectsRoot.Resolved", root)
            : null);

        string? command = CommandResolver.Find(CommandLine.Split(DefaultAgent(s)?.Command ?? "").Name);
        SetStatus(CommandStatus, command is not null ? Loc.Format("Settings.Command.Found", command) : Loc.Get("Settings.Command.NotFound"));

        SetStatus(TerminalStatus, s.Terminal != TerminalKind.Console && CommandResolver.FindWindowsTerminal() is null
            ? Loc.Get("Settings.Terminal.WtMissing") : null);

        SetStatus(ShellStatus, s.Shell == ShellKind.Pwsh && CommandResolver.FindPwsh() is null
            ? Loc.Get("Settings.Shell.PwshMissing") : null);

        SetStatus(HotkeyStatus, _host.HotkeyError);
        SetStatus(AgentsHotkeyStatus, _host.AgentsHotkeyError);
    }

    void RefreshAutostart()
    {
        _loading = true;
        AutostartBox.IsChecked = _host.Autostart.IsEnabled;
        _loading = false;
    }

    // ---- handlers ----

    void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SelectedTag(LanguageBox) is { } language)
            Store.Update(s => s.Language = language);
    }

    void OnRootCommitted(object sender, RoutedEventArgs e) => CommitRoot();

    void CommitRoot()
    {
        string value = RootBox.Text.Trim();
        if (!_loading && value != Store.Current.ProjectsRoot)
            Store.Update(s => s.ProjectsRoot = value);
        UpdateStatus();
    }

    void OnBrowseRoot(object sender, RoutedEventArgs e)
    {
        string current = Store.Current.ResolveProjectsRoot();
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) == true)
        {
            RootBox.Text = dialog.FolderName;
            CommitRoot();
        }
    }

    void OnHoldChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoldValue is null)
            return; // during InitializeComponent
        HoldValue.Text = Loc.Format("Settings.Ms", (int)HoldSlider.Value);
        if (_loading)
            return;
        _holdDebounce.Stop();
        _holdDebounce.Start();
    }

    void CommitHold()
    {
        _holdDebounce.Stop();
        int value = (int)HoldSlider.Value;
        if (value != Store.Current.HoldMs)
            Store.Update(s => s.HoldMs = value);
    }

    void OnHotkeyKeyDown(object sender, KeyEventArgs e) => ReadHotkey(e, HotkeyStatus, SaveHotkey);

    void OnClearHotkey(object sender, RoutedEventArgs e) => SaveHotkey("");

    void OnAgentsHotkeyKeyDown(object sender, KeyEventArgs e) => ReadHotkey(e, AgentsHotkeyStatus, SaveAgentsHotkey);

    void OnClearAgentsHotkey(object sender, RoutedEventArgs e) => SaveAgentsHotkey("");

    /// <summary>Turns a key press in a shortcut box into a shortcut; Backspace or Delete turns it off.</summary>
    void ReadHotkey(KeyEventArgs e, TextBlock status, Action<string> save)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.Escape)
            return; // keep keyboard navigation and Esc-to-close
        e.Handled = true;
        if (key is Key.Back or Key.Delete)
        {
            save("");
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return; // wait for the non-modifier key
        }
        var binding = new HotkeyBinding(Keyboard.Modifiers, key);
        if (!binding.IsUsable)
        {
            SetStatus(status, Loc.Get("Settings.Hotkey.NeedsModifier"));
            return;
        }
        save(binding.ToString());
    }

    void SaveAgentsHotkey(string value)
    {
        if (value != Store.Current.ActiveAgents.Hotkey)
            Store.Update(s => s.ActiveAgents.Hotkey = value);
        AgentsHotkeyBox.Text = string.IsNullOrEmpty(value) ? Loc.Get("Settings.Hotkey.Off") : value;
        UpdateStatus();
    }

    void SaveHotkey(string value)
    {
        if (value != Store.Current.Hotkey)
            Store.Update(s => s.Hotkey = value);
        HotkeyBox.Text = string.IsNullOrEmpty(value) ? Loc.Get("Settings.Hotkey.Off") : value;
        UpdateStatus();
    }

    void OnCommandCommitted(object sender, RoutedEventArgs e)
    {
        string value = CommandBox.Text.Trim();
        if (!_loading && value != DefaultAgent(Store.Current)?.Command)
            Store.Update(s => { if (DefaultAgent(s) is { } agent) agent.Command = value; });
        UpdateStatus();
    }

    void OnTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (sender == RootBox)
            CommitRoot();
        else if (sender == AccentFromBox || sender == AccentToBox)
            CommitCustomAccent();
        else
            OnCommandCommitted(sender, e);
        e.Handled = true;
    }

    void OnTerminalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && Enum.TryParse(SelectedTag(TerminalBox), out TerminalKind terminal))
        {
            Store.Update(s => s.Terminal = terminal);
            UpdateStatus();
        }
    }

    void OnShellChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && Enum.TryParse(SelectedTag(ShellBox), out ShellKind shell))
        {
            Store.Update(s => s.Shell = shell);
            UpdateStatus();
        }
    }

    void OnAutostartClicked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _host.Autostart.Set(AutostartBox.IsChecked == true);
        RefreshAutostart();
    }

    void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        bool value = CheckUpdatesBox.IsChecked == true;
        if (!_loading)
            Store.Update(s => s.Updates.Check = value);
    }

    async void OnCheckNow(object sender, RoutedEventArgs e)
    {
        if (_host.Updates is { Status: UpdateCheck.Available, Latest: { } release })
            _host.OpenUpdateWindow(release);
        else
            await _host.CheckForUpdatesAsync();
    }

    void OnDebugClicked(object sender, RoutedEventArgs e)
    {
        bool value = DebugBox.IsChecked == true;
        if (!_loading)
            Store.Update(s => s.DebugLogging = value);
    }

    void OnActiveAgentsClicked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        bool follow = FollowAgentsBox.IsChecked == true, asks = NotifyAsksBox.IsChecked == true, finished = NotifyFinishedBox.IsChecked == true;
        AgentNotificationsPanel.IsEnabled = follow;
        Store.Update(s =>
        {
            s.ActiveAgents.Follow = follow;
            s.ActiveAgents.NotifyAsks = asks;
            s.ActiveAgents.NotifyFinished = finished;
        });
    }

    void OnNewProjectCardClicked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        bool prompt = CardPromptBox.IsChecked == true, git = CardGitBox.IsChecked == true;
        bool plan = CardPlanBox.IsChecked == true, templates = CardTemplatesBox.IsChecked == true;
        Store.Update(s =>
        {
            s.NewProjectCard.Prompt = prompt;
            s.NewProjectCard.Git = git;
            s.NewProjectCard.Plan = plan;
            s.NewProjectCard.Templates = templates;
        });
    }

    void OnOpenTemplates(object sender, RoutedEventArgs e) => _host.OpenTemplatesFolder();

    void OnEditJson(object sender, RoutedEventArgs e) => _host.OpenSettingsFile();

    void OnOpenLogs(object sender, RoutedEventArgs e) => _host.OpenLogFolder();

    void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---- helpers ----

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
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
