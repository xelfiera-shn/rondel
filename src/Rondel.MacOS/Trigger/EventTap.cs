using System.Diagnostics;
using System.Runtime.InteropServices;
using Rondel.Core.Context;
using Rondel.Core.Logging;
using Rondel.MacOS.Interop;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Trigger;

/// <summary>A swallowed Option + left press that may become a menu.</summary>
internal readonly record struct Gesture(int Id, CGPoint Point);

/// <summary>Callbacks raised on the tap thread or a timer thread. Implementations must only queue work and return.</summary>
internal interface IGestureSink
{
    /// <summary>Start detecting the context now; report it with <see cref="EventTap.ReportDetection"/>.</summary>
    void OnGestureStarted(Gesture gesture);

    /// <summary>Hold threshold reached with a usable context; the button is still down.</summary>
    void OnShowMenu(Gesture gesture, TriggerContext context);

    /// <summary>The pointer moved while the button that opened the menu is still down.</summary>
    void OnHeldMove(CGPoint point);

    /// <summary>The button that opened the menu was released (marking-menu selection).</summary>
    void OnHeldRelease(CGPoint point);

    /// <summary>A press outside the open menu (the click was swallowed).</summary>
    void OnOutsideClick();

    void OnGestureCancelled(Gesture gesture, string reason);
}

/// <summary>
/// The Option + hold trigger: an active CGEventTap on its own thread, the counterpart of the Windows mouse hook.
/// <list type="bullet">
/// <item>An Option + left press (no ⌘ ⌃ ⇧) is swallowed and detection starts at once; the callback only reads the
/// flags and Rondel's tag, everything else happens off the tap thread.</item>
/// <item>Released early, moved past the drag distance, or no usable context → the press is posted again (and the
/// release), so Option+click and Option+drag keep working.</item>
/// <item>Held for HoldMs with a usable context → the menu opens. The menu window never got the press, so the drags
/// until the release are forwarded to it; the release and presses outside the open menu are swallowed.</item>
/// </list>
/// Needs the Accessibility permission. The state is guarded by a lock: the tap thread and the timers share it.
/// </summary>
internal sealed unsafe class EventTap(IGestureSink sink)
{
    /// <summary>Marks the presses Rondel posts again, so the tap lets them through ("RNDL", as on Windows).</summary>
    const long Tag = 0x524E444C;

    /// <summary>Moving further than this (points) before the hold time gives the press back as the start of a drag.</summary>
    const double DragDistance = 4;

    /// <summary>How long after the hold threshold a slow detection may take before the press is given back.</summary>
    const int DetectionGraceMs = 1200;

    enum State { Idle, Pending, MenuHeld, MenuOpen }

    static EventTap? s_instance;

    readonly Lock _gate = new();
    nint _tap;
    int _holdMs = 300;
    bool _paused;

    State _state;
    Gesture _gesture;
    nint _down; // copy of the swallowed press, posted again when it is given back
    long _startTicks;
    TriggerContext? _context;
    bool _holdElapsed;
    Timer? _holdTimer, _graceTimer;
    int _nextId;
    bool _menuShown;
    CGRect _menuFrame;
    bool _swallowUp;

    public bool IsInstalled => _tap != 0;

