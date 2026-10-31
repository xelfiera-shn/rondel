using System.Runtime.InteropServices;
using Rondel.Core.Logging;
using static Rondel.Windows.Interop.Native;

namespace Rondel.Windows.Interop;

/// <summary>
/// Re-injects mouse input that the hook swallowed. Every event carries <see cref="Tag"/> in
/// dwExtraInfo so our own hook recognises and ignores it.
/// </summary>
internal static unsafe class InputInjector
{
    public const nuint Tag = 0x524E_444C; // "RNDL"

    /// <summary>A complete click: press at <paramref name="down"/>, release at <paramref name="up"/>.</summary>
    public static void Click(POINT down, POINT up)
    {
        Span<INPUT> inputs = stackalloc INPUT[4];
        int n = 0;
        bool moved = down.X != up.X || down.Y != up.Y;
        if (moved) inputs[n++] = AbsoluteMove(down);
        inputs[n++] = Button(MOUSEEVENTF_LEFTDOWN);
        if (moved) inputs[n++] = AbsoluteMove(up);
        inputs[n++] = Button(MOUSEEVENTF_LEFTUP);
        Send(inputs[..n], "click");
    }

    /// <summary>Press at <paramref name="down"/> and leave the button down at <paramref name="current"/> (drag or hold continues).</summary>
    public static void Press(POINT down, POINT current)
    {
        Span<INPUT> inputs = stackalloc INPUT[3];
        int n = 0;
        bool moved = down.X != current.X || down.Y != current.Y;
        if (moved) inputs[n++] = AbsoluteMove(down);
        inputs[n++] = Button(MOUSEEVENTF_LEFTDOWN);
        if (moved) inputs[n++] = AbsoluteMove(current);
        Send(inputs[..n], "press");
    }

    /// <summary>
    /// An empty input event. Windows only lets the process that produced the last input take the
    /// foreground; sending this first lets SetForegroundWindow succeed for the menu.
    /// </summary>
    public static void Nudge()
    {
        Span<INPUT> inputs = stackalloc INPUT[1];
        inputs[0] = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwExtraInfo = Tag } };
        Send(inputs, "nudge");
    }

    static INPUT Button(uint flags) =>
        new() { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flags, dwExtraInfo = Tag } };

    static INPUT AbsoluteMove(POINT p)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        // Normalised 0..65535 coordinates over the virtual desktop; rounding up maps back to the exact pixel.
        int dx = (int)(((long)(p.X - left) * 65536 + width - 1) / width);
        int dy = (int)(((long)(p.Y - top) * 65536 + height - 1) / height);
        return new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                dwExtraInfo = Tag,
            },
        };
    }

    static void Send(Span<INPUT> inputs, string what)
    {
        fixed (INPUT* p = inputs)
        {
            uint sent = SendInput((uint)inputs.Length, p, sizeof(INPUT));
            if (sent != inputs.Length)
                Log.Warn($"SendInput ({what}) injected {sent}/{inputs.Length} events, error {Marshal.GetLastPInvokeError()}");
        }
    }
}
