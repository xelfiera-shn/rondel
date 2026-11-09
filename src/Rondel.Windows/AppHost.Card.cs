using System.Windows.Threading;
using Rondel.Core;
using Rondel.Core.Activity;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Settings;
using Rondel.Windows.Interop;
using Rondel.Windows.UI;

namespace Rondel.Windows;

/// <summary>The agents card: the followed agents in sight, on top of the windows or on the desktop.</summary>
internal sealed partial class AppHost
{
    /// <summary>%LOCALAPPDATA%\Rondel\agents-card.json: where the card was on each monitor (not in settings.json).</summary>
    static string CardStateFile => Path.Combine(Path.GetDirectoryName(AppInfo.LogDirectory)!, "agents-card.json");

    AgentsCard? _card;
    FileSystemWatcher? _usageWatcher;
    DispatcherTimer? _usageRead;

    /// <summary>
    /// The start of the status line command that reports the usage limits: Rondel's own path as a shell runs it; null
    /// when the path has characters no unquoted form avoids (<see cref="StatusLineTap.ShellPath"/>).
    /// </summary>
    static readonly Lazy<string?> UsageTapCommand = new(() =>
    {
        if (Environment.ProcessPath is not { } path || StatusLineTap.ShellPath(path, Native.ShortPath) is not { } shellPath)
        {
            Log.Warn($"The usage limits cannot be shown: no status line command runs '{Environment.ProcessPath}' (a space without a short name)");
            return null;
        }
        return StatusLineTap.Prefix(shellPath);
    });

    void StartCard()
    {
        // The card's own changes (its pin, its quick settings) are saved like any setting, so they are back after a
        // restart; an open Settings window shows them at once.
        _card = new AgentsCard(() => Store.Current.ActiveAgents.Card, change =>
        {
            Store.Update(s => change(s.ActiveAgents.Card));
            _settingsWindow?.LoadValues();
        }, CardStateFile);
        _card.AgentClicked += FocusAgent;
        _card.SettingsRequested += OpenSettings;
        _card.ApplyLook(RondelPalette.For(Store.Current.Appearance, SystemTheme.Read()));
        WatchUsage();
    }

    /// <summary>
    /// Reads usage.json now and whenever a session's status line writes it (<see cref="StatusLineTap"/>), a moment
    /// after the last write.
    /// </summary>
    void WatchUsage()
    {
        var store = UsageStore.Default;
        _usageRead = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _usageRead!.Stop();
            _card?.ShowUsage(store.Read());
        }, _ui);
        _usageRead.Stop();
        _card?.ShowUsage(store.Read());
        try
        {
            string directory = Path.GetDirectoryName(store.Path)!;
            Directory.CreateDirectory(directory);
            _usageWatcher = new FileSystemWatcher(directory, UsageStore.FileName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            void Changed(object? sender, FileSystemEventArgs e) => _ui.BeginInvoke(() =>
            {
                _usageRead.Stop();
                _usageRead.Start();
            });
            _usageWatcher.Changed += Changed;
            _usageWatcher.Created += Changed;
            _usageWatcher.Renamed += (sender, e) => Changed(sender, e);
            _usageWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Watching {store.Path} failed; the card shows the usage limits read at start only", ex);
        }
    }

    void UpdateCard() => _card?.Update(_activity.Agents, Store.Current.ActiveAgents.Follow);

    /// <summary>
    /// "Show the agents card" while the card is turned on: available when it is not on screen, so a card hidden
    /// without a shortcut can come back.
    /// </summary>
    IEnumerable<TrayMenuItem> CardTrayItems(AppSettings settings)
    {
        if (_card is null || !settings.ActiveAgents.Follow || !settings.ActiveAgents.Card.Show)
            yield break;
        yield return new TrayMenuItem(Loc.Get("Tray.ShowCard"), _card.ShowAgain, Enabled: _card.CanShowAgain);
    }

    void ApplyCard(AppSettings settings)
    {
        var card = settings.ActiveAgents.Card;
        ApplyCardHotkey(card);
        // Sessions started from now on report the usage limits while the card shows them.
        _activity.UsageTap = settings.ActiveAgents.Follow && card.Show && card.ShowUsage ? UsageTapCommand.Value : null;
        if (_card is null)
            return;
        _card.HideShortcut = card.Show && CardHotkeyError is null && !string.IsNullOrEmpty(card.Hotkey) ? card.Hotkey : null;
        _card.Update(_activity.Agents, settings.ActiveAgents.Follow);
    }

    /// <summary>The card's shortcut, registered only while the card is turned on.</summary>
    void ApplyCardHotkey(AgentsCardOptions card)
    {
        CardHotkeyError = null;
        if (!card.Show || !HotkeyBinding.TryParse(card.Hotkey, out var binding))
        {
            _tray.ClearCardHotkey();
            if (card.Show && !string.IsNullOrWhiteSpace(card.Hotkey))
                Log.Warn($"Agents card shortcut '{card.Hotkey}' could not be parsed");
            return;
        }
        if (!_tray.SetCardHotkey(binding.NativeModifiers, binding.VirtualKey))
            CardHotkeyError = Loc.Get("Settings.Hotkey.Failed");
        else
            Log.Info($"Agents card shortcut {binding} registered");
    }
}
