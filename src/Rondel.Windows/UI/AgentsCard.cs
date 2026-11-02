using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rondel.Core.Activity;
using Rondel.Core.Logging;
using Rondel.Core.Settings;
using Rondel.Windows.Interop;
using static Rondel.Windows.Interop.Native;

namespace Rondel.Windows.UI;

/// <summary>
/// Shows the agents card (<see cref="AgentsCardWindow"/>) and keeps it where the user wants it.
/// <list type="bullet">
/// <item>It appears when the first agent starts and hides when none is left; its shortcut shows or hides it.</item>
/// <item>Collapsed to its badge, it opens while the mouse is over it, toward the free side of the screen; pinned, it
/// stays open. It is dragged by its header or its badge, and its place is kept per monitor.</item>
/// <item>On top of other windows, or on the desktop: behind every window, and raised while the desktop is in front
/// (after Win+D).</item>
/// <item>Over a full-screen app it hides, or, when the user wants it there, stays open, see-through for clicks, so
/// it never takes the focus or a click from a game.</item>
/// </list>
/// It never takes the focus (WS_EX_NOACTIVATE).
/// </summary>
internal sealed unsafe class AgentsCard : IDisposable
{
    static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(450);
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ForegroundPoll = TimeSpan.FromSeconds(2);

    /// <summary>The collapsed window, with the margin for the shadow (device-independent pixels).</summary>
    const double BadgeSize = 50 + 2 * 14;

    /// <summary>Room left to the screen's edges when the card is placed the first time.</summary>
    const double EdgeGap = 24;

    const int DragThreshold = 4;

    /// <summary>For the WinEvent callback, which gets no state.</summary>
    static AgentsCard? s_instance;

    readonly AgentsCardWindow _window = new();
    readonly Func<AgentsCardOptions> _options;
    readonly Action<Action<AgentsCardOptions>> _change;
    readonly string? _stateFile;
    readonly Dictionary<string, POINT> _positions = [];
    readonly DispatcherTimer _collapse;
    readonly DispatcherTimer _refresh;
    readonly DispatcherTimer _poll;
    string? _lastMonitor;
    nint _hwnd;
    nint _hook;
    IReadOnlyList<ActiveAgent> _agents = [];
    bool _enabled;
    bool _manualShown;
    bool _manualHidden;
    bool _hovering;
    bool _fullScreen;
    bool _desktopInFront;
    bool _clickThrough;
    POINT? _anchor; // the badge's top-left corner, in device pixels
    bool _flipX;
    bool _flipY;
    Popup? _quick;
    bool _pressed;
    bool _dragging;
    POINT _dragStart;
    POINT _dragOrigin;

    /// <param name="change">Changes the card's options (saved in settings.json, so they are back after a restart).</param>
    /// <param name="stateFile">Where the card's place is kept; null for none (developer aids).</param>
    public AgentsCard(Func<AgentsCardOptions> options, Action<Action<AgentsCardOptions>> change, string? stateFile)
    {
        _options = options;
        _change = change;
        _stateFile = stateFile;
        ReadPositions();

        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Left = -32000;
        _window.Top = -32000;
        _window.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            SetExtendedStyle(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, true);
        };
        _window.MouseEnter += (_, _) => OnMouseEnter();
        _window.MouseLeave += (_, _) => OnMouseLeave();
        _window.PreviewMouseLeftButtonDown += OnPress;
        _window.PreviewMouseMove += OnDrag;
        _window.PreviewMouseLeftButtonUp += OnRelease;
        _window.AgentClicked += id => AgentClicked?.Invoke(id);
        _window.PinToggled += pinned => _change(o => o.Pinned = pinned);
        _window.MenuRequested += ShowQuickSettings;

