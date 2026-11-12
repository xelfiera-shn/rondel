using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Rondel.Core;
using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Platform;
using Rondel.Core.Prompts;
using Rondel.Core.Settings;
using Rondel.MacOS.Detection;
using Rondel.MacOS.Interop;
using Rondel.MacOS.Launch;
using Rondel.MacOS.Settings;
using Rondel.MacOS.Trigger;
using Rondel.MacOS.UI;
using Rondel.MacOS.Updates;

namespace Rondel.MacOS;

/// <summary>
/// Composition root: wires the tap thread, the detection thread and the main thread together. IGestureSink
/// callbacks arrive on the tap thread or a timer thread and are only forwarded from here.
/// </summary>
internal sealed partial class AppHost : IGestureSink, IMenuHost, IDisposable
{
    readonly MenuBar _menuBar = new();
    readonly DetectionWorker _worker = new();
    readonly FinderDetector _detector;
    readonly EventTap _tap;
    readonly MenuFlow _flow;
    readonly QuickSweeper _quick;
    IDisposable? _accessibilityPoll;
    nint _activity;
    RadialMenuWindow? _menu;
    readonly List<AnswerCardWindow> _answerCards = [];
    SettingsWindow? _settingsWindow;
    WelcomeWindow? _welcomeWindow;
    SessionsWindow? _sessionsWindow;
    string? _look;

    /// <param name="settingsDirectory">Where settings.json is; the settings folder unless a diagnostic passes a copy.</param>
    public AppHost(string? settingsDirectory = null)
    {
        Store = new SettingsStore(new AvaloniaSynchronizationContext(), settingsDirectory);
        Store.Load();
        _detector = new FinderDetector(() => Store.Current);
        _tap = new EventTap(this);
        var launcher = new MacTerminalLauncher((message, activityId) => Dispatcher.UIThread.Post(() => LaunchFailed(message, activityId)),
            _agentTerminals);
        // Developer aids read the saved prompts next to their copy of the settings.
        Prompts = settingsDirectory is null ? SavedPrompts.Default : new SavedPrompts(Path.Combine(settingsDirectory, "Prompts"));
        _flow = new MenuFlow(this, launcher, new MacFolderNameValidator(), () => Store.Current, change => Store.Update(change),
            activity: _agents, answers: new MacAnswerLauncher(), prompts: Prompts);
        _quick = new QuickSweeper(_flow);
        Updates = new MacUpdates(Store, () => QuitRequested?.Invoke());
        ApplySettings(Store.Current);
    }

    /// <summary>The saved prompts the Ask slices offer.</summary>
    public SavedPrompts Prompts { get; }

    /// <summary>Looking for newer releases, and updating Rondel.app in place.</summary>
    public MacUpdates Updates { get; }

    public SettingsStore Store { get; }

    public Autostart Autostart { get; } = new();

    /// <summary>Whether Rondel may ask Finder for a window's folder; null until it was checked.</summary>
    public bool? HasFinderAccess { get; private set; }

    /// <summary>A permission was granted or taken back (on the main thread).</summary>
    public event Action? PermissionsChanged;

    /// <summary>"Quit Rondel" was chosen in the menu bar menu.</summary>
    public event Action? QuitRequested;

