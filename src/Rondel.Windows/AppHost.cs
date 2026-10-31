using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Rondel.Core;
using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Platform;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;
using Rondel.Windows.Detection;
using Rondel.Windows.Hook;
using Rondel.Windows.Interop;
using Rondel.Windows.Launch;
using Rondel.Windows.UI;
using Autostart = Rondel.Windows.Settings.Autostart;

namespace Rondel.Windows;

/// <summary>
/// Composition root: wires the hook thread, the detection thread and the UI thread together.
/// IGestureSink callbacks arrive on the hook thread and are only forwarded from here.
/// </summary>
internal sealed class AppHost : IGestureSink, IDisposable
{
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly TrayHost _tray;
    readonly RadialMenuWindow _menu;
    readonly DetectionWorker _worker;
    readonly IContextDetector _detector;
    readonly MouseHook _hook;
    readonly ITerminalLauncher _launcher = new WindowsTerminalLauncher();
    SettingsWindow? _settingsWindow;

    public AppHost()
    {
        Store = new SettingsStore(new DispatcherSynchronizationContext(_ui));
        Store.Load();
        var settings = Store.Current;
        Log.DebugEnabled = settings.DebugLogging;
        Loc.SetLanguage(settings.Language);

        _tray = new TrayHost(BuildTrayMenu);
        _menu = new RadialMenuWindow();
        _worker = new DetectionWorker();
        _detector = new ShellContextDetector(() => Store.Current);
        _hook = new MouseHook(this, settings.HoldMs, settings.Paused);
    }

    public SettingsStore Store { get; }

    public IAutostart Autostart { get; } = new Autostart();

    /// <summary>Why the configured hotkey is not active, or null.</summary>
    public string? HotkeyError { get; private set; }

    public void Start()
    {
        Store.Changed += (settings, external) =>
        {
            ApplySettings(settings);
            if (external)
                _settingsWindow?.LoadValues();
        };
        Store.LoadFailed += _ => _tray.ShowBalloon(AppInfo.Name, Loc.Get("Tray.SettingsInvalid"), warning: true);

        _menu.ItemInvoked += OnItemInvoked;
        _menu.MenuClosed += _hook.NotifyMenuClosed;
        _menu.Prepare();

        _tray.Activated += OpenSettings;
        _tray.HotkeyPressed += OnHotkey;
        _tray.ThemeChanged += () =>
        {
            var theme = SystemTheme.Read();
            _menu.UpdateTheme(theme);
            _settingsWindow?.ApplyBrand(theme);
        };
        _tray.Show(Store.Current.Paused, TrayTooltip(Store.Current));

        _hook.Start();
        if (!_hook.IsInstalled)
            _tray.ShowBalloon(AppInfo.Name, Loc.Get("Tray.HookFailed"), warning: true);

        ApplySettings(Store.Current);
        if (Store.LoadError is not null)
            _tray.ShowBalloon(AppInfo.Name, Loc.Get("Tray.SettingsInvalid"), warning: true);

        Log.Info($"{AppInfo.Name} {AppInfo.Version} started (Windows {Environment.OSVersion.Version}, UI culture {System.Globalization.CultureInfo.CurrentUICulture.Name}, language {Loc.Language})");
    }

    /// <summary>Another copy of the app was started: show the settings instead.</summary>
    public void OnSecondInstance() => OpenSettings();

    /// <summary>
    /// Developer aid: "--show-menu" opens the Explorer menu for the projects folder at the cursor without
    /// the mouse gesture; "--show-menu input [text]" continues into the New + Prompt card, optionally with a name typed.
    /// </summary>
    public void ShowMenuAtCursor(bool input, string? text)
    {
        string root = Store.Current.ResolveProjectsRoot();
        var context = new TriggerContext(ContextKind.ExplorerEmpty, root, Path.GetFileName(root), "show-menu");
        ShowMenu(context, Native.GetCursorPosition());
        if (input && MenuBuilder.Build(context, Store.Current) is { } model
            && model.Items.FirstOrDefault(i => i.Action.Input == ActionInput.FolderNameAndPrompt) is { } item)
        {
            OnItemInvoked(model, item);
            if (!string.IsNullOrEmpty(text))
                _menu.NameBox.Text = text;
        }
    }

    void ApplySettings(AppSettings settings)
    {
        Log.DebugEnabled = settings.DebugLogging;
        Loc.SetLanguage(settings.Language);
        _hook.Configure(settings.HoldMs, settings.Paused);
        _tray.Update(settings.Paused, TrayTooltip(settings));
        ApplyHotkey(settings.Hotkey);
    }