    /// <summary>Installs the tap on its own thread. Fails without the Accessibility permission.</summary>
    public bool Start()
    {
        s_instance = this;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            ulong mask = (1UL << (int)LeftMouseDown) | (1UL << (int)LeftMouseUp) | (1UL << (int)LeftMouseDragged);
            _tap = CGEventTapCreate(SessionEventTap, 0 /* head insert */, 0 /* active: may swallow */, mask,
                (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&Callback, 0);
            if (_tap != 0)
                CFRunLoopAddSource(CFRunLoopGetCurrent(), CFMachPortCreateRunLoopSource(0, _tap, 0), RunLoopCommonModes);
            ready.Set();
            if (_tap != 0)
                CFRunLoopRun();
        })
        { IsBackground = true, Name = "EventTap" };
        thread.Start();
        ready.Wait();
        if (_tap == 0)
            Log.Warn("The event tap could not be installed (no Accessibility permission?)");
        else
            Log.Info($"Event tap installed (hold {_holdMs} ms{(_paused ? ", paused" : "")})");
        return _tap != 0;
    }

    public void Configure(int holdMs, bool paused)
    {
        lock (_gate)
        {
            _holdMs = holdMs;
            _paused = paused;
        }
    }

    /// <summary>Detection finished (any thread).</summary>
    public void ReportDetection(int gestureId, TriggerContext context)
    {
        lock (_gate)
        {
            if (_state != State.Pending || _gesture.Id != gestureId)
                return;
            _context = context;
            if (context.Kind == ContextKind.None)
                GiveBack($"not ours: {context.Reason}");
            else if (_holdElapsed)
                OpenMenu();
        }
    }

    /// <summary>The menu window is on screen (frame in points, top-left origin); presses outside it close it.</summary>
    public void NotifyMenuOpened(CGRect frame)
    {
        lock (_gate)
        {
            _menuShown = true;
            _menuFrame = frame;
            if (_state == State.Idle)
                _state = State.MenuOpen; // opened without the gesture (the shortcut)
        }
    }

    public void NotifyMenuClosed()
    {
        lock (_gate)
        {
            _menuShown = false;
            if (_state == State.MenuOpen)
                _state = State.Idle;
        }
    }

    [UnmanagedCallersOnly]
    static nint Callback(nint proxy, uint type, nint evt, nint userInfo)
    {
        var self = s_instance!;
        long start = Stopwatch.GetTimestamp();
        nint result = self.Handle(type, evt);
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (ms > 5)
            Log.Debug($"Slow event tap callback: {ms:F1} ms");
        return result;
    }

    /// <returns>The event to let through, or 0 to swallow it.</returns>
    nint Handle(uint type, nint evt)
    {
        if (type is TapDisabledByTimeout or TapDisabledByUserInput)
        {
            CGEventTapEnable(_tap, true);
            Log.Warn($"The event tap was disabled ({(type == TapDisabledByTimeout ? "too slow" : "by user input")}); enabled again");
            return evt;
        }
        if (CGEventGetIntegerValueField(evt, EventSourceUserData) == Tag)
            return evt;

        var point = CGEventGetLocation(evt);
        lock (_gate)
        {
            return _state switch
            {
                State.Idle => type == LeftMouseDown ? OnPress(evt, point) : evt,
                State.Pending => OnPendingEvent(type, evt, point),
                State.MenuHeld => OnHeldEvent(type, evt, point),
                _ => OnMenuOpenEvent(type, evt, point),
            };
        }
    }

    nint OnPress(nint evt, CGPoint point)
    {
        if (_paused || (CGEventGetFlags(evt) & (FlagOption | FlagCommand | FlagControl | FlagShift)) != FlagOption)
            return evt;
        _gesture = new Gesture(++_nextId, point);
        _down = CGEventCreateCopy(evt);
        _startTicks = Stopwatch.GetTimestamp();
        _context = null;
        _holdElapsed = false;
        _menuShown = false;
        _state = State.Pending;
        int id = _gesture.Id;
        _holdTimer = new Timer(_ => OnHoldElapsed(id), null, _holdMs, Timeout.Infinite);
        _graceTimer = new Timer(_ => OnGraceElapsed(id), null, _holdMs + DetectionGraceMs, Timeout.Infinite);
        sink.OnGestureStarted(_gesture);
        return 0;
    }

    nint OnPendingEvent(uint type, nint evt, CGPoint point)
    {
        if (type == LeftMouseDragged)
        {
            if (Math.Abs(point.X - _gesture.Point.X) <= DragDistance && Math.Abs(point.Y - _gesture.Point.Y) <= DragDistance)
                return 0;
            GiveBack("moved: the drag goes on");
            return evt;
        }
        if (type == LeftMouseUp)
        {
            nint up = CGEventCreateCopy(evt);
            GiveBack("released before the hold time: an ordinary Option+click");
            Post(up);
            return 0;
        }
        return evt;
    }

    nint OnHeldEvent(uint type, nint evt, CGPoint point)
    {
        if (type == LeftMouseDragged)
        {
            sink.OnHeldMove(point);
            return 0;
        }
        if (type == LeftMouseUp)
        {
            _state = _menuShown ? State.MenuOpen : State.Idle;
            sink.OnHeldRelease(point);
            return 0;
        }
        return evt;
    }

    nint OnMenuOpenEvent(uint type, nint evt, CGPoint point)
    {
        if (type == LeftMouseDown && !_menuFrame.Contains(point))
        {
            _swallowUp = true;
            _state = State.Idle;
            _menuShown = false;
            sink.OnOutsideClick();
            return 0;
        }
        if (type == LeftMouseUp && _swallowUp)
        {
            _swallowUp = false;
            return 0;
        }
        return evt;
    }

    void OnHoldElapsed(int id)
    {
        lock (_gate)
        {
            if (_state != State.Pending || _gesture.Id != id)
                return;
            _holdElapsed = true;
            if (_context is { Kind: not ContextKind.None })
                OpenMenu();
        }
    }

    void OnGraceElapsed(int id)
    {
        lock (_gate)
        {
            if (_state == State.Pending && _gesture.Id == id && _context is null)
                GiveBack("detection took too long");
        }
    }

    /// <summary>Under the lock.</summary>
    void OpenMenu()
    {
        StopTimers();
        CFRelease(_down);
        _down = 0;
        _state = State.MenuHeld;
        Log.Debug($"Gesture #{_gesture.Id}: held {Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds:F0} ms, opening the menu");
        sink.OnShowMenu(_gesture, _context!);
    }

    /// <summary>Under the lock: the swallowed press goes to whatever is under the pointer after all.</summary>
    void GiveBack(string reason)
    {
        StopTimers();
        _state = State.Idle;
        Post(_down);
        _down = 0;
        sink.OnGestureCancelled(_gesture, reason);
    }

    void StopTimers()
    {
        _holdTimer?.Dispose();
        _graceTimer?.Dispose();
        _holdTimer = _graceTimer = null;
    }

    /// <summary>Posts a copy of an event with Rondel's tag and releases it.</summary>
    static void Post(nint evt)
    {
        CGEventSetIntegerValueField(evt, EventSourceUserData, Tag);
        CGEventPost(SessionEventTap, evt);
        CFRelease(evt);
    }
}
