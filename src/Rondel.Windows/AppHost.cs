using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Rondel.Core;
using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Platform;
using Rondel.Core.Settings;
using Rondel.Windows.Detection;
using Rondel.Windows.Hook;
using Rondel.Windows.Interop;
using Rondel.Windows.Launch;
using Rondel.Windows.UI;
using Autostart = Rondel.Windows.Settings.Autostart;

namespace Rondel.Windows;

/// <summary>
/// Composition root: wires the hook thread, the detection thread and the UI thread together, and connects
/// them to the shared <see cref="MenuFlow"/>. IGestureSink callbacks arrive on the hook thread and are only
/// forwarded from here.
/// </summary>
internal sealed class AppHost : IGestureSink, IMenuHost, IDisposable
{
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly TrayHost _tray;
    readonly RadialMenuWindow _menu;
    readonly DetectionWorker _worker;
    readonly IContextDetector _detector;
    readonly MouseHook _hook;
    readonly MenuFlow _flow;
    SettingsWindow? _settingsWindow;

    /// <param name="settingsDirectory">Folder of settings.json; the real one when null (developer aids pass a copy).</param>
    public AppHost(string? settingsDirectory = null)
    {
        Store = new SettingsStore(new DispatcherSynchronizationContext(_ui), settingsDirectory);
        Store.Load();
        var settings = Store.Current;
        Log.DebugEnabled = settings.DebugLogging;
        Loc.SetLanguage(settings.Language);

        _tray = new TrayHost(BuildTrayMenu);
        _menu = new RadialMenuWindow();
        _worker = new DetectionWorker();
        _detector = new ShellContextDetector(() => Store.Current);
        _hook = new MouseHook(this, settings.HoldMs, settings.Paused);
        _flow = new MenuFlow(this, new WindowsTerminalLauncher(), new FolderNameValidator(), () => Store.Current, change => Store.Update(change));
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

        _menu.ItemInvoked += _flow.Invoke;
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
        var context = _flow.ProjectsContext(ContextKind.ExplorerEmpty, "show-menu");
        ShowMenu(context, Native.GetCursorPosition());
        if (input && _flow.Build(context) is { } model
            && model.Items.FirstOrDefault(i => i.Action.Input == ActionInput.FolderNameAndPrompt) is { } item)
        {
            _flow.Invoke(model, item);
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
            if (_flow.Build(context) is not { } model)
            {
                _hook.NotifyMenuClosed();
                return;
            }
            _menu.ShowMenu(model, anchor);
            _hook.NotifyMenuOpened(_menu.Handle);
            RequestDetails(model);
        }
        catch (Exception ex)
        {
            Log.Error("Showing the menu failed", ex);
            _hook.NotifyMenuClosed();
        }
    }

    /// <summary>Session hints, a Resume slice's sessions and the recent projects are filled in when they arrive.</summary>
    void RequestDetails(MenuModel model)
    {
        if (!MenuDetails.Needed(model))
            return;
        Task.Run(() => MenuDetails.Load(model))
            .ContinueWith(
                lookup => _ui.BeginInvoke(() =>
                {
                    _menu.ApplyHints(model, lookup.Result.Hints);
                    _menu.ApplyChildren(model, lookup.Result.Children);
                }),
                TaskContinuationOptions.OnlyOnRanToCompletion);
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
            // Nothing useful under the cursor: offer the desktop actions for the projects folder.
            if (context.Kind == ContextKind.None)
                context = _flow.ProjectsContext(ContextKind.DesktopEmpty, "hotkey fallback");
            _ui.BeginInvoke(() => ShowMenu(context, point));
        });
    }

    // ---- IMenuHost (UI thread) ----

    void IMenuHost.CloseMenu(bool restoreFocus) => _menu.CloseMenu(restoreFocus);

    void IMenuHost.StartInput(InputSpec spec) => _menu.StartInput(spec);

    void IMenuHost.StartNewProject(NewProjectSpec spec) => _menu.StartNewProject(spec);

    void IMenuHost.ShowWarning(string message) => _tray.ShowBalloon(AppInfo.Name, message, warning: true);

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

    /// <summary>Opens the folder new projects' templates come from, creating it the first time.</summary>
    public void OpenTemplatesFolder()
    {
        string directory = Rondel.Core.Projects.ProjectTemplates.Default.Directory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Creating the templates folder '{directory}' failed", ex);
        }
        ShellOpen(directory);
    }

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
