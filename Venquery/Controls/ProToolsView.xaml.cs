using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Venquery.Services;

namespace Venquery.Controls;

/// <summary>Repair Kit page: a read-only admin console (one elevated read gathers
/// storage/SMART, NVMe, thermals, security posture, servicing and diagnostics) plus a library of guarded
/// repair scripts. Self-contained; compiled only into builds that define PROTOOLS. Raises
/// <see cref="StatusChanged"/> instead of touching the host's status line, and reuses <c>MainWindow</c>'s
/// internal shell helpers.</summary>
public partial class ProToolsView : UserControl
{
    private DeepReadResult? _lastRead;
    private string _savedRepairScript = "";
    private readonly List<FixRow> _fixRows = ProToolsService.Fixes.Select(f => new FixRow { Fix = f }).ToList();

    public event Action<string>? StatusChanged;

    public ProToolsView()
    {
        InitializeComponent();
        ToolsTitle.Text = Edition.Current.ToolsName;
        ToolsIntro.Text = Edition.Current.ToolsIntro;
        ToolsDeepReadText.Text = Edition.Current.ToolsDeepReadText;
        ToolsFooter.Text = Edition.Current.ToolsFooter;
        FixList.ItemsSource = _fixRows;
    }

    private Window? Owner => Window.GetWindow(this);

    // ---- Section 1: elevated deep read ----

    private async void OnReadWithAdmin(object sender, RoutedEventArgs e)
    {
        DeepReadButton.IsEnabled = false;
        DeepReadStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DeepReadStatus.Text = "Requesting administrator approval…";
        try
        {
            var result = await ProToolsService.RunElevatedDeepReadAsync();
            if (result is null)
            {
                DeepReadStatus.SetResourceReference(TextBlock.ForegroundProperty, "Amber");
                DeepReadStatus.Text = "Administrator permission was not granted, or the read returned nothing.";
                StatusChanged?.Invoke("Deep read cancelled, administrator permission was not granted");
                return;
            }
            _lastRead = result;
            RenderDeepRead(result);
            CopyReadButton.Visibility = Visibility.Visible;
            DeepReadStatus.SetResourceReference(TextBlock.ForegroundProperty, result.Elevated ? "Green" : "Amber");
            DeepReadStatus.Text = result.Elevated
                ? "✓ Read with administrator rights."
                : "Read completed without elevation, some values are blank.";
            StatusChanged?.Invoke($"{MainWindow.AppVersion} · full system read complete");
        }
        finally { DeepReadButton.IsEnabled = true; }
    }

    private void OnCopyRead(object sender, RoutedEventArgs e)
    {
        if (_lastRead is null) return;
        MainWindow.TrySetClipboard(ProToolsService.ToText(_lastRead));
        StatusChanged?.Invoke("Copied the admin report to the clipboard");
    }