        _collapse = new DispatcherTimer(CollapseDelay, DispatcherPriority.Normal, (_, _) => Collapse(), _window.Dispatcher);
        _collapse.Stop();
        _refresh = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => _window.ShowAgents(_agents), _window.Dispatcher);
        _refresh.Stop();
        // A window can go full screen without the foreground changing (F11 in a browser), so this looks every few seconds too.
        _poll = new DispatcherTimer(ForegroundPoll, DispatcherPriority.Background, (_, _) => CheckForeground(), _window.Dispatcher);
        _poll.Stop();

        s_instance = this;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnForegroundChanged, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>An agent was clicked on the card.</summary>
    public event Action<string>? AgentClicked;

    /// <summary>Settings… in the quick settings.</summary>
    public event Action? SettingsRequested;

    /// <summary>The card is on screen (open or collapsed): its badge tells what needs the user.</summary>
    public bool IsShown { get; private set; }

    /// <summary>The shortcut shown next to "Hide the card", if any.</summary>
    public string? HideShortcut { get; set; }

    public void ApplyLook(RondelPalette palette)
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

    /// <summary>The card's shortcut, or "Hide the card": hides a card on screen, or shows it, also with no agent.</summary>
    public void Toggle()
    {
        if (!_enabled || !_options().Show)
            return;
        if (IsShown)
        {
            _manualHidden = true;
            _manualShown = false;
        }
        else
        {
            _manualHidden = false;
            _manualShown = true;
        }
        Layout();
    }

    /// <summary>Shows, hides, opens or collapses the card, and puts it in its place and layer.</summary>
    void Layout()
    {
        var options = _options();
        bool overFullScreen = _fullScreen && options.OverFullScreen;
        bool visible = _enabled && options.Show && !_manualHidden && (_agents.Count > 0 || _manualShown) && (!_fullScreen || options.OverFullScreen);
        if (!visible)
        {
            Hide();
            return;
        }

        bool open = overFullScreen || options.Pinned || _hovering || _dragging || _quick?.IsOpen == true;
        _window.SetPinned(options.Pinned);
        _window.SetCollapsed(!open);
        _window.Opacity = overFullScreen ? 0.92 : 1;
        if (!_window.IsVisible)
            _window.Show();
        SetClickThrough(overFullScreen);
        _window.UpdateLayout();
        if (!_dragging)
            Place(open);
        SetLayer(options, overFullScreen);
        IsShown = true;
        _refresh.Start();
        _poll.Start();
    }

    void Hide()
    {
        if (_quick is { IsOpen: true })
            _quick.IsOpen = false;
        if (_window.IsVisible)
            _window.Hide();
        IsShown = false;
        _hovering = false;
        _refresh.Stop();
        // While the user wants the card over full-screen apps, the end of the full screen must still be noticed.
        if (!(_fullScreen && _enabled && _options().Show))
            _poll.Stop();
    }

    // ---- place ----

    double Scale => _hwnd == 0 ? 1 : GetDpiForWindow(_hwnd) / 96.0;

    /// <summary>
    /// The badge at its anchor; the open card from there toward the free side of the screen, kept on the work area.
    /// </summary>
    void Place(bool open)
    {
        double scale = Scale;
        var anchor = _anchor ??= StartingPlace();
        var content = (FrameworkElement)_window.Content;
        int width = (int)Math.Round(content.DesiredSize.Width * scale), height = (int)Math.Round(content.DesiredSize.Height * scale);
        int badge = (int)Math.Round(BadgeSize * scale);
        var (work, _) = GetMonitorWorkArea(new POINT(anchor.X + badge / 2, anchor.Y + badge / 2));
        int x = anchor.X, y = anchor.Y;
        if (open)
        {
            _flipX = anchor.X + width > work.Right && anchor.X + badge - width >= work.Left;
            _flipY = anchor.Y + height > work.Bottom && anchor.Y + badge - height >= work.Top;
            if (_flipX)
                x = anchor.X + badge - width;
            if (_flipY)
                y = anchor.Y + badge - height;
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        SetWindowPos(_hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Where the card was last on a monitor that is still there, or the bottom right of the main one.</summary>
    POINT StartingPlace()
    {
        if (_lastMonitor is { } key && _positions.TryGetValue(key, out var saved) && MonitorKey(saved) == key)
            return saved;
        nint primary = MonitorFromPoint(new POINT(0, 0), 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var info = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        GetMonitorInfoW(primary, &info);
        uint dpiX = 96, dpiY = 96;
        if (GetDpiForMonitor(primary, MDT_EFFECTIVE_DPI, &dpiX, &dpiY) != 0)
            dpiX = 96;
        double scale = dpiX / 96.0;
        int offset = (int)Math.Round((BadgeSize + EdgeGap) * scale);
        return new POINT(info.rcWork.Right - offset, info.rcWork.Bottom - offset);
    }

    /// <summary>Above other windows, or at the bottom (above the desktop only while it is in front).</summary>
    void SetLayer(AgentsCardOptions options, bool overFullScreen)
    {
        bool topmost = overFullScreen || options.Placement == CardPlacement.OnTop;
        if (_window.Topmost != topmost)
            _window.Topmost = topmost;
        if (!topmost)
            SetWindowPos(_hwnd, _desktopInFront ? HWND_TOP : HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    void SetClickThrough(bool through)
    {
        if (_clickThrough == through)
            return;
        _clickThrough = through;
        SetExtendedStyle(WS_EX_TRANSPARENT, through);
    }

    void SetExtendedStyle(int style, bool on)
    {
        if (_hwnd == 0)
            return;
        long current = GetWindowLongPtr(_hwnd, GWL_EXSTYLE), mask = (uint)style;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (nint)(on ? current | mask : current & ~mask));
    }

    // ---- open, collapse ----

    void OnMouseEnter()
    {
        _collapse.Stop();
        if (_hovering)
            return;
        _hovering = true;
        Layout();
    }

    void OnMouseLeave()
    {
        if (!_dragging && _quick?.IsOpen != true)
            _collapse.Start();
    }

    void Collapse()
    {
        _collapse.Stop();
        if (_window.IsMouseOver || _dragging || _quick?.IsOpen == true)
            return;
        _hovering = false;
        Layout();
    }

    // ---- dragging, by the header or the badge ----

    void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (!IsDragHandle(e.OriginalSource as DependencyObject))
            return;
        _dragStart = GetCursorPosition();
        RECT rect;
        GetWindowRect(_hwnd, &rect);
        _dragOrigin = new POINT(rect.Left, rect.Top);
        _pressed = true;
        _window.CaptureMouse();
        e.Handled = true;
    }

    void OnDrag(object sender, MouseEventArgs e)
    {
        if (!_pressed)
            return;
        var now = GetCursorPosition();
        int dx = now.X - _dragStart.X, dy = now.Y - _dragStart.Y;
        if (!_dragging && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
            return;
        _dragging = true;
        SetWindowPos(_hwnd, 0, _dragOrigin.X + dx, _dragOrigin.Y + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
            return;
        _pressed = false;
        _window.ReleaseMouseCapture();
        if (!_dragging)
            return;
        _dragging = false;
        e.Handled = true;
        // The badge's corner the card opened from is where it goes back to.
        RECT rect;
        GetWindowRect(_hwnd, &rect);
        int badge = (int)Math.Round(BadgeSize * Scale);
        bool open = !_window.IsCollapsed;
        _anchor = new POINT(open && _flipX ? rect.Right - badge : rect.Left, open && _flipY ? rect.Bottom - badge : rect.Top);
        SavePosition(_anchor.Value);
        if (!_window.IsMouseOver)
            _collapse.Start();
    }

    /// <summary>The header or the badge, but not one of the header's buttons.</summary>
    bool IsDragHandle(DependencyObject? source)
    {
        for (var node = source; node is not null && node != _window; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is Button)
                return false;
            if (node == _window.Header || node == _window.Badge)
                return true;
        }
        return false;
    }

    // ---- quick settings ----

    void ShowQuickSettings()
    {
        if (_quick is { IsOpen: true })
        {
            _quick.IsOpen = false;
            return;
        }
        Popup? popup = null;
        var panel = _window.QuickSettings(_options(), HideShortcut,
            change: change =>
            {
                popup!.IsOpen = false;
                _change(change);
            },
            hide: () =>
            {
                popup!.IsOpen = false;
                Toggle();
            },
            openSettings: () =>
            {
                popup!.IsOpen = false;
                SettingsRequested?.Invoke();
            });
        panel.Margin = new Thickness(0, 4, 12, 16); // room for its shadow
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        popup = new Popup
        {
            Child = panel,
            PlacementTarget = _window.MenuButton,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = _window.MenuButton.ActualWidth - panel.DesiredSize.Width + 12,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
        };
        popup.Closed += (_, _) =>
        {
            if (!_window.IsMouseOver)
                _collapse.Start();
        };
        _quick = popup;
        popup.IsOpen = true;
    }

    // ---- foreground: the desktop in front, a full-screen app ----

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            s_instance?.CheckForeground();
        }
        catch (Exception ex)
        {
            Log.Error("The agents card's foreground check failed", ex);
        }
    }

    void CheckForeground()
    {
        nint foreground = GetForegroundWindow();
        bool desktop = foreground != 0 && (ClassNameIs(foreground, "WorkerW") || ClassNameIs(foreground, "Progman"));
        int state = 0;
        bool fullScreen = SHQueryUserNotificationState(&state) == 0 && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        if (desktop == _desktopInFront && fullScreen == _fullScreen)
            return;
        _desktopInFront = desktop;
        _fullScreen = fullScreen;
        Layout();
    }

    // ---- the card's place, per monitor ----

    static string MonitorKey(POINT point)
    {
        nint monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        GetMonitorInfoW(monitor, &info);
        var r = info.rcMonitor;
        return $"{r.Left},{r.Top},{r.Right},{r.Bottom}";
    }

    void SavePosition(POINT anchor)
    {
        string key = MonitorKey(anchor);
        _positions[key] = anchor;
        _lastMonitor = key;
        if (_stateFile is null)
            return;
        var positions = new JsonObject();
        foreach (var (monitor, point) in _positions)
            positions[monitor] = new JsonArray(point.X, point.Y);
        var json = new JsonObject { ["Last"] = _lastMonitor, ["Positions"] = positions };
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
            _lastMonitor = json["Last"]?.GetValue<string>();
            if (json["Positions"] is JsonObject positions)
            {
                foreach (var (monitor, value) in positions)
                {
                    if (value is JsonArray { Count: 2 } point)
                        _positions[monitor] = new POINT(point[0]!.GetValue<int>(), point[1]!.GetValue<int>());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            Log.Warn($"Reading {_stateFile} failed", ex);
        }
    }

    /// <summary>For the card demo's report: what is on screen, where, and with which window styles.</summary>
    public string Describe()
    {
        if (!IsShown || _hwnd == 0)
            return "hidden";
        RECT rect;
        GetWindowRect(_hwnd, &rect);
        var (work, dpi) = GetMonitorWorkArea(new POINT(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
        long style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        bool inside = rect.Left >= work.Left && rect.Top >= work.Top && rect.Right <= work.Right && rect.Bottom <= work.Bottom;
        return $"{(_window.IsCollapsed ? "badge" : "open")}, window {rect} in work area {work} ({(inside ? "inside" : "OUTSIDE")}, {dpi} dpi), "
            + $"anchor {_anchor}, topmost {_window.Topmost}, "
            + $"tool window {(style & WS_EX_TOOLWINDOW) != 0}, no-activate {(style & WS_EX_NOACTIVATE) != 0}, click-through {(style & WS_EX_TRANSPARENT) != 0}";
    }

    public void Dispose()
    {
        if (_hook != 0)
            UnhookWinEvent(_hook);
        _hook = 0;
        s_instance = null;
        _collapse.Stop();
        _refresh.Stop();
        _poll.Stop();
        _window.Close();
    }
}
