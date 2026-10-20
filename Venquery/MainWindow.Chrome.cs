using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Venquery;

// 1.4.1: the window's title bar is drawn by the app (WindowChrome in MainWindow.xaml), so it looks built in like
// modern Windows apps. Windows still handles moving, double-click to maximise, resizing and the system menu; this
// partial supplies the caption buttons and, on Windows 11, Snap Layouts on the maximise button (Windows only shows
// the flyout when the button reports itself as HTMAXBUTTON to WM_NCHITTEST, so that button is hit-tested natively).
public partial class MainWindow
{
    private const int WM_NCHITTEST = 0x0084, WM_NCMOUSELEAVE = 0x02A2;
    private const int WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2;
    private const int HTMAXBUTTON = 9;
    private bool _maxPressed;

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    /// <summary>Wire the custom chrome once the HWND exists (called from OnSourceInitialized).</summary>
    private void InitChrome()
    {
        StateChanged += (_, _) => ApplyWindowState();
        ApplyWindowState();
        Services.TitleBarTheme.RoundCorners(this);
        if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(ChromeWndProc);
    }

    /// <summary>A maximised WindowChrome window extends past the screen by the resize frame, so pad the content in by
    /// that much; and swap the maximise / restore glyph.</summary>
    private void ApplyWindowState()
    {
        var max = WindowState == WindowState.Maximized;
        var f = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = max ? new Thickness(f.Left + 2, f.Top + 2, f.Right + 2, f.Bottom + 2) : new Thickness(0);
        MaxGlyph.Data = (Geometry)FindResource(max ? "CapRestore" : "CapMax");
        MaxButton.ToolTip = max ? "Restore Down" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, max ? "Restore Down" : "Maximize");
    }

    private IntPtr ChromeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (OverMaxButton(lParam))
                {
                    MaxButton.Tag = "NcHover";
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                }
                MaxButton.Tag = null;
                break;
            case WM_NCLBUTTONDOWN:
                if (wParam.ToInt32() == HTMAXBUTTON) { _maxPressed = true; handled = true; return IntPtr.Zero; }
                break;
            case WM_NCLBUTTONUP:
                if (wParam.ToInt32() == HTMAXBUTTON && _maxPressed)
                {
                    _maxPressed = false;
                    handled = true;
                    OnMaximizeRestore(MaxButton, new RoutedEventArgs());
                    return IntPtr.Zero;
                }
                _maxPressed = false;
                break;
            case WM_NCMOUSELEAVE:
                MaxButton.Tag = null;
                _maxPressed = false;
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Is the screen point in lParam (device pixels) over the maximise button?</summary>
    private bool OverMaxButton(IntPtr lParam)
    {
        try
        {
            if (!MaxButton.IsVisible || PresentationSource.FromVisual(MaxButton) is null) return false;
            var v = lParam.ToInt64();
            var screen = new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
            var p = MaxButton.PointFromScreen(screen);
            return p.X >= 0 && p.Y >= 0 && p.X < MaxButton.ActualWidth && p.Y < MaxButton.ActualHeight;
        }
        catch { return false; }   // mid-teardown or no visual tree yet
    }
}
