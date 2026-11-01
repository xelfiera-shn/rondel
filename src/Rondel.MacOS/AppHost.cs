using System.Diagnostics;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using Rondel.Core;
using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Settings;
using Rondel.MacOS.Detection;
using Rondel.MacOS.Interop;
using Rondel.MacOS.Trigger;
using Rondel.MacOS.UI;

namespace Rondel.MacOS;

/// <summary>
/// Composition root: wires the tap thread, the detection thread and the main thread together. IGestureSink
/// callbacks arrive on the tap thread or a timer thread and are only forwarded from here.
/// </summary>
/// <remarks>
/// Launching comes with the next step: until then a chosen item is logged and the menu closes.
/// </remarks>
internal sealed class AppHost : IGestureSink, IDisposable
{
    readonly MenuBar _menuBar = new();
    readonly DetectionWorker _worker = new();
    readonly FinderDetector _detector;
    readonly EventTap _tap;
    IDisposable? _accessibilityPoll;
    nint _activity;
    RadialMenuWindow? _menu;
    string? _look;

    public AppHost()
    {
        Store = new SettingsStore(new AvaloniaSynchronizationContext());
        Store.Load();
        _detector = new FinderDetector(() => Store.Current);
        _tap = new EventTap(this);
        ApplySettings(Store.Current);
    }

    public SettingsStore Store { get; }

    /// <summary>"Quit Rondel" was chosen in the menu bar menu.</summary>
    public event Action? QuitRequested;

    public void Start()
    {
        Store.Changed += (settings, _) => ApplySettings(settings);
        Store.LoadFailed += _ => Warn(Loc.Get("Tray.SettingsInvalid"));

        _menuBar.AccessibilityClicked += () =>
        {
            Native.RequestAccessibility();
            Workspace.Open(Workspace.AccessibilitySettings);
        };
        _menuBar.FinderAccessClicked += () => AskForFinderAccess(fromMenu: true);
        _menuBar.PauseClicked += () => Store.Update(s => s.Paused = !s.Paused);
        _menuBar.EditSettingsClicked += () => Workspace.Open(Store.FilePath, "TextEdit");
        _menuBar.OpenLogsClicked += () => Workspace.Open(Log.Directory);
        _menuBar.QuitClicked += () => QuitRequested?.Invoke();
        _menuBar.Show(new MenuBarState(Store.Current.Paused, !Native.AXIsProcessTrusted(), true,
            Store.LoadError is null ? null : Loc.Get("Tray.SettingsInvalid")));

        _menu = new RadialMenuWindow(Palette(Store.Current));
        _menu.ItemInvoked += OnItemInvoked;
        _menu.MenuClosed += _tap.NotifyMenuClosed;
        _menu.Prepare();
        if (Application.Current?.PlatformSettings is { } platform)
            platform.ColorValuesChanged += (_, _) => ApplyLook(Store.Current, force: true);

        // The event tap must answer within a fraction of a second, also after hours in the background.
        _activity = Native.BeginLatencyCriticalActivity("Rondel watches for Option + hold");
        StartTrigger();
        AskForFinderAccess(fromMenu: false);

        Log.Info($"{AppInfo.Name} {AppInfo.Version} started (macOS {Environment.OSVersion.Version}, UI culture {System.Globalization.CultureInfo.CurrentUICulture.Name}, language {Loc.Language})");
    }

    void ApplySettings(AppSettings settings)
    {
        Log.DebugEnabled = settings.DebugLogging;
        Loc.SetLanguage(settings.Language);
        _tap.Configure(settings.HoldMs, settings.Paused);
        _menuBar.Update(s => s with { Paused = settings.Paused });
        ApplyLook(settings);
    }

    /// <summary>The menu's theme, mode and accent; redrawn only when one of them changed (or macOS switched modes).</summary>
    void ApplyLook(AppSettings settings, bool force = false)
    {
        var appearance = settings.Appearance;
        string look = $"{appearance.Theme}|{appearance.Mode}|{appearance.AccentFrom}|{appearance.AccentTo}|{SystemIsDark}";
        if (_menu is null || (!force && look == _look))
            return;
        _look = look;
        _menu.ApplyLook(Palette(settings));
    }

    static MenuPalette Palette(AppSettings settings) => MenuPalette.For(settings.Appearance, SystemIsDark);

    static bool SystemIsDark => Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

    void Warn(string message)
    {
        Log.Warn(message);
        _menuBar.Update(s => s with { Warning = message });
    }

    // ---- permissions ----

    /// <summary>
    /// The tap needs the Accessibility permission. Without it macOS is asked to show its prompt once, and a grant
    /// is picked up while Rondel runs, so no restart is needed.
    /// </summary>
    void StartTrigger()
    {
        if (Native.AXIsProcessTrusted())
        {
            InstallTap();
            return;
        }
        Log.Warn("No Accessibility permission yet; the trigger is off until it is granted");
        Native.RequestAccessibility();
        _accessibilityPoll = DispatcherTimer.Run(() =>
        {
            if (!Native.AXIsProcessTrusted())
                return true;
            Log.Info("Accessibility permission granted");
            InstallTap();
            return false;
        }, TimeSpan.FromSeconds(1));
    }

    void InstallTap()
    {
        _menuBar.Update(s => s with { NeedsAccessibility = false });
        if (!_tap.Start())
            Warn(Loc.Get("Tray.HookFailed"));
    }

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
            Dispatcher.UIThread.Post(() => _menuBar.Update(s => s with { NeedsFinderAccess = status != Native.AutomationAllowed }));
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
        var model = MenuBuilder.Build(context, Store.Current);
        if (model.Items.Count == 0)
        {
            Log.Warn($"No actions configured for {context.Kind}; check Menus in settings.json");
            return;
        }
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

    /// <summary>Session hints are filled in when they arrive; they never delay the menu.</summary>
    void RequestDetails(MenuModel model)
    {
        if (!MenuDetails.Needed(model))
            return;
        Task.Run(() => MenuDetails.Load(model)).ContinueWith(
            lookup => Dispatcher.UIThread.Post(() => _menu?.ApplyHints(model, lookup.Result.Hints)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>Until launching comes: what would start, in the log.</summary>
    void OnItemInvoked(MenuModel model, MenuItemModel item)
    {
        Log.Info($"Action '{item.Action.Id}' ({item.Agent.Id}) for {model.Context.Kind} '{item.Directory ?? model.Context.TargetDir}'; launching comes next");
        _menu?.CloseMenu(restoreFocus: true);
    }

    public void Dispose()
    {
        _accessibilityPoll?.Dispose();
        _worker.Dispose();
        _menuBar.Dispose();
        Store.Dispose();
    }
}
