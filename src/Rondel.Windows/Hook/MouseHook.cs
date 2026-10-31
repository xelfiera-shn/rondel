using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Rondel.Core.Context;
using Rondel.Core.Logging;
using Rondel.Windows.Detection;
using Rondel.Windows.Interop;
using static Rondel.Windows.Interop.Native;

namespace Rondel.Windows.Hook;

/// <summary>A swallowed Ctrl + left press that may become a menu.</summary>
internal readonly record struct Gesture(int Id, POINT Point, nint RootWindow, nint HitWindow);

/// <summary>Callbacks raised on the hook thread. Implementations must only queue work and return.</summary>
internal interface IGestureSink
{
    /// <summary>Start detecting the context now; report it with <see cref="MouseHook.ReportDetection"/>.</summary>
    void OnGestureStarted(Gesture gesture);

    /// <summary>Hold threshold reached with a usable context; the button is still down.</summary>
    void OnShowMenu(Gesture gesture, TriggerContext context);

    /// <summary>The button that opened the menu was released (marking-menu selection).</summary>
    void OnHeldRelease(POINT point);

    /// <summary>A mouse button went down outside the open menu (the click was swallowed).</summary>
    void OnOutsideClick();

    void OnGestureCancelled(Gesture gesture, string reason);
}

/// <summary>
/// Global WH_MOUSE_LL hook on its own thread. It owns the trigger state machine:
/// <list type="bullet">
/// <item>A physical Ctrl + left press over the desktop or an Explorer items view is swallowed and detection starts right away.</item>
/// <item>Released early, moved past the drag threshold, or no usable context → the original input is re-injected,
/// so normal Ctrl+click and Ctrl+drag behave exactly as before.</item>
/// <item>Held for HoldMs with a usable context → the menu opens; the release and clicks outside the menu are swallowed.</item>
/// </list>
/// The callback does only cheap Win32 checks; all state lives on the hook thread.
/// </summary>
internal sealed unsafe class MouseHook : IDisposable
{
    const uint WM_APP_DETECTION = WM_APP + 1;
    const uint WM_APP_MENU_OPENED = WM_APP + 2;
    const uint WM_APP_MENU_CLOSED = WM_APP + 3;
    const uint WM_APP_OPTIONS = WM_APP + 4;

    /// <summary>How long after the hold threshold we wait for a slow detection before giving the click back.</summary>
    const int DetectionGraceMs = 1200;

    enum State { Idle, Pending, MenuHeld, MenuOpen }

    enum Replay { Click, Press }

    static MouseHook? s_instance;
    static readonly bool s_selfElevated = Environment.IsPrivilegedProcess;
    static readonly uint s_selfPid = (uint)Environment.ProcessId;

    readonly IGestureSink _sink;
    readonly ConcurrentQueue<(int Id, TriggerContext Context)> _detections = new();
    readonly Dictionary<uint, (bool Elevated, long Tick)> _elevation = [];
    Thread? _thread;
    uint _threadId;
    nint _hook;

    int _holdMs;
    bool _paused;

    State _state;
    Gesture _gesture;
    int _nextId;
    POINT _lastPoint;
    int _dragCx, _dragCy;
    bool _holdElapsed;
    TriggerContext? _context;
    nuint _holdTimer, _graceTimer;
    nint _menuWindow;
    uint _swallowUp;

    public MouseHook(IGestureSink sink, int holdMs, bool paused)
    {
        _sink = sink;
        _holdMs = holdMs;
        _paused = paused;
    }

