using System.Windows;
using Venquery.Services;

namespace Venquery.Controls;

/// <summary>Shows the driver updates Windows Update offers for this PC (read-only). Each entry is matched to a
/// device from Venquery's own scan by hardware ID when possible, so "which device is this for?" is answered.</summary>
public partial class WuDriversWindow : Window
{
    private readonly Func<string, string?> _deviceForHwid;

    public WuDriversWindow(Func<string, string?> deviceForHwid)
    {
        _deviceForHwid = deviceForHwid;
        InitializeComponent();
        Loaded += async (_, _) => await CheckAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (Owner is MainWindow owner) TitleBarTheme.Apply(this, owner.CurrentThemeIsDark);
    }

    private sealed class Row
    {
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Device { get; init; } = "";
        public bool HasDevice => Device.Length > 0;
    }

    private async Task CheckAsync()
    {
        BusyPanel.Visibility = Visibility.Visible;
        ResultText.Visibility = Visibility.Collapsed;
        DriverList.ItemsSource = null;
        RecheckButton.IsEnabled = false;
        AppLog.Write("windows update: checking for driver updates");
        var (drivers, error) = await WindowsUpdateDriverService.SearchAsync();
        BusyPanel.Visibility = Visibility.Collapsed;
        RecheckButton.IsEnabled = true;
        ResultText.Visibility = Visibility.Visible;
        if (error.Length > 0)
        {
            ResultText.Text = error;
            ResultText.SetResourceReference(ForegroundProperty, "Amber");
            AppLog.Write("windows update: " + error);
            return;
        }
        ResultText.ClearValue(ForegroundProperty);
        ResultText.Text = drivers.Count == 0
            ? "Windows Update has no driver updates waiting for this PC. Your drivers are as current as Windows Update offers."
            : $"Windows Update offers {drivers.Count} driver update(s) for this PC:";
        AppLog.Write($"windows update: {drivers.Count} driver update(s) offered");
        DriverList.ItemsSource = drivers.Select(d => new Row
        {
            Title = d.Title,
            Detail = string.Join(" · ", new[]
            {
                d.Manufacturer, d.DriverClass, d.Date?.ToString("yyyy-MM-dd") ?? "",
                d.SizeBytes > 0 ? OrganizerService.FormatBytes(d.SizeBytes) : "",
            }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Device = MatchDevice(d.HardwareId),
        }).ToList();
    }

    private string MatchDevice(string hwid)
    {
        if (string.IsNullOrWhiteSpace(hwid)) return "";
        var name = _deviceForHwid(hwid);
        return string.IsNullOrWhiteSpace(name) ? "" : "For: " + name;
    }

    private async void OnRecheck(object sender, RoutedEventArgs e) => await CheckAsync();
    private void OnOpenWu(object sender, RoutedEventArgs e) => WindowsUpdateDriverService.OpenWindowsUpdate();
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