    private void RenderDeepRead(DeepReadResult r)
    {
        var p = DeepReadPanel;
        p.Children.Clear();

        // --- Storage & SMART ---
        p.Children.Add(Header("Storage & SMART"));
        if (r.Disks.Count == 0) p.Children.Add(Muted("No physical disks reported."));
        foreach (var d in r.Disks)
        {
            p.Children.Add(Strong(d.Model));
            if (d.PredictFailure == true) p.Children.Add(Flag("⚠ SMART predicts this drive may fail, back up now."));
            var bits = new List<string>
            {
                d.WearPercent is { } w ? $"{w}% life used{(string.IsNullOrEmpty(d.WearSource) ? "" : $" · {d.WearSource}")}" : "wear: not reported",
                d.TemperatureC is { } t ? $"{t} °C" : "temp: not reported",
                d.PowerOnHours is { } h ? $"{h:n0} power-on hours" : "power-on hours: not reported",
            };
            p.Children.Add(Muted("    " + string.Join("  ·  ", bits)));
            if (d.Smart is { Count: > 0 })
            {
                foreach (var a in d.Smart.Where(a => a.Critical))
                    p.Children.Add(Flag($"    ! {a.Name} (0x{a.Id:X2}) = {a.Raw}"));
                p.Children.Add(Muted("    SMART: " + string.Join("  ·  ",
                    d.Smart.Select(a => $"{a.Name.Split(' ')[0]}=0x{a.Id:X2}:{a.Raw}").Take(12)) + (d.Smart.Count > 12 ? " …" : "")));
            }
        }

        // --- NVMe ---
        if (r.Nvme.Count > 0)
        {
            p.Children.Add(Header("NVMe health"));
            foreach (var n in r.Nvme)
            {
                p.Children.Add(Strong(n.Model));
                var bits = new List<string>
                {
                    n.SparePercent is { } s ? $"{s}% spare" : "spare: n/a",
                    n.TemperatureC is { } t ? $"{t} °C" : "temp: n/a",
                    n.PowerOnHours is { } h ? $"{h:n0} power-on hours" : "hours: n/a",
                    n.MediaErrors is { } m ? $"{m} media errors" : "",
                };
                p.Children.Add(Muted("    " + string.Join("  ·  ", bits.Where(b => b.Length > 0))));
                if (n.CriticalWarning is { } cw && cw != 0) p.Children.Add(Flag($"    ! NVMe critical warning: 0x{cw:X2}"));
            }
        }

        // --- Thermal ---
        if (r.Thermals.Count > 0)
        {
            p.Children.Add(Header("Thermal sensors"));
            foreach (var z in r.Thermals) p.Children.Add(Muted($"    {z.Name}  ·  {z.TemperatureC} °C"));
        }

        // --- Security posture ---
        if (r.Security is { } sec)
        {
            p.Children.Add(Header("Security posture"));
            p.Children.Add(Muted($"    TPM: {(sec.TpmPresent ? $"present{(sec.TpmEnabled == true ? ", enabled" : "")}{(string.IsNullOrEmpty(sec.TpmSpec) ? "" : $" · spec {sec.TpmSpec}")}" : "not present")}"));
            p.Children.Add(Muted($"    Secure Boot: {sec.SecureBoot}"));
            foreach (var v in sec.BitLocker)
                p.Children.Add(Muted($"    BitLocker {v.Drive} {v.Protection}{(v.EncryptionPercent is { } pc ? $" ({pc}%)" : "")}"));
        }

        // --- Servicing ---
        if (r.Servicing is { } sv)
        {
            p.Children.Add(Header("Servicing"));
            if (!string.IsNullOrEmpty(sv.OsCaption)) p.Children.Add(Muted($"    {sv.OsCaption}  ·  {sv.OsBuild}"));
            p.Children.Add(Muted($"    Activation: {sv.Activation}"));
            if (sv.RebootPending) p.Children.Add(Flag($"    ⚠ Reboot pending, {string.Join(", ", sv.RebootReasons)}"));
            else p.Children.Add(Muted("    Reboot pending: no"));
            if (sv.RecentHotfixes.Count > 0)
                p.Children.Add(Muted("    Recent hotfixes: " + string.Join(", ", sv.RecentHotfixes.Select(h => h.Id))));
        }

        // --- Diagnostics ---
        if (r.ProblemDevices.Count > 0)
        {
            p.Children.Add(Header("Problem devices"));
            foreach (var d in r.ProblemDevices) p.Children.Add(Flag($"    ⚠ {d.Name} [{d.DeviceClass}], {d.ErrorText}"));
        }
        if (r.BadServices.Count > 0)
        {
            p.Children.Add(Header($"Auto-start services not running ({r.BadServices.Count})"));
            foreach (var b in r.BadServices.Take(20)) p.Children.Add(Muted($"    {b.DisplayName} ({b.Name}), {b.State}"));
            p.Children.Add(Muted("    (some may be delayed-start or trigger-start and are fine)"));
        }
        if (r.Network.Count > 0)
        {
            p.Children.Add(Header("Network"));
            foreach (var n in r.Network)
            {
                p.Children.Add(Strong("    " + n.Description));
                p.Children.Add(Muted($"        IP {string.Join(", ", n.IpAddresses)}  ·  gw {n.Gateway}  ·  DNS {string.Join(", ", n.DnsServers)}  ·  DHCP {(n.DhcpEnabled ? "on" : "off")}"));
            }
        }

        // --- Volumes / firewall / RDP / top processes ---
        if (r.Volumes.Count > 0)
        {
            p.Children.Add(Header("Volumes"));
            foreach (var v in r.Volumes)
            {
                var line = $"    {v.Drive}  {v.FreeBytes / 1073741824.0:0.0} GB free of {v.TotalBytes / 1073741824.0:0.0} GB";
                if (v.Low) p.Children.Add(Flag(line + "   ← low (<10%)")); else p.Children.Add(Muted(line));
            }
        }
        if (r.Firewall is { } fw)
        {
            p.Children.Add(Header("Firewall & remote access"));
            p.Children.Add(Muted($"    Firewall, Domain: {FwTxt(fw.Domain)}  ·  Private: {FwTxt(fw.Private)}  ·  Public: {FwTxt(fw.Public)}"));
        }
        if (r.Rdp is { } rd)
            p.Children.Add(Muted($"    Remote Desktop: {(rd.Enabled ? $"enabled (NLA {(rd.NlaRequired ? "required" : "off")})" : "disabled")}"));
        if (r.TopProcesses.Count > 0)
        {
            p.Children.Add(Header("Top processes (by memory)"));
            foreach (var pr in r.TopProcesses) p.Children.Add(Muted($"    {pr.Name}  ·  {pr.WorkingSetMb:n0} MB"));
        }

        DeepReadCard.Visibility = Visibility.Visible;
    }

