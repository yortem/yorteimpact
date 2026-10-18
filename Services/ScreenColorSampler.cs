using System.Runtime.InteropServices;
using System.Windows;

namespace YorteImpact.Services;

/// <summary>
/// Screen-wide eyedropper support: a low-level mouse hook that reports the
/// next global left-click (even outside our window), plus 1px desktop sampling
/// via the screen DC. In-canvas clicks are handled by WPF itself (precise,
/// zoom-independent); the hook only fires for clicks outside our window.
/// </summary>
public static class ScreenColorSampler
{
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private static HookProc? _proc;
    private static IntPtr _hook;
    private static Action<Point>? _callback;

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr hdc, int x, int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    public static bool IsActive => _hook != IntPtr.Zero;

    public static void StartGlobalClick(Action<Point> onClick)
    {
        Stop();
        _callback = onClick;
        _proc = Hook; // rooted in static field for the hook's lifetime
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        _callback = null;
    }

    public static bool IsPointInWindow(Point screenPoint, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        return screenPoint.X >= r.left && screenPoint.X < r.right
            && screenPoint.Y >= r.top && screenPoint.Y < r.bottom;
    }

    /// <summary>Sample one desktop pixel (screen coordinates). Falls back to black.</summary>
    public static System.Windows.Media.Color SampleScreenPixel(Point screenPoint)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            uint c = GetPixel(dc, (int)screenPoint.X, (int)screenPoint.Y);
            if (c == 0xFFFFFFFF) return System.Windows.Media.Colors.Black;
            return System.Windows.Media.Color.FromRgb(
                (byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private static IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN && _callback != null)
        {
            var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            _callback(new Point(s.pt.x, s.pt.y));
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