    void ApplyHotkey(string text)
    {
        HotkeyError = null;
        if (!HotkeyBinding.TryParse(text, out var binding))
        {
            _tray.ClearHotkey();
            if (!string.IsNullOrWhiteSpace(text))
                Log.Warn($"Hotkey '{text}' could not be parsed");
            return;
        }
        if (!_tray.SetHotkey(binding.NativeModifiers, binding.VirtualKey))
            HotkeyError = Loc.Get("Settings.Hotkey.Failed");
        else
            Log.Info($"Hotkey {binding} registered");
    }

    static string TrayTooltip(AppSettings settings) =>
        Loc.Get(settings.Paused ? "Tray.TooltipPaused" : "Tray.Tooltip");

    // ---- hook thread → detection thread → UI thread ----

    void IGestureSink.OnGestureStarted(Gesture gesture)
    {
        Log.Debug($"Gesture #{gesture.Id}: Ctrl+press at {gesture.Point}, window 0x{gesture.HitWindow:X} in 0x{gesture.RootWindow:X}");
        _worker.Post(() => _hook.ReportDetection(gesture.Id, Detect(gesture.Point, gesture.RootWindow, $"#{gesture.Id}")));
    }

    void IGestureSink.OnShowMenu(Gesture gesture, TriggerContext context) =>
        _ui.BeginInvoke(() => ShowMenu(context, gesture.Point));

    void IGestureSink.OnHeldRelease(POINT point) =>
        _ui.BeginInvoke(() => _menu.OnHeldRelease(point));

    void IGestureSink.OnOutsideClick() =>
        _ui.BeginInvoke(() => _menu.CloseMenu(restoreFocus: true));

    void IGestureSink.OnGestureCancelled(Gesture gesture, string reason) =>
        Log.Debug($"Gesture #{gesture.Id}: passed through ({reason})");

    /// <summary>Runs on the detection thread.</summary>
    TriggerContext Detect(POINT point, nint rootWindow, string tag)
    {
        var watch = Stopwatch.StartNew();
        TriggerContext context;
        try
        {
            context = _detector.Detect(point, rootWindow);
        }
        catch (Exception ex)
        {
            Log.Error($"Context detection {tag} failed", ex);
            context = TriggerContext.None($"exception {ex.GetType().Name}");
        }
        if (Log.DebugEnabled)
        {
            Log.Debug($"Detection {tag}: {context.Kind}, dir '{context.TargetDir}', {context.Reason} ({watch.ElapsedMilliseconds} ms)"
                + string.Concat(context.Trace.Select(line => Environment.NewLine + "    " + line)));
        }
        return context;
    }

    void ShowMenu(TriggerContext context, POINT anchor)
    {
        try
        {
            var model = MenuBuilder.Build(context, Store.Current);
            if (model.Items.Count == 0)
            {
                Log.Warn($"No actions configured for {context.Kind}; check Menus in settings.json");
                _hook.NotifyMenuClosed();
                return;
            }
            _menu.ShowMenu(model, anchor);
            _hook.NotifyMenuOpened(_menu.Handle);
            RequestSessionHints(model);
        }
        catch (Exception ex)
        {
            Log.Error("Showing the menu failed", ex);
            _hook.NotifyMenuClosed();
        }
    }

    /// <summary>What one session store knows about the menu's folder.</summary>
    sealed record SessionDetails(SessionInfo? Info, IReadOnlyList<SessionEntry> Recent);