    public void Start()
    {
        Store.Changed += (settings, external) =>
        {
            ApplySettings(settings);
            if (external)
                _settingsWindow?.LoadValues();
        };
        Store.LoadFailed += _ => Warn(Loc.Get("Tray.SettingsInvalid"));
        StartShortcuts();

        _menuBar.AccessibilityClicked += AllowAccessibility;
        _menuBar.FinderAccessClicked += AllowFinderAccess;
        _menuBar.ScreenshotClicked += AskAboutScreenshot;
        _menuBar.SettingsClicked += OpenSettings;
        _menuBar.SessionsClicked += OpenSessions;
        _menuBar.HowToUseClicked += ShowWelcome;
        _menuBar.ShowCardClicked += () =>
        {
            _card?.ShowAgain();
            UpdateCardMenu();
        };
        _menuBar.PauseClicked += () => Store.Update(s => s.Paused = !s.Paused);
        _menuBar.OpenLogsClicked += OpenLogFolder;
        _menuBar.QuitClicked += () => QuitRequested?.Invoke();
        _menuBar.UpdateClicked += () =>
        {
            if (Updates.Offered is { } release)
                Updates.OpenWindow(release);
        };
        Updates.Changed += () =>
        {
            _menuBar.Update(s => s with { Update = Updates.Offered?.Version.ToString() });
            _settingsWindow?.ShowUpdateStatus();
        };
        _menuBar.Show(new MenuBarState(Store.Current.Paused, !Native.AXIsProcessTrusted(), true,
            Store.LoadError is null ? null : Loc.Get("Tray.SettingsInvalid")));

        _menu = new RadialMenuWindow(Palette(Store.Current));
        _menu.ItemInvoked += _flow.Invoke;
        _menu.MenuClosed += _tap.NotifyMenuClosed;
        _menu.FrameChanged += _tap.NotifyMenuOpened;
        _menu.Prepare();
        if (Application.Current?.PlatformSettings is { } platform)
            platform.ColorValuesChanged += (_, _) => ApplyLook(Store.Current, force: true);
        // Opening Rondel again (from Finder, Spotlight or Launchpad) while it runs opens its settings.
        if (Application.Current?.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                // A click on one of Rondel's notifications reopens it too; that click has its own window. Which of
                // the two arrives first is not fixed, so the reopen waits a moment before it looks.
                if (e.Kind == ActivationKind.Reopen)
                {
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (!Notifications.ClickedWithin(TimeSpan.FromSeconds(2)))
                            OpenSettings();
                    }, TimeSpan.FromMilliseconds(400));
                }
            };
        }

        _quick.Start();
        StartFollowingAgents();
        StartCard();
        // The event tap must answer within a fraction of a second, also after hours in the background.
        _activity = Native.BeginLatencyCriticalActivity("Rondel watches for Option + hold");
        // The first time, the welcome window says what the two permissions are for and asks for them; on later
        // starts macOS asks at once for one that is missing.
        if (Store.CreatedNew)
        {
            StartTrigger(prompt: false);
            CheckFinderAccess();
            ShowWelcome();
        }
        else
        {
            StartTrigger(prompt: true);
            AskForFinderAccess(fromMenu: false);
        }

        Log.Info($"{AppInfo.Name} {AppInfo.Version} started (macOS {Environment.OSVersion.Version}, UI culture {System.Globalization.CultureInfo.CurrentUICulture.Name}, language {Loc.Language})");
    }

    void ApplySettings(AppSettings settings)
    {
        Log.DebugEnabled = settings.DebugLogging;
        Loc.SetLanguage(settings.Language);
        _tap.Configure(settings.HoldMs, settings.Paused);
        _menuBar.Update(s => s with { Paused = settings.Paused });
        ApplyLook(settings);
        ApplyShortcuts(settings);
        UpdateCard();
    }

    /// <summary>The menu's theme, mode and accent; redrawn only when one of them changed (or macOS switched modes).</summary>
    void ApplyLook(AppSettings settings, bool force = false)
    {
        var appearance = settings.Appearance;
        string look = $"{appearance.Theme}|{appearance.Mode}|{appearance.AccentFrom}|{appearance.AccentTo}|{SystemIsDark}";
        if (_menu is null || (!force && look == _look))
            return;
        _look = look;
        var palette = Palette(settings);
        _menu.ApplyLook(palette);
        _card?.ApplyLook(palette);
        foreach (var card in _answerCards)
            card.ApplyLook(palette);
    }

    static MenuPalette Palette(AppSettings settings) => MenuPalette.For(settings.Appearance, SystemIsDark);

    static bool SystemIsDark => Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    /// <summary>The Sessions window: Claude Code's saved sessions, to see what they take and delete those not needed.</summary>
    public void OpenSessions()
    {
        if (_sessionsWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            open.Refresh();
            return;
        }
        var storage = new Core.Agents.ClaudeCode.ClaudeCodeStorage(tempDirectory: QuickSweeper.TempDirectory);
        // A Quick Conversation is not a saved session, even while Claude Code still has something of it.
        string quick = Core.Agents.ClaudeCode.ClaudeCodeSessionStore.Encode(Path.TrimEndingDirectorySeparator(_flow.QuickDirectory));
        var window = new SessionsWindow(() => [.. storage.List().Where(p => !string.Equals(p.Key, quick, StringComparison.OrdinalIgnoreCase))],
            storage.Delete, _flow.Resume, storage.CleanupPeriodDays());
        window.Closed += (_, _) => _sessionsWindow = null;
        _sessionsWindow = window;
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// The welcome window, which shows how the menu opens and asks for the permissions: by itself the first time
    /// Rondel runs, and from the menu bar (How to use) whenever asked.
    /// </summary>
    public void ShowWelcome()
    {
        if (_welcomeWindow is { } open)
        {
            open.Activate();
            return;
        }
        Log.Info(Store.CreatedNew ? "First run: showing the welcome window" : "Showing the welcome window");
        var window = new WelcomeWindow(this);
        window.SettingsRequested += OpenSettings;
        window.Closed += (_, _) => _welcomeWindow = null;
        _welcomeWindow = window;
        window.Show();
        window.Activate();
    }

    public void OpenSettingsFile() => Workspace.OpenText(Store.FilePath);

    public void OpenLogFolder() => Workspace.Open(Log.Directory);

    /// <summary>Opens the folder new projects' templates come from, creating it the first time.</summary>
    public void OpenTemplatesFolder()
    {
        string directory = Core.Projects.ProjectTemplates.Default.Directory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Creating the templates folder '{directory}' failed", ex);
            return;
        }
        Workspace.Open(directory);
    }

    /// <summary>Opens the folder of the saved prompts, creating it the first time.</summary>
    public void OpenPromptsFolder()
    {
        try
        {
            Directory.CreateDirectory(Prompts.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Creating the prompts folder '{Prompts.Directory}' failed", ex);
            return;
        }
        Workspace.Open(Prompts.Directory);
    }

    void Warn(string message)
    {
        Log.Warn(message);
        _menuBar.Update(s => s with { Warning = message });
    }

    // ---- permissions ----

    /// <summary>
    /// The tap needs the Accessibility permission. Without it macOS is asked to show its prompt once (unless
    /// <paramref name="prompt"/> is false: the welcome window asks), and a grant is picked up while Rondel runs, so
    /// no restart is needed.
    /// </summary>
    void StartTrigger(bool prompt)
    {
        if (Native.AXIsProcessTrusted())
        {
            InstallTap();
            return;
        }
        Log.Warn("No Accessibility permission yet; the trigger is off until it is granted");
        if (prompt)
            Native.RequestAccessibility();
        _accessibilityPoll = DispatcherTimer.Run(() =>
        {
            if (!Native.AXIsProcessTrusted())
                return true;
            Log.Info("Accessibility permission granted");
            InstallTap();
            PermissionsChanged?.Invoke();
            return false;
        }, TimeSpan.FromSeconds(1));
    }

    void InstallTap()
    {
        _menuBar.Update(s => s with { NeedsAccessibility = false });
        if (!_tap.Start())
            Warn(Loc.Get("Tray.HookFailed"));
    }

    public void AllowAccessibility()
    {
        Native.RequestAccessibility();
        Workspace.Open(Workspace.AccessibilitySettings);
    }

    public void AllowFinderAccess() => AskForFinderAccess(fromMenu: true);

    /// <summary>Checks the Automation permission for Finder again without asking (it can be changed in System Settings).</summary>
    public void CheckFinderAccess() => Task.Run(() => ShowFinderAccess(Native.AutomationPermission(Workspace.FinderId, ask: false)));

    void ShowFinderAccess(int status) => Dispatcher.UIThread.Post(() =>
    {
        bool granted = status == Native.AutomationAllowed;
        _menuBar.Update(s => s with { NeedsFinderAccess = !granted });
        if (HasFinderAccess == granted)
            return;
        HasFinderAccess = granted;
        PermissionsChanged?.Invoke();
    });

    /// <summary>
    /// The Automation permission for Finder, needed for the empty area of a Finder window. Asked at start and from
    /// the menu, never during a gesture. The question waits for the user's answer, so it runs off the main thread.
    /// </summary>
    void AskForFinderAccess(bool fromMenu)
    {
        Task.Run(() =>
        {
            int status = Native.AutomationPermission(Workspace.FinderId, ask: false);
            if (status == Native.AutomationNotAsked)
                status = Native.AutomationPermission(Workspace.FinderId, ask: true);
            else if (status == Native.AutomationDenied && fromMenu)
                Workspace.Open(Workspace.AutomationSettings); // macOS asks only once; after a no, only the setting changes it
            Log.Info($"Automation permission for Finder: {(status == Native.AutomationAllowed ? "granted" : status.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
            ShowFinderAccess(status);
        });
    }

    // ---- tap → detection thread → main thread ----

    void IGestureSink.OnGestureStarted(Gesture gesture)
    {
        Log.Debug($"Gesture #{gesture.Id}: Option+press at {gesture.Point}");
        _worker.Post(() => _tap.ReportDetection(gesture.Id, Detect(gesture.Point, $"#{gesture.Id}")));
    }

    void IGestureSink.OnShowMenu(Gesture gesture, TriggerContext context) =>
        Dispatcher.UIThread.Post(() => ShowMenu(context, gesture.Point));

    void IGestureSink.OnHeldMove(CGPoint point) =>
        Dispatcher.UIThread.Post(() => _menu?.OnHeldMove(point));

    void IGestureSink.OnHeldRelease(CGPoint point) =>
        Dispatcher.UIThread.Post(() => _menu?.OnHeldRelease(point));

    void IGestureSink.OnOutsideClick() =>
        Dispatcher.UIThread.Post(() => _menu?.CloseMenu(restoreFocus: true));

    void IGestureSink.OnGestureCancelled(Gesture gesture, string reason) =>
        Log.Debug($"Gesture #{gesture.Id}: given back ({reason})");

    /// <summary>On the detection thread.</summary>
    TriggerContext Detect(CGPoint point, string tag)
    {
        var watch = Stopwatch.StartNew();
        var context = _detector.Detect(point);
        if (Log.DebugEnabled)
        {
            Log.Debug($"Detection {tag}: {context.Kind}, dir '{context.TargetDir}', {context.Reason} ({watch.ElapsedMilliseconds} ms)"
                + string.Concat(context.Trace.Select(line => Environment.NewLine + "    " + line)));
        }
        return context;
    }

    void ShowMenu(TriggerContext context, CGPoint anchor)
    {
        if (_menu is null)
            return;
        if (_flow.Build(context) is not { Items.Count: > 0 } model)
            return;
        try
        {
            _tap.NotifyMenuOpened(_menu.ShowMenu(model, anchor));
            RequestDetails(model);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Log.Error("Showing the menu failed", ex);
            _tap.NotifyMenuClosed();
        }
    }

    /// <summary>
    /// Session hints, a Resume slice's sessions, the recent projects, the active agents and the saved prompts pinned to
    /// the Ask slices are filled in when they arrive.
    /// </summary>
    void RequestDetails(MenuModel model)
    {
        if (!MenuDetails.Needed(model))
            return;
        var agents = _agents.Agents;
        Task.Run(() => MenuDetails.Load(model, agents, Prompts)).ContinueWith(
            lookup => Dispatcher.UIThread.Post(() =>
            {
                _menu?.ApplyHints(model, lookup.Result.Hints, lookup.Result.States);
                _menu?.ApplyChildren(model, lookup.Result.Children);
            }),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    // ---- IMenuHost (main thread) ----

    void IMenuHost.CloseMenu(bool restoreFocus) => _menu?.CloseMenu(restoreFocus);

    void IMenuHost.StartInput(InputSpec spec) => _menu?.StartInput(spec);

    void IMenuHost.ShowCard(InputSpec spec)
    {
        if (_menu is null)
            return;
        _tap.NotifyMenuOpened(_menu.ShowCard(spec, Native.CursorLocation()));
    }

    /// <summary>
    /// Quick Conversation's first answer, on a card of its own: the prompt card closes and gives the focus back, and the
    /// answer card floats where it was until it is closed or continues in a terminal, so a slow answer blocks nothing.
    /// </summary>
    void IMenuHost.ShowAnswer(QuickAnswer answer)
    {
        var spec = _menu?.CurrentInput;
        var (card, work) = _menu?.CardPlace() ?? default;
        var header = new AnswerHeader(spec?.Title ?? Loc.Get("Action.Quick"), spec?.Glyph ?? '\uE945', spec?.Location, spec?.Image,
            spec?.AgentName ?? "");
        _menu?.CloseMenu(restoreFocus: true);
        var window = new AnswerCardWindow(answer, header, Palette(Store.Current));
        window.ContinuedInTerminal += _quick.Continued;
        window.Closed += (_, _) => _answerCards.Remove(window);
        _answerCards.Add(window);
        window.ShowAt(card, work, offset: _answerCards.Count - 1);
    }

    void IMenuHost.StartNewProject(NewProjectSpec spec) => _menu?.StartNewProject(spec);

    void IMenuHost.ShowWarning(string message) => Warn(message);

    void IMenuHost.FocusAgent(string id) => FocusAgent(id);

    public void Dispose()
    {
        CancelCapture();
        _accessibilityPoll?.Dispose();
        foreach (var card in _answerCards.ToList())
            card.Close();
        StopFollowingAgents();
        _card?.Dispose();
        _quick.Dispose();
        Updates.Dispose();
        _worker.Dispose();
        _menuBar.Dispose();
        Store.Dispose();
    }
}
