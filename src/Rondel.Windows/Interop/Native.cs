using System.Runtime.InteropServices;

namespace Rondel.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;

    public POINT(int x, int y) { X = x; Y = y; }

    public override readonly string ToString() => $"({X},{Y})";
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;

    public override readonly string ToString() => $"[{Left},{Top},{Right},{Bottom}]";
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nuint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public nuint dwExtraInfo;
}

/// <summary>INPUT with only the mouse member of the union (the largest one, so the size matches).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct INPUT
{
    public uint type;
    public MOUSEINPUT mi;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
    public int fIcon;
    public uint xHotspot;
    public uint yHotspot;
    public nint hbmMask;
    public nint hbmColor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public nint hIcon;
    public fixed char szTip[128];
    public uint dwState;
    public uint dwStateMask;
    public fixed char szInfo[256];
    public uint uTimeoutOrVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

internal static unsafe partial class Native
{
    // Window messages
    public const uint WM_NULL = 0x0000;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_XBUTTONDOWN = 0x020B;
    public const uint WM_XBUTTONUP = 0x020C;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_THEMECHANGED = 0x031A;
    public const uint WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    public const uint WM_USER = 0x0400;
    public const uint WM_APP = 0x8000;

    // Hooks
    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;
    public const uint LLMHF_INJECTED = 0x1;
    public const uint PM_NOREMOVE = 0x0;

    // dwExtraInfo signature Windows puts on mouse messages synthesized from pen/touch input.
    public const nuint MI_WP_SIGNATURE_MASK = 0xFFFFFF00;
    public const nuint MI_WP_SIGNATURE = 0xFF515700;

    // Virtual keys
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    // SendInput
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    // System metrics
    public const int SM_MENUDROPALIGNMENT = 40;
    public const int SM_CXSMICON = 49;
    public const int SM_CXICON = 11;
    public const int SM_CXDRAG = 68;
    public const int SM_CYDRAG = 69;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    // Windows
    public const uint GA_ROOT = 2;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;
    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_TOP = 0;
    public static readonly nint HWND_BOTTOM = 1;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>SHQueryUserNotificationState: a full-screen app, a Direct3D game in full screen, presentation mode.</summary>
    public const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    // Processes / tokens
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;

    // Notify icon
    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01;
    public const uint NIF_ICON = 0x02;
    public const uint NIF_TIP = 0x04;
    public const uint NIF_INFO = 0x10;
    public const uint NIF_SHOWTIP = 0x80;
    public const uint NIIF_INFO = 0x1;
    public const uint NIIF_WARNING = 0x2;
    public const uint NIIF_USER = 0x4;
    public const uint NIIF_LARGE_ICON = 0x20;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIN_SELECT = WM_USER;
    public const uint NIN_KEYSELECT = WM_USER + 1;
    public const uint NIN_BALLOONUSERCLICK = WM_USER + 5;
    public const int SW_RESTORE = 9;

    // Menus
    public const uint MF_STRING = 0x0000;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RIGHTALIGN = 0x0008;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;

    // Hotkeys
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    // ---- user32: hooks and message loop ----

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn, nint hmod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(MSG* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(MSG* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(MSG* lpMsg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(MSG* lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hWnd, nuint uIDEvent);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string lpString);

    // ---- user32: input ----

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(POINT* lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    // ---- user32: windows ----

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    public static partial nint GetParent(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int GetClassNameW(nint hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, uint* lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowExW(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong32(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    public static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    public static nint SetWindowLongPtr(nint hWnd, int nIndex, nint value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : SetWindowLong32(hWnd, nIndex, (int)value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, RECT* lpRect);

    public const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    public static partial nint GetTopWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(int* pquns);

    // ---- user32 / shcore: monitors and DPI ----

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint hMonitor, MONITORINFO* lpmi);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hmonitor, int dpiType, uint* dpiX, uint* dpiY);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    // ---- user32 / shell32: tray, menus, icons ----

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIconW(uint dwMessage, NOTIFYICONDATAW* lpData);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetMenuDefaultItem(nint hMenu, uint uItem, uint fByPos);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint hMenu);

    [LibraryImport("user32.dll")]
    public static partial nint CreateIconIndirect(ICONINFO* piconinfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* pbmi, uint usage, void** ppvBits, nint hSection, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, void* lpBits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    // ---- uxtheme (undocumented, Windows 10 1903+): lets native popup menus follow dark mode ----

    [LibraryImport("uxtheme.dll", EntryPoint = "#135")]
    public static partial int SetPreferredAppMode(int mode);

    [LibraryImport("uxtheme.dll", EntryPoint = "#133")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowDarkModeForWindow(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

    [LibraryImport("uxtheme.dll", EntryPoint = "#136")]
    public static partial void FlushMenuThemes();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint dwProcessId);

    public const uint ASFW_ANY = unchecked((uint)-1);

    // ---- kernel32 / advapi32 ----

    public const uint FILE_ADD_SUBDIRECTORY = 0x0004;
    public const uint FILE_SHARE_ALL = 0x7;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const int ERROR_ACCESS_DENIED = 5;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    public static partial nint GetModuleHandleW(char* lpModuleName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, nint* tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, void* tokenInformation, uint tokenInformationLength, uint* returnLength);

    // ---- helpers ----

    public static string GetClassName(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        int length = GetClassNameW(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    /// <summary>Allocation-free class name comparison (used on the hook thread).</summary>
    public static bool ClassNameIs(nint hwnd, string name)
    {
        char* buffer = stackalloc char[64];
        int length = GetClassNameW(hwnd, buffer, 64);
        return length == name.Length && new ReadOnlySpan<char>(buffer, length).SequenceEqual(name);
    }

    /// <summary>First window with class <paramref name="className"/> from <paramref name="hwnd"/> up to <paramref name="root"/>, or 0.</summary>
    public static nint FindAncestor(nint hwnd, nint root, string className)
    {
        for (int depth = 0; depth < 16 && hwnd != 0; depth++)
        {
            if (ClassNameIs(hwnd, className))
                return hwnd;
            if (hwnd == root)
                break;
            hwnd = GetParent(hwnd);
        }
        return 0;
    }

    public static uint GetWindowProcessId(nint hwnd)
    {
        uint pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        return pid;
    }

    /// <summary>True/false if the process token could be read, null if access was denied.</summary>
    public static bool? IsProcessElevated(uint pid)
    {
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
            return null;
        try
        {
            nint token;
            if (!OpenProcessToken(process, TOKEN_QUERY, &token))
                return null;
            try
            {
                int elevated = 0;
                uint length = 0;
                if (!GetTokenInformation(token, TokenElevation, &elevated, sizeof(int), &length))
                    return null;
                return elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static POINT GetCursorPosition()
    {
        POINT pt;
        GetCursorPos(&pt);
        return pt;
    }

    public static (RECT Work, uint Dpi) GetMonitorWorkArea(POINT pt)
    {
        nint monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        GetMonitorInfoW(monitor, &info);
        uint dpiX = 96, dpiY = 96;
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY) != 0)
            dpiX = 96;
        return (info.rcWork, dpiX);
    }

    public static uint GetDpiAtPoint(POINT pt)
    {
        nint monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        uint dpiX = 96, dpiY = 96;
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY) == 0 ? dpiX : 96;
    }

    public static void CopyToBuffer(string value, char* destination, int capacity)
    {
        int length = Math.Min(value.Length, capacity - 1);
        value.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }
}
