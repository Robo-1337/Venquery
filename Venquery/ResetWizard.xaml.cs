using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Venquery.Services;

namespace Venquery;

// The Prepare-for-Reset wizard (modal). Backup-only: it rounds up installed apps (winget), portable apps,
// loose documents and drivers into ONE parent folder named after this PC, with a subfolder per option.
// Copy work (apps manifest, portable, documents) Venquery does directly; the driver export is the one
// elevated step (it launches its own guarded, copy-only script). Excluded from the Legacy build (uses
// WingetService, which Legacy doesn't compile), the launcher is hidden there.
public partial class ResetWizard : Window
{
    // A generic tickable row for the portable/documents checklist steps.
    public sealed class WizardItem : INotifyPropertyChanged
    {
        public string Name { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string SourcePath { get; init; } = "";
        private bool _isSelected = true;
        public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private string _destBase = "";
    private string _model = "";
    private bool _onChoose = true;
    private readonly List<string> _screens = new();   // detail screens + "review", built when leaving Choose
    private int _screenIdx;
    private bool _done;
    private string _finalFolder = "";

    private readonly System.Collections.ObjectModel.ObservableCollection<WizardItem> _portableItems = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<WizardItem> _docItems = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<WizardItem> _appItems = new();
    private bool _appsScanned;
    private WingetExportResult? _wingetExport;   // cached so we don't run winget twice (checklist + Run)

    /// <summary>Verify the copied apps/documents and write a "Backup receipt" (the app-wide setting).</summary>
    public bool WriteReceipts { get; set; } = true;

    public ResetWizard()
    {
        InitializeComponent();
        _destBase = ResolveBase(out var usedFallback);
        RefreshDestText(usedFallback);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (Owner is MainWindow owner) TitleBarTheme.Apply(this, owner.CurrentThemeIsDark);
        UpdateChrome();
    }

    // ---- destination ----
    private static string ExeDir()
    {
        try { var p = Environment.ProcessPath; return string.IsNullOrEmpty(p) ? "" : Path.GetDirectoryName(p) ?? ""; }
        catch { return ""; }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, ".venq-write-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // Default to the folder Venquery is running from (so a USB/Desktop copy backs up right there); fall back
    // to Downloads when that folder is read-only (e.g. Program Files, or a read-only stick).
    private static string ResolveBase(out bool usedFallback)
    {
        var exe = ExeDir();
        if (!string.IsNullOrEmpty(exe) && IsWritable(exe)) { usedFallback = false; return exe; }
        usedFallback = true;
        return DownloadService.DownloadsFolder();
    }

    private void RefreshDestText(bool usedFallback)
    {
        DestText.Text = _destBase;
        DestHint.Text = usedFallback
            ? "Venquery's own folder isn't writable, so this defaults to your Downloads folder. Change it to a USB stick if you like."
            : "This is the folder Venquery is running from. Change it to a USB stick if you like.";
    }

    private void OnChangeDest(object sender, RoutedEventArgs e)
    {
        var picked = FolderPicker.Pick(this, "Choose where to save the backup", _destBase);
        if (!string.IsNullOrEmpty(picked))
        {
            if (MainWindow.IsSystemLocation(picked!))
            {
                MessageBox.Show(this, "Please pick a folder outside Windows and Program Files.", Edition.Current.DisplayName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!IsWritable(picked!))
            {
                MessageBox.Show(this, "That folder can't be written to. Pick another.", Edition.Current.DisplayName,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _destBase = picked!;
            DestText.Text = _destBase;
            DestHint.Text = "Custom folder.";
        }
    }

    // ---- navigation ----
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_onChoose) return;
        if (_screenIdx == 0) { _onChoose = true; }
        else _screenIdx--;
        ShowCurrent();
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (_done) { if (!string.IsNullOrEmpty(_finalFolder)) MainWindow.RevealInExplorer(_finalFolder); Close(); return; }

        if (_onChoose)
        {
            if (!(ChkApps.IsChecked == true || ChkPortable.IsChecked == true || ChkDocuments.IsChecked == true || ChkDrivers.IsChecked == true))
            {
                MessageBox.Show(this, "Tick at least one thing to back up.", Edition.Current.DisplayName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _screens.Clear();
            if (ChkApps.IsChecked == true) _screens.Add("apps");
            if (ChkPortable.IsChecked == true) _screens.Add("portable");
            if (ChkDocuments.IsChecked == true) _screens.Add("documents");
            _screens.Add("review");
            _onChoose = false;
            _screenIdx = 0;
            ShowCurrent();
            return;
        }

        if (_screens[_screenIdx] == "review") { await RunBackupAsync(); return; }
        _screenIdx++;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        ChoosePanel.Visibility = _onChoose ? Visibility.Visible : Visibility.Collapsed;
        ChecklistPanel.Visibility = Visibility.Collapsed;
        WingetMissingPanel.Visibility = Visibility.Collapsed;
        ReviewPanel.Visibility = Visibility.Collapsed;
        RunPanel.Visibility = Visibility.Collapsed;

        if (!_onChoose)
        {
            var s = _screens[_screenIdx];
            if (s == "review") ShowReview();
            else if (s == "apps") ShowAppsStep();
            else ShowChecklist(s);
        }
        UpdateChrome();
    }

    private void OnChoiceChanged(object sender, RoutedEventArgs e) { if (StepCaption is not null && _onChoose) UpdateChrome(); }

    private void UpdateChrome()
    {
        BackButton.Visibility = _onChoose || _done ? Visibility.Collapsed : Visibility.Visible;
        if (_done) { NextButton.Content = "Open folder"; CancelButton.Content = "Close"; StepCaption.Text = "Finished"; return; }
        var onReview = !_onChoose && _screens.Count > 0 && _screens[_screenIdx] == "review";
        NextButton.Content = onReview ? "Finish & Run" : "Next";
        // Choose + detail screens + review. On Choose the screens aren't built yet, so count what's ticked
        // (the first step used to read "Step 1 of 1").
        int total = _onChoose
            ? 2 + new[] { ChkApps, ChkPortable, ChkDocuments }.Count(c => c?.IsChecked == true)
            : 1 + _screens.Count;
        int cur = _onChoose ? 1 : 2 + _screenIdx;
        StepCaption.Text = $"Step {cur} of {total}";
    }

    // ---- checklist steps ----
    // Drives the user ticked on each step. Nothing is pre-ticked: no drive (C: included) is scanned until
    // the user picks it and presses Scan.
    private readonly HashSet<string> _portableDrives = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _docDrives = new(StringComparer.OrdinalIgnoreCase);
    // Specific folders added on each step (this wizard run only).
    private readonly List<string> _portableFolders = new();
    private readonly List<string> _docFolders = new();
    private List<string> CurrentFolders => _checklistWhich == "portable" ? _portableFolders : _docFolders;
    private string _checklistWhich = "";
    private bool _checklistScanning;

    private void ShowChecklist(string which)
    {
        ChecklistPanel.Visibility = Visibility.Visible;
        _checklistWhich = which;
        var isPortable = which == "portable";
        ChecklistTitle.Text = isPortable ? "Pick the portable apps to keep" : "Pick the documents to keep";
        ChecklistItems.ItemsSource = isPortable ? _portableItems : _docItems;
        ChecklistScopePanel.Visibility = Visibility.Visible;
        ChecklistScopeHint.Text = isPortable
            ? "Pick the drives to search for portable apps (or add specific folders), then press Scan."
            : "Pick the drives to search (or add specific folders), then press Scan. The Windows drive (C:) means your personal folders (Desktop, Documents, Pictures, Downloads\u2026) plus its root, which is what a reset wipes.";
        BuildChecklistDrives(isPortable ? _portableDrives : _docDrives, !isPortable);
        BuildChecklistFolders();
        UpdateChecklistCount();
    }

    private void BuildChecklistDrives(HashSet<string> picked, bool documents)
    {
        ChecklistDriveBar.Children.Clear();
        foreach (var d in DriveList.Ready())
        {
            var cb = new CheckBox
            {
                Style = (Style)FindResource("PillCheck"),
                Content = d.Caption,
                Tag = d,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = picked.Contains(d.Root),
            };
            if (d.IsOsDrive && documents)
                cb.ToolTip = "Windows drive: your personal folders (Desktop, Documents, Pictures, Videos, Music, Downloads, Public) plus loose files in the drive root";
            cb.Checked += (_, _) => { picked.Add(d.Root); UpdateChecklistScanEnabled(); };
            cb.Unchecked += (_, _) => { picked.Remove(d.Root); UpdateChecklistScanEnabled(); };
            ChecklistDriveBar.Children.Add(cb);
        }
        UpdateChecklistScanEnabled();
    }

    private void UpdateChecklistScanEnabled()
    {
        var picked = _checklistWhich == "portable" ? _portableDrives : _docDrives;
        ChecklistScanButton.IsEnabled = !_checklistScanning && (picked.Count > 0 || CurrentFolders.Any(Directory.Exists));
        ChecklistDriveBar.IsEnabled = ChecklistFolderBar.IsEnabled = ChecklistAddFolder.IsEnabled = !_checklistScanning;
    }

    private void BuildChecklistFolders()
    {
        ChecklistFolderBar.Children.Clear();
        var folders = CurrentFolders;
        foreach (var f in folders.ToList())
        {
            var name = Path.GetFileName(f.TrimEnd('\\'));
            var cb = new CheckBox
            {
                Style = (Style)FindResource("PillCheck"),
                Content = (string.IsNullOrEmpty(name) ? f : name),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = true,
                ToolTip = f + "\nUntick to remove it.",
            };
            var path = f;
            cb.Unchecked += (_, _) => { folders.Remove(path); BuildChecklistFolders(); UpdateChecklistScanEnabled(); };
            ChecklistFolderBar.Children.Add(cb);
        }
        UpdateChecklistScanEnabled();
    }

    private void OnChecklistAddFolder(object sender, RoutedEventArgs e)
    {
        var picked = FolderPicker.Pick(this, "Choose a folder to search", CurrentFolders.LastOrDefault());
        if (string.IsNullOrWhiteSpace(picked)) return;
        if (!CurrentFolders.Any(x => string.Equals(x, picked, StringComparison.OrdinalIgnoreCase))) CurrentFolders.Add(picked!);
        BuildChecklistFolders();
    }

    private async void OnChecklistScan(object sender, RoutedEventArgs e)
    {
        if (_checklistScanning) return;
        var which = _checklistWhich;
        var isPortable = which == "portable";
        var picked = (isPortable ? _portableDrives : _docDrives).ToList();
        var folders = CurrentFolders.Where(Directory.Exists).ToList();
        if (picked.Count == 0 && folders.Count == 0) return;

        _checklistScanning = true;
        UpdateChecklistScanEnabled();
        var items = isPortable ? _portableItems : _docItems;
        items.Clear();
        ChecklistProgress.Visibility = Visibility.Visible;
        ChecklistProgressText.Text = isPortable ? "Scanning for portable apps\u2026" : "Scanning for loose documents\u2026";
        NextButton.IsEnabled = false;
        try
        {
            if (isPortable)
            {
                var res = await PortableAppScanner.ScanAsync(picked.Concat(folders).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), PortableAppScanner.DefaultExcludes());
                foreach (var exe in res.Executables)
                    items.Add(new WizardItem { Name = exe.Name, Subtitle = exe.Folder, SourcePath = exe.Folder });
            }
            else
            {
                var res = await DocumentScanner.ScanAsync(DocumentScanner.RootsFor(picked).Concat(folders).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                foreach (var it in res.Items)
                    items.Add(new WizardItem { Name = it.Name, Subtitle = $"{Path.GetDirectoryName(it.FullPath)}  \u00b7  {(it.IsFolder ? "folder" : "file")}", SourcePath = it.FullPath });
            }
        }
        catch (Exception ex) { MessageBox.Show(this, "Scan failed: " + ex.Message, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _checklistScanning = false;
            ChecklistProgress.Visibility = Visibility.Collapsed;
            NextButton.IsEnabled = true;
            UpdateChecklistScanEnabled();
            UpdateChecklistCount();
        }
    }

    // ---- apps step (winget): a checklist of installed apps, or the "winget missing + install" panel ----
    private async void ShowAppsStep()
    {
        NextButton.IsEnabled = false;
        var haveWinget = await WingetService.IsInstalledAsync();
        NextButton.IsEnabled = true;
        // The user may have moved on while winget was being checked.
        if (_onChoose || _screens.Count <= _screenIdx || _screens[_screenIdx] != "apps") return;
        if (!haveWinget)
        {
            WingetMissingPanel.Visibility = Visibility.Visible;
            var (isLtsc, editionId, product) = WingetService.DetectEdition();
            EditionNote.Text = isLtsc
                ? $"This looks like a Windows LTSC / IoT Enterprise edition ({(string.IsNullOrWhiteSpace(product) ? editionId : product)}). Those ship without the Microsoft Store and App Installer, so winget and its packages aren't here by default."
                : "winget (Microsoft's App Installer) wasn't found. Some editions (LTSC / IoT Enterprise, Server) don't include it.";
            return;
        }

        ChecklistPanel.Visibility = Visibility.Visible;
        ChecklistScopePanel.Visibility = Visibility.Collapsed;
        ChecklistTitle.Text = "Pick the installed apps to back up";
        ChecklistItems.ItemsSource = _appItems;
        UpdateChecklistCount();
        if (_appsScanned) return;

        ChecklistProgress.Visibility = Visibility.Visible;
        ChecklistProgressText.Text = "Reading installed apps via winget (nothing is installed)…";
        NextButton.IsEnabled = false;
        try
        {
            var winget = new WingetService();
            _wingetExport = await winget.ExportAsync();
            if (_wingetExport.Ok)
            {
                foreach (var a in _wingetExport.Restorable)
                    _appItems.Add(new WizardItem { Name = a.Id, Subtitle = string.IsNullOrEmpty(a.Version) ? "winget package" : "winget · " + a.Version, SourcePath = a.Id });
            }
            else ChecklistProgressText.Text = "winget couldn't list apps: " + _wingetExport.Error;
            _appsScanned = true;
        }
        catch (Exception ex) { MessageBox.Show(this, "Reading apps failed: " + ex.Message, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            if (_appItems.Count > 0 || (_wingetExport?.Ok ?? false)) ChecklistProgress.Visibility = Visibility.Collapsed;
            NextButton.IsEnabled = true;
            UpdateChecklistCount();
        }
    }

    private void OnInstallWinget(object sender, RoutedEventArgs e)
    {
        var res = WingetService.WriteInstallScript(DownloadService.DownloadsFolder());
        if (!res.Ok) { MessageBox.Show(this, "Could not write the installer script.\n\n" + res.Error, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var ok = MessageBox.Show(this,
            "This opens a PowerShell window and runs the winget installer script.\n\n" +
            "It downloads winget and its packages from Microsoft's own endpoints and installs them for you " +
            "(it asks you to type Y first; no administrator needed). Open and run it now?",
            "Install winget", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (ok != MessageBoxResult.Yes)
        {
            WingetInstallStatus.Text = "Saved the installer to your Downloads folder (" + System.IO.Path.GetFileName(res.ScriptPath) + "). Run it, then reopen Venquery.";
            return;
        }
        try
        {
            // Per-user App Installer install needs no elevation, so this launches as the current user.
            WingetService.LaunchInstallScript(res.ScriptPath, code =>
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    if (await WingetService.IsInstalledAsync())
                    {
                        WingetInstallStatus.Text = "winget is installed.";
                        // Still on the apps step? Swap the "missing" panel for the app checklist.
                        if (IsLoaded && !_onChoose && _screens.Count > _screenIdx && _screens[_screenIdx] == "apps") ShowCurrent();
                    }
                    else WingetInstallStatus.Text = code == 2
                        ? "Install cancelled, nothing was changed."
                        : "winget didn't install. The details are in venquery-install-winget.log in your Downloads folder.";
                })));
            WingetInstallStatus.Text = "Launched the installer, follow the PowerShell window. This step updates when it finishes.";
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not launch the installer.\n\n" + ex.Message, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void UpdateChecklistCount()
    {
        var list = ChecklistItems.ItemsSource as System.Collections.IEnumerable;
        int total = 0, sel = 0;
        if (list is not null) foreach (WizardItem i in list) { total++; if (i.IsSelected) sel++; }
        ChecklistCount.Text = $"{sel} of {total} selected";
    }

    private void OnChecklistAll(object sender, RoutedEventArgs e) { SetAllChecklist(true); }
    private void OnChecklistClear(object sender, RoutedEventArgs e) { SetAllChecklist(false); }
    private void SetAllChecklist(bool on)
    {
        if (ChecklistItems.ItemsSource is System.Collections.IEnumerable list)
            foreach (WizardItem i in list) i.IsSelected = on;
        UpdateChecklistCount();
    }

    // ---- review ----
    private void ShowReview()
    {
        ReviewPanel.Visibility = Visibility.Visible;
        var parts = new List<string>();
        if (ChkApps.IsChecked == true)
            parts.Add(_wingetExport is null || !_wingetExport.Ok
                ? "• Installed apps → skipped (winget not available)"
                : $"• {_appItems.Count(i => i.IsSelected)} installed app(s) → Apps\\ (winget manifest + restore script)");
        if (ChkPortable.IsChecked == true) parts.Add($"• {_portableItems.Count(i => i.IsSelected)} portable app(s) → Portable Apps\\");
        if (ChkDocuments.IsChecked == true) parts.Add($"• {_docItems.Count(i => i.IsSelected)} document(s)/folder(s) → Documents\\");
        if (ChkDrivers.IsChecked == true) parts.Add("• Drivers → Drivers\\ (elevated export; installs nothing)");
        ReviewText.Text = "This will create one folder and fill these subfolders:\n\n" + string.Join("\n", parts) +
                          $"\n\nInto: {_destBase}\\Venquery Backup - <this PC>";
        ReviewNote.Text = ChkDrivers.IsChecked == true
            ? "The driver export opens in its own Administrator window (UAC + a typed Y). Everything else Venquery copies directly. Nothing is installed."
            : "Everything is copied directly. Nothing is installed or run.";
    }

    // ---- run ----
    private void Log(string line) { RunLog.AppendText(line + Environment.NewLine); RunLog.ScrollToEnd(); }

    private async Task RunBackupAsync()
    {
        _onChoose = false;
        ChoosePanel.Visibility = ChecklistPanel.Visibility = ReviewPanel.Visibility = Visibility.Collapsed;
        RunPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StepCaption.Text = "Backing up…";

        if (string.IsNullOrEmpty(_model))
        {
            // Outside the try below, so it must not throw: a WMI hiccup here used to leave the wizard stuck with
            // Next/Cancel disabled. The PC name is a fine folder name if the model can't be read.
            try { _model = await Task.Run(HardwareScanner.RawMachineIdentity); } catch { _model = ""; }
        }
        var safe = SafeName(string.IsNullOrWhiteSpace(_model) ? Environment.MachineName : _model);
        var parent = UniqueDir(Path.Combine(_destBase, "Venquery Backup - " + safe));

        try
        {
            Directory.CreateDirectory(parent);
            _finalFolder = parent;
            Log("Backup folder:");
            Log("  " + parent);
            Log("");

            if (ChkApps.IsChecked == true) await BackupAppsAsync(parent);
            if (ChkPortable.IsChecked == true) await BackupCopyAsync(parent, "Portable Apps", _portableItems, "portable app");
            if (ChkDocuments.IsChecked == true) await BackupCopyAsync(parent, "Documents", _docItems, "document");
            if (ChkDrivers.IsChecked == true) BackupDrivers(parent);

            Log("");
            Log("Done. Your backup is in the folder above, move it to a USB stick to keep it safe.");
            RunTitle.Text = "Backup complete";
        }
        catch (Exception ex) { Log("");  Log("[error] " + ex.Message); RunTitle.Text = "Finished with errors"; }
        finally
        {
            RunProgress.IsIndeterminate = false;
            RunProgress.Value = 100;
            _done = true;
            NextButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            UpdateChrome();
        }
    }

    private async Task BackupAppsAsync(string parent)
    {
        // Uses the export cached on the apps step, and only the apps the user ticked there. If winget was
        // missing (or the step wasn't reached), there's no export, so apps are skipped.
        if (_wingetExport is null || !_wingetExport.Ok) { Log("Installed apps: winget unavailable, skipped."); return; }
        var ids = _appItems.Where(i => i.IsSelected).Select(i => i.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) { Log("Installed apps: none ticked, skipped."); return; }
        Log($"Installed apps: writing {ids.Count} app(s)…");
        try
        {
            var machine = await Task.Run(HardwareScanner.RawMachineIdentity);
            var winget = new WingetService();
            var res = winget.WriteBackupFiles(_wingetExport.RawJson, ids, _wingetExport.Manual, Path.Combine(parent, "Apps"), machine, false);
            Log(res.Ok ? $"  → Apps\\  ({res.Count} app(s) + restore script)" : "  failed: " + res.Error);
        }
        catch (Exception ex) { Log("  failed: " + ex.Message); }
    }

    private async Task BackupCopyAsync(string parent, string sub, IEnumerable<WizardItem> items, string label)
    {
        var sources = items.Where(i => i.IsSelected).Select(i => i.SourcePath).ToList();
        if (sources.Count == 0) { Log($"{sub}: nothing ticked, skipped."); return; }
        Log($"{sub}: copying {sources.Count} {label}(s)…");
        try
        {
            var receipt = WriteReceipts ? new BackupReceipt() : null;
            var res = await PortableBackupService.BackupAsync(sources, Path.Combine(parent, sub), null, default, receipt);
            Log(res.Ok ? $"  → {sub}\\  ({res.Copied} copied{(res.Skipped > 0 ? $", {res.Skipped} skipped" : "")})" : "  failed: " + res.Error);
            if (res.Ok && receipt is not null && receipt.Files.Count > 0)
            {
                var (ok, bad, _) = receipt.WriteAndVerify(Path.Combine(parent, sub), $"{sub} (Prepare for Reset)");
                Log(bad == 0 ? $"     all {ok} file(s) checked, receipt saved" : $"     {bad} file(s) need a look, see the receipt in {sub}\\");
            }
        }
        catch (Exception ex) { Log("  failed: " + ex.Message); }
    }

    private void BackupDrivers(string parent)
    {
        Log("Drivers: preparing the guarded export script (needs administrator)…");
        var driversDir = Path.Combine(parent, "Drivers");
        var res = DriverBackupService.WriteBackupScript(parent, driversDir);
        if (!res.Ok) { Log("  failed: " + res.Error); return; }
        try
        {
            Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("powershell.exe"),
                $"-NoProfile -ExecutionPolicy Bypass -File \"{res.ScriptPath}\"")
            { UseShellExecute = true, Verb = "runas" });
            Log("  → launched the driver export in its own Administrator window.");
            Log("     Approve the UAC prompt and type Y there; drivers land in Drivers\\ .");
        }
        catch (System.ComponentModel.Win32Exception) { Log("  driver export cancelled, administrator permission was not granted."); Log("  (the script is saved in the backup folder; you can run it later.)"); }
        catch (Exception ex) { Log("  couldn't launch: " + ex.Message); }
    }

    // ---- helpers ----
    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }

    private static string UniqueDir(string path)
    {
        if (!Directory.Exists(path)) return path;
        for (var i = 2; ; i++) { var c = $"{path} ({i})"; if (!Directory.Exists(c)) return c; }
    }
}
