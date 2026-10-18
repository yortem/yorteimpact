using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YorteImpact.Services;

/// <summary>
/// Turns the native Windows title bar dark (DWM immersive dark mode) so the
/// window chrome matches the app instead of showing a bright white strip.
/// </summary>
public static class DarkTitleBar
{
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int on = 1;
        if (DwmSetWindowAttribute(hwnd, ImmersiveDarkMode, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, ImmersiveDarkModeLegacy, ref on, sizeof(int));
    }
}
