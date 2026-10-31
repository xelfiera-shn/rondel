using System.Runtime.InteropServices;
using System.Windows.Interop;
using Rondel.Core;
using Rondel.Core.Logging;
using Rondel.Windows.Interop;
using static Rondel.Windows.Interop.Native;

namespace Rondel.Windows.UI;

internal sealed record TrayMenuItem(string Text, Action? Invoke, bool Checked = false, bool IsDefault = false)
{
    public static readonly TrayMenuItem Separator = new("-", null);
}

/// <summary>
/// Hidden top-level window that owns the notification-area icon, its native context menu and
/// the global hotkey, and receives system broadcasts (theme change, taskbar restart).
/// </summary>
internal sealed unsafe class TrayHost : IDisposable
{
    const uint CallbackMessage = WM_APP + 0x100;
    const uint IconId = 1;
    const int HotkeyId = 1;

    readonly HwndSource _source;
    readonly Func<IReadOnlyList<TrayMenuItem>> _menuItems;
    readonly uint _taskbarCreated;
    nint _icon;
    bool _paused;
    string _tooltip = AppInfo.Name;
    bool _hotkeyRegistered;

    public TrayHost(Func<IReadOnlyList<TrayMenuItem>> menuItems)
    {
        _menuItems = menuItems;
        var parameters = new HwndSourceParameters("Rondel.Tray")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        DarkMenus.Enable(Handle);
    }

    public nint Handle => _source.Handle;

    /// <summary>Left click / keyboard activation of the icon.</summary>
    public event Action? Activated;

    /// <summary>Light/dark mode or accent color changed.</summary>
    public event Action? ThemeChanged;

    public event Action? HotkeyPressed;

    /// <summary>The last notification shown was clicked.</summary>
    public event Action? BalloonClicked;

    public void Show(bool paused, string tooltip)
    {
        _paused = paused;
        _tooltip = tooltip;
        AddIcon();
    }

    public void Update(bool paused, string tooltip)
    {
        bool iconChanged = paused != _paused;
        _paused = paused;
        _tooltip = tooltip;
        if (iconChanged)
            ReplaceIconHandle();
        var data = NewData(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    public void ShowBalloon(string title, string text, bool warning)
    {
        var data = NewData(NIF_INFO);
        CopyToBuffer(text, data.szInfo, 256);
        CopyToBuffer(title, data.szInfoTitle, 64);
        data.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    public bool SetHotkey(uint modifiers, uint virtualKey)
    {
        ClearHotkey();
        _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, modifiers | MOD_NOREPEAT, virtualKey);
        if (!_hotkeyRegistered)
            Log.Warn($"RegisterHotKey failed: {Marshal.GetLastPInvokeError()}");
        return _hotkeyRegistered;
    }

    public void ClearHotkey()
    {
        if (_hotkeyRegistered)
            UnregisterHotKey(Handle, HotkeyId);
        _hotkeyRegistered = false;
    }

    void AddIcon()
    {
        ReplaceIconHandle();
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NIM_ADD, &data))
        {
            // Already there (e.g. explorer restarted twice quickly): update instead.
            if (!Shell_NotifyIconW(NIM_MODIFY, &data))
                Log.Warn("Adding the notification area icon failed");
        }
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    void ReplaceIconHandle()
    {
        uint dpi = GetDpiForWindow(Handle);
        int size = GetSystemMetricsForDpi(SM_CXSMICON, dpi == 0 ? 96 : dpi);
        nint old = _icon;
        _icon = AppIconRenderer.CreateIconHandle(Math.Max(16, size), _paused);
        if (old != 0)
            DestroyIcon(old);
    }

    NOTIFYICONDATAW NewData(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        CopyToBuffer(_tooltip, data.szTip, 128);
        return data;
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        uint message = (uint)msg;
        if (message == CallbackMessage)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case WM_CONTEXTMENU:
                    ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    break;
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Activated?.Invoke();
                    break;
                case NIN_BALLOONUSERCLICK:
                    BalloonClicked?.Invoke();
                    break;
            }
            handled = true;
            return 0;
        }
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            Log.Info("Taskbar recreated, re-adding the tray icon");
            AddIcon();
            return 0;
        }
        switch (message)
        {
            case WM_HOTKEY when wParam == HotkeyId:
                HotkeyPressed?.Invoke();
                handled = true;
                break;
            case WM_SETTINGCHANGE when lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet":
            case WM_DWMCOLORIZATIONCOLORCHANGED:
            case WM_THEMECHANGED:
                DarkMenus.Refresh();
                ThemeChanged?.Invoke();
                break;
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                Update(_paused, _tooltip);
                break;
        }
        return 0;
    }

    void ShowMenu(int x, int y)
    {
        var items = _menuItems();
        nint menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (ReferenceEquals(item, TrayMenuItem.Separator))
                {
                    AppendMenuW(menu, MF_SEPARATOR, 0, null);
                    continue;
                }
                AppendMenuW(menu, MF_STRING | (item.Checked ? MF_CHECKED : 0), (nuint)(i + 1), item.Text);
                if (item.IsDefault)
                    SetMenuDefaultItem(menu, (uint)(i + 1), 0);
            }

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(Handle);
            uint align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            int command = TrackPopupMenuEx(menu, align | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, Handle, 0);
            PostMessageW(Handle, WM_NULL, 0, 0);
            if (command > 0 && command <= items.Count)
                items[command - 1].Invoke?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        ClearHotkey();
        var data = NewData(0);
        Shell_NotifyIconW(NIM_DELETE, &data);
        if (_icon != 0)
            DestroyIcon(_icon);
        _source.Dispose();
    }
}

/// <summary>
/// Native popup menus are light-only unless the app opts in through undocumented uxtheme
/// exports (Windows 10 1903+). Any failure just leaves menus light.
/// </summary>
internal static class DarkMenus
{
    static bool s_available = Environment.OSVersion.Version.Build >= 18362;

    public static void Enable(nint hwnd)
    {
        if (!s_available)
            return;
        try
        {
            SetPreferredAppMode(1); // AllowDark: follow the system app mode
            AllowDarkModeForWindow(hwnd, true);
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            s_available = false;
        }
    }

    public static void Refresh()
    {
        if (!s_available)
            return;
        try
        {
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            s_available = false;
        }
    }
}
