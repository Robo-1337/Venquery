using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Venquery;

public partial class ExportDialog : Window
{
    public bool IncludeStability => IncludeStabilityCheck.IsChecked == true;

    /// <summary>Which stability categories the user ticked (group keys understood by StabilityReport).</summary>
    public ISet<string> StabilityGroups
    {
        get
        {
            var set = new HashSet<string>();
            if (GrpCrash.IsChecked == true) set.Add("crash");
            if (GrpFreeze.IsChecked == true) set.Add("freeze");
            if (GrpHardware.IsChecked == true) set.Add("hardware");
            if (GrpStorage.IsChecked == true) set.Add("storage");
            if (GrpDevice.IsChecked == true) set.Add("device");
            if (GrpApp.IsChecked == true) set.Add("app");
            return set;
        }
    }

    /// <summary>Append the (verbose) Reliability Monitor history. Off by default.</summary>
    public bool IncludeReliability => GrpReliability.IsChecked == true;

    /// <summary>Keep full, unshortened event text in the report.</summary>
    public bool VerboseStability => VerboseCheck.IsChecked == true;

    /// <summary>Advanced: append devices that matched no definition (mostly in-box-driver
    /// hardware) so an unknown hardware ID can be looked up. Off by default.</summary>
    public bool IncludeUnrecognized => IncludeUnrecognizedCheck.IsChecked == true;

    /// <summary>Append the read-only system-health snapshot (storage/memory/battery). On by default.</summary>
    public bool IncludeHealth => IncludeHealthCheck.IsChecked == true;

    /// <summary>Also write a devices.csv (the driver table) beside the text report. Off by default.</summary>
    public bool IncludeCsv => IncludeCsvCheck.IsChecked == true;

    /// <summary>Also write a report.json (machine-readable) beside the text report. Off by default.</summary>
    public bool IncludeJson => IncludeJsonCheck.IsChecked == true;

    /// <summary>Also save this session's diagnostic log beside the report (on by default: it's what support needs).</summary>
    public bool IncludeLog => IncludeLogCheck.IsChecked == true;

    /// <summary>Chosen look-back window in days (clamped to a sane 1–3650 range).</summary>
    public int Days => Math.Max(1, Math.Min(3650, int.TryParse(DaysBox.Text, out var d) ? d : 30));

    public ExportDialog() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // The HWND exists now, match the native title bar to the owner's theme so a dark theme
        // doesn't leave this dialog with a white title bar.
        if (Owner is MainWindow owner)
            Services.TitleBarTheme.Apply(this, owner.CurrentThemeIsDark);

        // Never let the dialog exceed the screen (the bug that hid the Export button). The body scrolls,
        // so a capped height keeps the pinned footer on-screen on any display; also nudge it up if the
        // starting height would spill below the work area.
        var work = SystemParameters.WorkArea.Height;
        MaxHeight = Math.Max(360, work - 48);
        if (Height > MaxHeight) Height = MaxHeight;
    }

    private void OnToggleStability(object sender, MouseButtonEventArgs e)
        => IncludeStabilityCheck.IsChecked = !(IncludeStabilityCheck.IsChecked == true);

    private void OnToggleUnrecognized(object sender, MouseButtonEventArgs e)
        => IncludeUnrecognizedCheck.IsChecked = !(IncludeUnrecognizedCheck.IsChecked == true);

    private void OnToggleHealth(object sender, MouseButtonEventArgs e)
        => IncludeHealthCheck.IsChecked = !(IncludeHealthCheck.IsChecked == true);

    private void OnToggleCsv(object sender, MouseButtonEventArgs e)
        => IncludeCsvCheck.IsChecked = !(IncludeCsvCheck.IsChecked == true);

    private void OnToggleJson(object sender, MouseButtonEventArgs e)
        => IncludeJsonCheck.IsChecked = !(IncludeJsonCheck.IsChecked == true);

    private void OnToggleLog(object sender, MouseButtonEventArgs e)
        => IncludeLogCheck.IsChecked = !(IncludeLogCheck.IsChecked == true);

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) DaysBox.Text = b.Content?.ToString() ?? "30";
    }

    private void OnDaysInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");   // digits only

    private void OnExport(object sender, RoutedEventArgs e) { DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }
}
