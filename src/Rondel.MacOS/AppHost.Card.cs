using Avalonia.Threading;
using Rondel.Core;
using Rondel.Core.Activity;
using Rondel.Core.Logging;
using Rondel.MacOS.UI;

namespace Rondel.MacOS;

/// <summary>The agents card: the followed agents in sight, above the windows or on the desktop.</summary>
internal sealed partial class AppHost
{
    /// <summary>Where the card was on each screen (not in settings.json).</summary>
    static string CardStateFile => Path.Combine(AppInfo.SettingsDirectory, "agents-card.json");

    AgentsCard? _card;
    FileSystemWatcher? _usageWatcher;
    DispatcherTimer? _usageRead;

    /// <summary>
    /// The start of the status line command that reports the usage limits: Rondel's own executable. Claude Code runs a
    /// status line in a POSIX shell on macOS, so a path with a space goes in single quotes.
    /// </summary>
    static readonly Lazy<string?> UsageTapCommand = new(() =>
    {
        if (Environment.ProcessPath is not { } path)
        {
            Log.Warn("The usage limits cannot be shown: Rondel's executable is not known");
            return null;
        }
        return StatusLineTap.Prefix(StatusLineTap.ShellPath(path) ?? "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'");
    });

    void StartCard()
    {
        // The card's own changes (its pin, its options) are saved like any setting, so they are back after a restart;
        // an open Settings window shows them at once.
        _card = new AgentsCard(() => Store.Current.ActiveAgents.Card, change =>
        {
            Store.Update(s => change(s.ActiveAgents.Card));
            _settingsWindow?.LoadValues();
        }, Palette(Store.Current), CardStateFile);
        _card.AgentClicked += FocusAgent;
        _card.SettingsRequested += OpenSettings;
        _card.HiddenByUser += UpdateCardMenu;
        _card.HideShortcut = CardShortcutText;
        UpdateCard();
        WatchUsage();
    }

    void UpdateCard()
    {
        var settings = Store.Current.ActiveAgents;
        // Sessions started from now on report the usage limits while the card shows them.
        _agents.UsageTap = settings.Follow && settings.Card.Show && settings.Card.ShowUsage ? UsageTapCommand.Value : null;
        _card?.Update(_agents.Agents, settings.Follow);
        UpdateCardMenu();
    }

    /// <summary>
    /// Reads usage.json now and whenever a session's status line writes it (<see cref="StatusLineTap"/>), a moment after
    /// the last write.
    /// </summary>
    void WatchUsage()
    {
        var store = UsageStore.Default;
        _usageRead = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _usageRead!.Stop();
            _card?.ShowUsage(store.Read());
        });
        _usageRead.Stop();
        _card?.ShowUsage(store.Read());
        try
        {
            string directory = Path.GetDirectoryName(store.Path)!;
            Directory.CreateDirectory(directory);
            _usageWatcher = new FileSystemWatcher(directory, UsageStore.FileName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            void Changed(object? sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(() =>
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

    /// <summary>
    /// "Show the agents card" in the menu bar menu while the card is turned on: available when it is not on screen, so a
    /// hidden card can come back.
    /// </summary>
    void UpdateCardMenu()
    {
        var settings = Store.Current.ActiveAgents;
        bool? item = _card is null || !settings.Follow || !settings.Card.Show ? null : _card.CanShowAgain;
        _menuBar.Update(s => s with { ShowCard = item });
    }
}
