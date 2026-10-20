using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Venquery.Services;

namespace Venquery.Controls;

/// <summary>The "Restore from a backup" tab of Backup &amp; Restore. Picks a backup, shows its parts with a tick box
/// each (drivers unticked by default), then restores the ticked parts: documents and portable apps are copied
/// (never overwriting, with a receipt when that option is on), installed apps and drivers are handed to the
/// backup's own guarded scripts after a confirmation.</summary>
public partial class RestoreView : UserControl
{
    public event Action<string>? StatusChanged;
    public Func<string, bool, MainWindow.TaskScope>? BeginTask { get; set; }
    /// <summary>Whether to verify copies and write a receipt (the app-wide backup setting).</summary>
    public Func<bool>? WriteReceipts { get; set; }

    private BackupInventory? _inventory;
    private readonly Dictionary<RestorePartKind, CheckBox> _checks = new();
    private ComboBox? _docsMode;
    private string _docsFolder = "";
    private string _portableFolder = RestoreService.DefaultPortableTarget();
    private TextBlock? _portableText;
    private bool _busy;

    public RestoreView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshFoundAsync();
    }

    private Window? Owner => Window.GetWindow(this);

    // ---- choose a backup ----------------------------------------------------------------------------------

    public async Task RefreshFoundAsync()
    {
        IReadOnlyList<string> found;
        try { found = await Task.Run(RestoreService.FindBackups); } catch { found = Array.Empty<string>(); }
        FoundBar.Children.Clear();
        foreach (var f in found.Take(8))
        {
            var label = Path.GetFileName(f.TrimEnd('\\'));
            string when = ""; try { when = Directory.GetLastWriteTime(f).ToString("yyyy-MM-dd"); } catch { }
            var b = new Button { Style = (Style)FindResource("ChromeButton"), Content = $"{label}  ·  {when}", Tag = f, Margin = new Thickness(0, 0, 6, 6), ToolTip = f };
            b.Click += (_, _) => LoadBackup(f);
            FoundBar.Children.Add(b);
        }
        FoundLabel.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnChooseBackup(object sender, RoutedEventArgs e)
    {
        var picked = FolderPicker.Pick(Owner, "Choose the backup folder (for example \"Venquery Backup - your PC\")", _inventory?.Root);
        if (!string.IsNullOrWhiteSpace(picked)) LoadBackup(picked!);
    }

    internal void LoadBackup(string folder)
    {
        _inventory = RestoreService.Inspect(folder);
        BackupPathText.Text = folder;
        BackupPathText.ToolTip = folder;
        BuildParts();
    }

    private void BuildParts()
    {
        PartsPanel.Children.Clear();
        _checks.Clear();
        _docsMode = null; _portableText = null;
        var inv = _inventory!;
        PartsLabel.Visibility = Visibility.Visible;
        if (inv.IsEmpty)
        {
            PartsPanel.Children.Add(new TextBlock
            {
                Text = "That folder doesn't look like a Venquery backup (no apps list, Portable Apps, Documents or Drivers inside).",
                Foreground = (System.Windows.Media.Brush)FindResource("Amber"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            });
            RestoreButton.IsEnabled = false;
            return;
        }
        foreach (var part in inv.Parts)
        {
            var cb = new CheckBox { Style = (Style)FindResource("FluentCheck"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
            // Drivers are optional: never ticked for you (they're installed as Administrator).
            cb.IsChecked = part.Available && part.Kind != RestorePartKind.Drivers;
            cb.IsEnabled = part.Available;
            cb.Checked += (_, _) => UpdateRestoreEnabled();
            cb.Unchecked += (_, _) => UpdateRestoreEnabled();
            System.Windows.Automation.AutomationProperties.SetAutomationId(cb, "Restore" + part.Kind);
            _checks[part.Kind] = cb;

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = part.Title + (part.Kind == RestorePartKind.Drivers ? " (optional)" : ""), FontSize = 13, FontWeight = FontWeights.SemiBold });
            var summary = new TextBlock { Text = part.Summary, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            summary.SetResourceReference(TextBlock.ForegroundProperty, part.Available ? "Muted" : "Dim");
            text.Children.Add(summary);

            if (part.Kind == RestorePartKind.Documents && part.Available)
            {
                _docsMode = new ComboBox { Style = (Style)FindResource("FluentCombo"), ItemContainerStyle = (Style)FindResource("FluentComboItem"), Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
                _docsMode.Items.Add(new ComboBoxItem { Content = "Into my Pictures, Videos, Music and Documents folders" });
                _docsMode.Items.Add(new ComboBoxItem { Content = "Into one folder I choose..." });
                _docsMode.SelectedIndex = 0;
                _docsMode.SelectionChanged += (_, _) =>
                {
                    if (_docsMode.SelectedIndex != 1) return;
                    var f = FolderPicker.Pick(Owner, "Restore the documents into...", _docsFolder);
                    if (string.IsNullOrWhiteSpace(f) || MainWindow.IsSystemLocation(Path.GetFullPath(f!))) { _docsMode.SelectedIndex = 0; return; }
                    _docsFolder = f!;
                    ((ComboBoxItem)_docsMode.Items[1]).Content = "Into " + f;
                };
                text.Children.Add(_docsMode);
            }
            if (part.Kind == RestorePartKind.PortableApps && part.Available)
            {
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
                var change = new Button { Style = (Style)FindResource("ChromeButton"), Content = "Change...", Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(change, Dock.Right);
                _portableText = new TextBlock { Text = "To " + _portableFolder, FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                change.Click += (_, _) =>
                {
                    var f = FolderPicker.Pick(Owner, "Restore the portable apps into...", Directory.Exists(_portableFolder) ? _portableFolder : null);
                    if (string.IsNullOrWhiteSpace(f) || MainWindow.IsSystemLocation(Path.GetFullPath(f!))) return;
                    _portableFolder = f!;
                    _portableText!.Text = "To " + _portableFolder;
                };
                row.Children.Add(change);
                row.Children.Add(_portableText);
                text.Children.Add(row);
            }

            var line = new DockPanel { LastChildFill = true };
            line.Children.Add(cb);
            line.Children.Add(text);
            var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8), Child = line };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            PartsPanel.Children.Add(card);
        }
        UpdateRestoreEnabled();
    }

    private bool Ticked(RestorePartKind k) => _checks.TryGetValue(k, out var c) && c.IsChecked == true && c.IsEnabled;

    private void UpdateRestoreEnabled()
        => RestoreButton.IsEnabled = !_busy && _checks.Values.Any(c => c.IsChecked == true && c.IsEnabled);

    // ---- restore ------------------------------------------------------------------------------------------

    private void Log(string line)
    {
        RunLog.AppendText(line + Environment.NewLine);
        RunLog.ScrollToEnd();
        AppLog.Write("restore: " + line);
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        var inv = _inventory;
        if (inv is null || _busy) return;
        var plan = new List<string>();
        if (Ticked(RestorePartKind.Documents)) plan.Add("copy the documents back" + (_docsMode?.SelectedIndex == 1 ? " into " + _docsFolder : " into your Pictures/Videos/Music/Documents folders"));
        if (Ticked(RestorePartKind.PortableApps)) plan.Add("copy the portable apps to " + _portableFolder);
        if (Ticked(RestorePartKind.Apps)) plan.Add("open the apps restore script (it lists the apps and waits for you to type Y)");
        if (Ticked(RestorePartKind.Drivers)) plan.Add("open the driver restore script as Administrator (it checks this is the same PC, then waits for Y)");
        if (MessageBox.Show(Owner, "Restore will:\n\n- " + string.Join("\n- ", plan) + "\n\nNothing is overwritten. Continue?",
                "Restore from backup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;

        _busy = true;
        UpdateRestoreEnabled();
        ChooseBackupButton.IsEnabled = false;
        RunLabel.Visibility = RunCard.Visibility = Visibility.Visible;
        RunLog.Clear();
        Log($"Restoring from {inv.Root}");
        var receipts = WriteReceipts?.Invoke() ?? true;
        try
        {
            var docs = inv.Parts.FirstOrDefault(p => p.Kind == RestorePartKind.Documents);
            if (docs is not null && Ticked(RestorePartKind.Documents))
            {
                var t = BeginTask?.Invoke("Restoring documents...", true);
                var receipt = receipts ? new BackupReceipt() : null;
                var r = await RestoreService.RestoreDocumentsAsync(docs, _docsMode?.SelectedIndex == 1 ? _docsFolder : null,
                    new Progress<(int done, int total, string current)>(p => t?.Report(p.total > 0 ? 100.0 * p.done / p.total : 100, $"Restoring {p.current}")), default, receipt);
                t?.Done();
                Log($"Documents: {r.Copied} item(s) copied back" + (r.Skipped > 0 ? $", {r.Skipped} skipped" : "") + (r.DestFolder.Length > 0 ? $" (last into {r.DestFolder})" : ""));
                foreach (var n in r.Notes.Take(10)) Log("  " + n);
                if (receipt is not null && receipt.Files.Count > 0)
                {
                    var receiptDir = Path.Combine(_docsMode?.SelectedIndex == 1 ? _docsFolder : Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                        $"{RestoreService.RestoredFolderPrefix} {DateTime.Now:yyyy-MM-dd}");
                    var (ok, bad, path) = receipt.WriteAndVerify(receiptDir, "Restored documents");
                    Log(bad == 0 ? $"  all {ok} file(s) checked, receipt: {path}" : $"  {bad} file(s) need a look, see {path}");
                }
            }
            var portable = inv.Parts.FirstOrDefault(p => p.Kind == RestorePartKind.PortableApps);
            if (portable is not null && Ticked(RestorePartKind.PortableApps))
            {
                var t = BeginTask?.Invoke("Restoring portable apps...", true);
                var receipt = receipts ? new BackupReceipt() : null;
                var r = await RestoreService.RestorePortableAsync(portable, _portableFolder,
                    new Progress<(int done, int total, string current)>(p => t?.Report(p.total > 0 ? 100.0 * p.done / p.total : 100, $"Copying {p.current}")), default, receipt);
                t?.Done();
                Log(r.Ok ? $"Portable apps: {r.Copied} app folder(s) copied to {_portableFolder}" + (r.Skipped > 0 ? $", {r.Skipped} skipped" : "") : "Portable apps: failed, " + r.Error);
                if (r.Ok && receipt is not null && receipt.Files.Count > 0)
                {
                    var (ok, bad, path) = receipt.WriteAndVerify(_portableFolder, "Restored portable apps");
                    Log(bad == 0 ? $"  all {ok} file(s) checked, receipt: {path}" : $"  {bad} file(s) need a look, see {path}");
                }
            }
            var apps = inv.Parts.FirstOrDefault(p => p.Kind == RestorePartKind.Apps);
            if (apps is not null && Ticked(RestorePartKind.Apps)) LaunchScript(apps.ScriptPath, elevated: false, "Installed apps");
            var drivers = inv.Parts.FirstOrDefault(p => p.Kind == RestorePartKind.Drivers);
            if (drivers is not null && Ticked(RestorePartKind.Drivers)) LaunchScript(drivers.ScriptPath, elevated: true, "Drivers");
            Log("Done.");
            StatusChanged?.Invoke($"{MainWindow.AppVersion} · restore finished from {Path.GetFileName(inv.Root.TrimEnd('\\'))}");
        }
        catch (Exception ex)
        {
            Log("[error] " + ex.Message);
            AppLog.WriteCrash(ex, "restore");
        }
        finally
        {
            _busy = false;
            ChooseBackupButton.IsEnabled = true;
            UpdateRestoreEnabled();
        }
    }

    /// <summary>Opens one of the backup's own guarded scripts in a visible PowerShell window. The scripts confirm with
    /// a typed Y before doing anything; the driver script is hardware-locked and runs as Administrator (UAC).</summary>
    private void LaunchScript(string script, bool elevated, string what)
    {
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script)) { Log($"{what}: the restore script is missing, skipped."); return; }
        try
        {
            var psi = new ProcessStartInfo(SystemTools.Exe("powershell.exe"), $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(script)! };
            if (elevated) psi.Verb = "runas";
            Process.Start(psi);
            Log($"{what}: opened its restore script in a PowerShell window{(elevated ? " (as Administrator)" : "")}. Follow it there.");
        }
        catch (System.ComponentModel.Win32Exception) { Log($"{what}: Administrator permission wasn't given, skipped. You can run {script} later."); }
        catch (Exception ex) { Log($"{what}: couldn't open the script, {ex.Message}"); }
    }
}