    private static TextBlock Header(string t) { var tb = new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 10, 0, 4) }; tb.SetResourceReference(TextBlock.ForegroundProperty, "Text"); return tb; }
    private static TextBlock Strong(string t) { var tb = new TextBlock { Text = t, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }; tb.SetResourceReference(TextBlock.ForegroundProperty, "Text"); return tb; }
    private static TextBlock Muted(string t) { var tb = new TextBlock { Text = t, FontSize = 12, TextWrapping = TextWrapping.Wrap }; tb.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return tb; }
    private static TextBlock Flag(string t) { var tb = new TextBlock { Text = t, FontSize = 12, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold }; tb.SetResourceReference(TextBlock.ForegroundProperty, "Amber"); return tb; }
    private static string FwTxt(bool? b) => b is null ? "?" : b.Value ? "on" : "off";

    // ---- Section 2: maintenance/repair scripts ----

    private void OnGenerateSelected(object sender, RoutedEventArgs e)
    {
        var ids = _fixRows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (ids.Count == 0)
        {
            MessageBox.Show(Owner, "Tick at least one action to put in the script.",
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var vol = string.IsNullOrWhiteSpace(VolumeBox.Text) ? "C:" : VolumeBox.Text.Trim();
        var res = ProToolsService.WriteFixScript(ids, DownloadService.DownloadsFolder(), vol, AutoDeleteCheck.IsChecked == true);
        if (!res.Ok)
        {
            MessageBox.Show(Owner, "Could not write the script.\n\n" + res.Error,
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _savedRepairScript = res.ScriptPath;
        RepairRunCommandBox.Text = MainWindow.RunCommandFor(res.ScriptPath);
        RepairSavedText.Text =
            $"Saved {ids.Count} action(s) to {System.IO.Path.GetFileName(res.ScriptPath)} in your Downloads folder" +
            (AutoDeleteCheck.IsChecked == true ? " (it will delete itself when finished)" : "") + ". " +
            "Open it to review first, or press Run. It still asks you to type Y as Administrator before it changes anything.";
        RepairSavedBar.Visibility = Visibility.Visible;
        StatusChanged?.Invoke($"{MainWindow.AppVersion} · script saved · {System.IO.Path.GetFileName(res.ScriptPath)}");
    }

    private void OnOpenRepairFolder(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_savedRepairScript)) MainWindow.RevealInExplorer(_savedRepairScript);
    }

    private void OnOpenRepairScript(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_savedRepairScript)) return;
        try { Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("notepad.exe"), $"\"{_savedRepairScript}\"") { UseShellExecute = true }); }
        catch { MainWindow.OpenExternal(_savedRepairScript); }
    }

    private void OnCopyRepairRunCommand(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_savedRepairScript)) return;
        MainWindow.TrySetClipboard(MainWindow.RunCommandFor(_savedRepairScript));
        StatusChanged?.Invoke("Copied the run-command to the clipboard");
    }

    /// <summary>Launch the generated script elevated, the one action here that runs rather than only
    /// writes, gated by this confirmation AND the script's own admin check + typed-Y.</summary>
    private void OnRunRepairScript(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_savedRepairScript) || !System.IO.File.Exists(_savedRepairScript))
        {
            MessageBox.Show(Owner, "Generate a script first.", Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ok = MessageBox.Show(Owner,
            "This opens the script in an Administrator PowerShell window and starts it.\n\n" +
            "Venquery normally only writes files, this is the one action that launches a script. It uses " +
            "Windows' own tools and still waits for you to type Y before doing anything.\n\nOpen it and run it now?",
            "Run maintenance script", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("powershell.exe"),
                $"-NoProfile -ExecutionPolicy Bypass -File \"{_savedRepairScript}\"")
            { UseShellExecute = true, Verb = "runas" });
            StatusChanged?.Invoke("Launched the script (elevated), confirm inside the PowerShell window");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            StatusChanged?.Invoke("Run cancelled, administrator permission was not granted");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Owner, "Could not launch the script.\n\n" + ex.Message, Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