    /// <summary>
    /// Session hints and the recent sessions of expandable slices never delay the menu: they are
    /// looked up afterwards (one lookup per session store) and filled in.
    /// </summary>
    void RequestSessionHints(MenuModel model)
    {
        if (model.Context.TargetDir is not { } directory)
            return;
        var stores = model.Items
            .Where(i => i.Action.Hint != SessionHint.None || MenuBuilder.CanExpand(i))
            .GroupBy(i => i.Agent.Sessions ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => (Id: g.Key, Store: SessionStores.Get(g.Key), Expands: g.Any(MenuBuilder.CanExpand)))
            .Where(s => s.Store is not null)
            .ToList();
        if (stores.Count == 0)
            return;
        Task.Run(() => stores.ToDictionary(
                s => s.Id,
                s => new SessionDetails(s.Store!.Lookup(directory), s.Expands ? s.Store.Recent(directory, MenuBuilder.MaxSessionItems) : []),
                StringComparer.OrdinalIgnoreCase))
            .ContinueWith(
                lookup => _ui.BeginInvoke(() => ApplySessionDetails(model, lookup.Result)),
                TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    void ApplySessionDetails(MenuModel model, Dictionary<string, SessionDetails> detailsByStore)
    {
        SessionDetails? DetailsFor(MenuItemModel item) =>
            detailsByStore.TryGetValue(item.Agent.Sessions ?? "", out var details) ? details : null;

        var hints = model.Items.Select(item => SessionHints.Describe(item.Action.Hint, DetailsFor(item)?.Info)).ToList();
        _menu.ApplyHints(model, hints);

        var children = model.Items
            .Select(item => MenuBuilder.CanExpand(item) && DetailsFor(item) is { } details
                ? MenuBuilder.SessionItems(item, details.Recent)
                : [])
            .ToList();
        _menu.ApplyChildren(model, children);
    }

    void OnHotkey()
    {
        if (_menu.IsMenuOpen)
        {
            _menu.CloseMenu(restoreFocus: true);
            return;
        }
        if (Store.Current.Paused)
            return;

        var point = Native.GetCursorPosition();
        nint root = Native.GetAncestor(Native.WindowFromPoint(point), Native.GA_ROOT);
        _worker.Post(() =>
        {
            var context = Detect(point, root, "hotkey");
            if (context.Kind == ContextKind.None)
            {
                // Nothing useful under the cursor: offer the desktop actions for the projects folder.
                string projects = Store.Current.ResolveProjectsRoot();
                context = new TriggerContext(ContextKind.DesktopEmpty, projects, Path.GetFileName(projects), "hotkey fallback");
            }
            _ui.BeginInvoke(() => ShowMenu(context, point));
        });
    }

    void OnItemInvoked(MenuModel model, MenuItemModel item)
    {
        var action = item.Action;
        string? directory = model.Context.TargetDir;
        Log.Info($"Action '{action.Id}' ({item.Agent.Id}) for {model.Context.Kind} '{directory}'");
        if (directory is null)
        {
            _menu.CloseMenu(restoreFocus: true);
            return;
        }

        if (action.Input == ActionInput.None)
        {
            _menu.CloseMenu(restoreFocus: false);
            StartAgent(item.Agent, action, directory, prompt: null, item.SessionId);
            return;
        }

        _menu.StartInput(new InputSpec(
            item.Label,
            item.Glyph,
            directory,
            item.Agent.Name,
            AskPrompt: action.Input == ActionInput.FolderNameAndPrompt,
            ValidateName: name => FolderNameValidator.Validate(directory, name),
            Submit: (name, prompt) => CreateFolderAndStart(item.Agent, action, directory, name, prompt)));
    }

    /// <returns>An inline error message, or null when the folder exists and the agent was started.</returns>
    string? CreateFolderAndStart(AgentProfile agent, ActionDefinition action, string parent, string name, string? prompt)
    {
        if (FolderNameValidator.Validate(parent, name) is { } error)
            return error;
        string path = Path.Combine(parent, name);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"Creating '{path}' failed", ex);
            return Loc.Format("Input.CreateFailed", ex.Message);
        }
        Log.Info($"Created '{path}'");
        _menu.CloseMenu(restoreFocus: false);
        StartAgent(agent, action, path, prompt);
        return null;
    }

    void StartAgent(AgentProfile agent, ActionDefinition action, string directory, string? prompt, string? sessionId = null)
    {
        if (LaunchPlanner.Create(agent, action, directory, prompt, sessionId) is not { } plan)
        {
            Log.Warn($"Agent '{agent.Id}' does not support {action.Capability}");
            return;
        }
        try
        {
            _launcher.Launch(plan, Store.Current);
        }
        catch (Exception ex) when (ex is LaunchException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error($"Launching {agent.Name} in '{directory}' failed", ex);
            _tray.ShowBalloon(AppInfo.Name, Loc.Format("Launch.Failed", agent.Name, ex.Message), warning: true);
        }
    }

    // ---- tray / settings ----

    IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var settings = Store.Current;
        return
        [
            new(Loc.Get("Tray.Settings"), OpenSettings, IsDefault: true),
            new(Loc.Get(settings.Paused ? "Tray.Resume" : "Tray.Pause"), () => Store.Update(s => s.Paused = !s.Paused)),
            new(Loc.Get("Tray.Autostart"), () => Autostart.Set(!Autostart.IsEnabled), Checked: Autostart.IsEnabled),
            new(Loc.Get("Tray.OpenLogs"), OpenLogFolder),
            TrayMenuItem.Separator,
            new(Loc.Get("Tray.Exit"), () => Application.Current.Shutdown()),
        ];
    }

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

    public void OpenLogFolder() => ShellOpen(Log.Directory);

    public void OpenSettingsFile()
    {
        if (!ShellOpen(Store.FilePath))
            ShellOpen("notepad.exe", $"\"{Store.FilePath}\"");
    }

    static bool ShellOpen(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = arguments ?? "" })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Opening '{target}' failed", ex);
            return false;
        }
    }

    public void Dispose()
    {
        _hook.Dispose();
        _worker.Dispose();
        _tray.Dispose();
        Store.Dispose();
    }
}