    public bool IsInstalled => _hook != 0;

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "MouseHook",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        ready.Wait();
    }

    // ---- thread-safe API (marshalled to the hook thread) ----

    public void ReportDetection(int gestureId, TriggerContext context)
    {
        _detections.Enqueue((gestureId, context));
        Post(WM_APP_DETECTION, 0, 0);
    }

    public void NotifyMenuOpened(nint menuWindow) => Post(WM_APP_MENU_OPENED, 0, menuWindow);

    public void NotifyMenuClosed() => Post(WM_APP_MENU_CLOSED, 0, 0);

    public void Configure(int holdMs, bool paused)
    {
        if (_threadId == 0)
        {
            _holdMs = holdMs;
            _paused = paused;
            return;
        }
        Post(WM_APP_OPTIONS, (nuint)holdMs, paused ? 1 : 0);
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            Post(WM_QUIT, 0, 0);
            _thread?.Join(TimeSpan.FromSeconds(1));
        }
    }

    void Post(uint message, nuint wParam, nint lParam)
    {
        if (_threadId != 0 && !PostThreadMessageW(_threadId, message, wParam, lParam))
            Log.Warn($"PostThreadMessage(0x{message:X}) to the hook thread failed: {Marshal.GetLastPInvokeError()}");
    }

    // ---- hook thread ----

    void Run(ManualResetEventSlim ready)
    {
        MSG msg;
        PeekMessageW(&msg, 0, 0, 0, PM_NOREMOVE); // make sure the thread has a message queue
        _threadId = GetCurrentThreadId();
        s_instance = this;
        _hook = SetWindowsHookExW(WH_MOUSE_LL, &HookProc, GetModuleHandleW(null), 0);
        if (_hook == 0)
            Log.Error($"SetWindowsHookEx(WH_MOUSE_LL) failed: {Marshal.GetLastPInvokeError()}");
        else
            Log.Info("Mouse hook installed");
        ready.Set();

        while (_hook != 0 && GetMessageW(&msg, 0, 0, 0) > 0)
        {
            if (msg.hwnd == 0)
            {
                OnThreadMessage(in msg);
                continue;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        if (_hook != 0)
            UnhookWindowsHookEx(_hook);
        s_instance = null;
    }

    [UnmanagedCallersOnly]
    static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode == HC_ACTION && s_instance is { } hook)
        {
            try
            {
                if (hook.OnMouse((uint)wParam, (MSLLHOOKSTRUCT*)lParam))
                    return 1;
            }
            catch (Exception ex)
            {
                // Never let an exception cross the native boundary; fall back to pass-through.
                Log.Error("Mouse hook callback failed", ex);
                hook.ResetToIdle();
            }
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <returns>true to swallow the event.</returns>
    bool OnMouse(uint message, MSLLHOOKSTRUCT* info)
    {
        if (info->dwExtraInfo == InputInjector.Tag)
            return false; // our own re-injected input

        if (_swallowUp != 0 && message == _swallowUp)
        {
            _swallowUp = 0;
            return true; // release of a click we swallowed to close the menu
        }

        switch (_state)
        {
            case State.Idle:
                return message == WM_LBUTTONDOWN && TryBeginGesture(info);

            case State.Pending:
                return OnPendingMouse(message, info);

            case State.MenuHeld:
                if (message == WM_LBUTTONUP)
                {
                    _state = State.MenuOpen;
                    _sink.OnHeldRelease(info->pt);
                    return true;
                }
                return false;

            case State.MenuOpen:
                if (!IsButtonDown(message))
                    return false;
                if (_menuWindow != 0 && !IsWindowVisible(_menuWindow))
                {
                    // The menu went away without telling us; behave as if idle.
                    _state = State.Idle;
                    return message == WM_LBUTTONDOWN && TryBeginGesture(info);
                }
                if (IsOverOwnWindow(info->pt))
                    return false;
                _swallowUp = UpMessageFor(message);
                _sink.OnOutsideClick();
                return true;
        }
        return false;
    }

    bool TryBeginGesture(MSLLHOOKSTRUCT* info)
    {
        if (_paused)
            return false;
        if ((info->flags & LLMHF_INJECTED) != 0)
            return false; // only physical input triggers
        if ((info->dwExtraInfo & MI_WP_SIGNATURE_MASK) == MI_WP_SIGNATURE)
            return false; // pen / touch
        if (!IsDown(VK_CONTROL) || IsDown(VK_SHIFT) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN))
            return false;
        if (!QuickTargetCheck(info->pt, out nint root, out nint hit))
            return false;

        _gesture = new Gesture(++_nextId, info->pt, root, hit);
        _lastPoint = info->pt;
        _holdElapsed = false;
        _context = null;
        uint dpi = GetDpiAtPoint(info->pt);
        _dragCx = Math.Max(4, GetSystemMetricsForDpi(SM_CXDRAG, dpi));
        _dragCy = Math.Max(4, GetSystemMetricsForDpi(SM_CYDRAG, dpi));
        _state = State.Pending;
        _holdTimer = SetTimer(0, 0, (uint)_holdMs, 0);
        _graceTimer = SetTimer(0, 0, (uint)(_holdMs + DetectionGraceMs), 0);
        _sink.OnGestureStarted(_gesture);
        return true;
    }

    bool OnPendingMouse(uint message, MSLLHOOKSTRUCT* info)
    {
        switch (message)
        {
            case WM_MOUSEMOVE:
                if (Math.Abs(info->pt.X - _gesture.Point.X) > _dragCx || Math.Abs(info->pt.Y - _gesture.Point.Y) > _dragCy)
                {
                    // A Ctrl+drag: give Explorer the press at the original point plus this move.
                    Cancel("drag", Replay.Press, info->pt);
                    return true;
                }
                _lastPoint = info->pt;
                return false;

            case WM_LBUTTONUP:
                // A plain Ctrl+click.
                Cancel("released before hold threshold", Replay.Click, info->pt);
                return true;

            default:
                return false;
        }
    }

    void OnThreadMessage(in MSG msg)
    {
        switch (msg.message)
        {
            case WM_TIMER:
                OnTimer(msg.wParam);
                break;

            case WM_APP_DETECTION:
                while (_detections.TryDequeue(out var detection))
                {
                    if (_state == State.Pending && detection.Id == _gesture.Id)
                    {
                        _context = detection.Context;
                        TryResolve();
                    }
                }
                break;

            case WM_APP_MENU_OPENED:
                _menuWindow = msg.lParam;
                if (_state == State.Idle)
                    _state = State.MenuOpen; // opened by the hotkey
                break;

            case WM_APP_MENU_CLOSED:
                if (_state is State.MenuOpen or State.MenuHeld)
                    _state = State.Idle;
                break;

            case WM_APP_OPTIONS:
                _holdMs = (int)msg.wParam;
                _paused = msg.lParam != 0;
                if (_paused && _state == State.Pending)
                    Cancel("paused", Replay.Press, _lastPoint);
                break;
        }
    }

    void OnTimer(nuint id)
    {
        KillTimer(0, id);
        if (id == _holdTimer)
        {
            _holdTimer = 0;
            if (_state == State.Pending)
            {
                _holdElapsed = true;
                TryResolve();
            }
        }
        else if (id == _graceTimer)
        {
            _graceTimer = 0;
            if (_state == State.Pending && _context is null)
                Cancel("context detection timed out", Replay.Press, _lastPoint);
        }
    }

    void TryResolve()
    {
        if (_context is null)
            return; // still detecting; the grace timer bounds the wait
        if (_context.Kind == ContextKind.None)
        {
            // Button is still physically down: hand the press back so a hold / drag continues normally.
            Cancel($"no context ({_context.Reason})", Replay.Press, _lastPoint);
            return;
        }
        if (!_holdElapsed)
            return;

        StopTimers();
        _state = State.MenuHeld;
        _sink.OnShowMenu(_gesture, _context);
    }

    /// <param name="replay">Click: the button was released (inject press + release). Press: it is still down.</param>
    /// <param name="current">Where the cursor is now.</param>
    void Cancel(string reason, Replay replay, POINT current)
    {
        StopTimers();
        _state = State.Idle;
        if (replay == Replay.Click)
            InputInjector.Click(_gesture.Point, current);
        else
            InputInjector.Press(_gesture.Point, current);
        _sink.OnGestureCancelled(_gesture, reason);
    }

    void StopTimers()
    {
        if (_holdTimer != 0) { KillTimer(0, _holdTimer); _holdTimer = 0; }
        if (_graceTimer != 0) { KillTimer(0, _graceTimer); _graceTimer = 0; }
    }

    void ResetToIdle()
    {
        StopTimers();
        _state = State.Idle;
        _swallowUp = 0;
    }

    /// <summary>
    /// Cheap filter run inside the hook: the press must land in a shell items view
    /// (SHELLDLL_DefView) of the desktop (Progman/WorkerW) or an Explorer window (CabinetWClass),
    /// and that window must not belong to an elevated process (we could not re-inject into it).
    /// </summary>
    bool QuickTargetCheck(POINT pt, out nint root, out nint hit)
    {
        root = 0;
        hit = WindowFromPoint(pt);
        if (hit == 0)
            return false;
        root = GetAncestor(hit, GA_ROOT);
        if (root == 0)
            return false;
        if (!ClassNameIs(root, "Progman") && !ClassNameIs(root, "WorkerW") && !ClassNameIs(root, "CabinetWClass"))
            return false;

        if (FindAncestor(hit, root, "SHELLDLL_DefView") == 0)
        {
            if (Log.DebugEnabled)
                Log.Debug($"Ctrl+press on {GetClassName(root)} ignored: '{GetClassName(hit)}' is not inside SHELLDLL_DefView");
            return false;
        }

        if (!s_selfElevated && IsElevated(root))
        {
            Log.Info("Ctrl+press on an elevated Explorer window ignored (input cannot be re-injected there)");
            return false;
        }
        return true;
    }

    bool IsElevated(nint hwnd)
    {
        uint pid = GetWindowProcessId(hwnd);
        long now = Environment.TickCount64;
        if (_elevation.TryGetValue(pid, out var cached) && now - cached.Tick < 60_000)
            return cached.Elevated;
        bool elevated = IsProcessElevated(pid) ?? true; // unknown → assume we can't inject
        if (_elevation.Count > 32)
            _elevation.Clear();
        _elevation[pid] = (elevated, now);
        return elevated;
    }

    /// <summary>Any window of this process (menu, its popups, settings) counts as "inside".</summary>
    static bool IsOverOwnWindow(POINT pt)
    {
        nint hwnd = WindowFromPoint(pt);
        return hwnd != 0 && GetWindowProcessId(GetAncestor(hwnd, GA_ROOT)) == s_selfPid;
    }

    static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static bool IsButtonDown(uint message) =>
        message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN;

    static uint UpMessageFor(uint downMessage) => downMessage switch
    {
        WM_LBUTTONDOWN => WM_LBUTTONUP,
        WM_RBUTTONDOWN => WM_RBUTTONUP,
        WM_MBUTTONDOWN => WM_MBUTTONUP,
        _ => WM_XBUTTONUP,
    };
}
