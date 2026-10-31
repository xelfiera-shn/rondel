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
using Rondel.Core.Settings;
using Rondel.Windows.Launch;

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
        };
        Activated += (_, _) => RefreshAutostart();
        LoadValues();
    }

    SettingsStore Store => _host.Store;

    /// <summary>
    /// Rondel's teal instead of the Windows accent for checked boxes, the slider, selections and focus,
    /// by overriding the Fluent theme's accent brushes for this window, plus the header's brand line.
    /// </summary>
    internal void ApplyBrand(ThemeColors theme)
    {
        var palette = RondelPalette.For(theme);
        // The Fluent theme derives its accent brushes from these shades (lighter ones on dark surfaces).
        string[] shades = ["SystemAccentColorLight3", "SystemAccentColorLight2", "SystemAccentColorLight1", "SystemAccentColor",
            "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];
        Color[] teal = [Color.FromRgb(0x99, 0xF6, 0xE4), Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0D, 0x94, 0x88),
            Color.FromRgb(0x0F, 0x76, 0x6E), Color.FromRgb(0x11, 0x5E, 0x59), Color.FromRgb(0x13, 0x4E, 0x4A)];
        for (int i = 0; i < shades.Length; i++)
            Resources[shades[i]] = teal[i];
        // Controls such as the slider use the SystemColors accent keys directly.
        (ResourceKey Color, ResourceKey Brush)[] systemKeys =
        [
            (SystemColors.AccentColorLight3Key, SystemColors.AccentColorLight3BrushKey),
            (SystemColors.AccentColorLight2Key, SystemColors.AccentColorLight2BrushKey),
            (SystemColors.AccentColorLight1Key, SystemColors.AccentColorLight1BrushKey),
            (SystemColors.AccentColorKey, SystemColors.AccentColorBrushKey),
            (SystemColors.AccentColorDark1Key, SystemColors.AccentColorDark1BrushKey),
            (SystemColors.AccentColorDark2Key, SystemColors.AccentColorDark2BrushKey),
            (SystemColors.AccentColorDark3Key, SystemColors.AccentColorDark3BrushKey),
        ];
        for (int i = 0; i < systemKeys.Length; i++)
        {
            Resources[systemKeys[i].Color] = teal[i];
            Resources[systemKeys[i].Brush] = Solid(teal[i], 1);
        }
        var accent = theme.IsDark ? teal[1] : teal[4];
        Resources["AccentFillColorPrimaryBrush"] = Solid(accent, 1);
        Resources["AccentFillColorDefaultBrush"] = Solid(accent, 1);
        Resources["AccentFillColorSecondaryBrush"] = Solid(accent, 0.9);
        Resources["AccentFillColorTertiaryBrush"] = Solid(accent, 0.8);
        Resources["AccentTextFillColorPrimaryBrush"] = Solid(accent, 1);
        Resources["AccentTextFillColorSecondaryBrush"] = Solid(accent, 1);
        Resources["AccentTextFillColorTertiaryBrush"] = Solid(accent, 1);
        // Text on the accent: dark on the bright teal of the dark theme, white on the deep teal of the light theme.
        Resources["TextOnAccentFillColorPrimaryBrush"] = Solid(theme.IsDark ? Color.FromRgb(0x0B, 0x10, 0x14) : Colors.White, 1);
        var line = new LinearGradientBrush(palette.BrandFrom, palette.BrandTo, 0);
        line.Freeze();
        BrandLine.Background = line;
    }

    static SolidColorBrush Solid(Color color, double opacity)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
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
            CommandLabel.Text = Loc.Format("Settings.Command", DefaultAgent(s)?.Name ?? "");
            CommandBox.Text = DefaultAgent(s)?.Command ?? "";
            Select(TerminalBox, s.Terminal.ToString());
            Select(ShellBox, s.Shell.ToString());
            DebugBox.IsChecked = s.DebugLogging;
            AutostartBox.IsChecked = _host.Autostart.IsEnabled;
            VersionText.Text = Loc.Format("Settings.Version", AppInfo.Version);
        }
        finally
        {
            _loading = false;
        }
        UpdateStatus();
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

    void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.Escape)
            return; // keep keyboard navigation and Esc-to-close
        e.Handled = true;
        if (key is Key.Back or Key.Delete)
        {
            SaveHotkey("");
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
            SetStatus(HotkeyStatus, Loc.Get("Settings.Hotkey.NeedsModifier"));
            return;
        }
        SaveHotkey(binding.ToString());
    }

    void OnClearHotkey(object sender, RoutedEventArgs e) => SaveHotkey("");

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

    void OnDebugClicked(object sender, RoutedEventArgs e)
    {
        bool value = DebugBox.IsChecked == true;
        if (!_loading)
            Store.Update(s => s.DebugLogging = value);
    }

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
