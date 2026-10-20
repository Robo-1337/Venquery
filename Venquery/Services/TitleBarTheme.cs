using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Venquery.Services;

/// <summary>Paints a window's native title bar (chrome) dark or light to match the app theme via
/// DWM immersive dark mode. Shared by the main window and its dialogs so a dark theme never leaves a
/// white title bar on a pop-up (e.g. the Export dialog).</summary>
public static class TitleBarTheme
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 20H1+/11, 19 on 1809–1903. Older Windows just
    // returns an error and the title bar stays default, harmless.
    private const int DwmwaImmersiveDarkMode = 20;
    private const int DwmwaImmersiveDarkModeOld = 19;

    /// <summary>Paint <paramref name="window"/>'s native title bar dark (or light). No-op until the
    /// window has an HWND, call again from <c>OnSourceInitialized</c> if applied during construction.</summary>
    public static void Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaImmersiveDarkMode, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaImmersiveDarkModeOld, ref on, sizeof(int));
    }

    // DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11): 2 = round. Older Windows returns an error, harmless.
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>Ask Windows 11 for the standard rounded window corners (an app-drawn title bar would otherwise lose
    /// them). A no-op on Windows 10/7.</summary>
    public static void RoundCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int round = 2;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
    }
}
