using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Venquery.Services;

namespace Venquery.Controls;

/// <summary>The Organize page (every edition). Scans a folder, shows its files by type, and moves files only
/// after a preview + confirmation, with an Undo for every run. All file work is in
/// <see cref="OrganizerService"/>; this class is the UI. It raises <see cref="StatusChanged"/> and drives the
/// shared progress band through <see cref="BeginTask"/> instead of touching the main window directly.</summary>
public partial class OrganizerView : UserControl
{
    public event Action<string>? StatusChanged;
    /// <summary>Set by the main window: starts the shared bottom progress band.</summary>
    public Func<string, bool, MainWindow.TaskScope>? BeginTask { get; set; }

    private readonly AppSettings _settings;
    private readonly bool _loading = true;
    private bool _revertingUndoDays;
    private string _folder = "";
    private readonly ObservableCollection<OrgFile> _files = new();
    private ListCollectionView? _view;
    private readonly HashSet<OrgKind> _enabledKinds = new(OrganizerService.AllKinds);
    private List<IReadOnlyList<OrgFile>> _dupeGroups = new();
    private OrgPlan? _pendingPlan;
    private string _lastLog = "";
    private string _lastResultFolder = "";
    private CancellationTokenSource? _cts;

    public OrganizerView(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_files);
        _view.Filter = o => o is OrgFile f && Shown(f);
        FileList.ItemsSource = _view;

        IncludeSubfolders.IsChecked = settings.OrganizerIncludeSubfolders;
        DestinationBox.SelectedIndex = settings.OrganizerUseLibraries ? 1 : 0;
        DateSplitBox.SelectedIndex = settings.OrganizerDateSplit switch { "year" => 1, "month" => 2, _ => 0 };
        var start = !string.IsNullOrWhiteSpace(settings.OrganizerFolder) && Directory.Exists(settings.OrganizerFolder)
            ? settings.OrganizerFolder : DownloadService.DownloadsFolder();
        RuleWhatBox.Items.Add(new ComboBoxItem { Content = "A file extension..." });
        foreach (var k in OrganizerService.AllKinds) RuleWhatBox.Items.Add(new ComboBoxItem { Content = OrganizerService.KindLabel(k) + " (type)", Tag = k });
        RuleWhatBox.SelectedIndex = 0;
        RefreshRules();
        var undoDays = OrganizerService.NormalizeUndoDays(settings.OrganizerUndoDays);
        foreach (var d in OrganizerService.UndoDayChoices) UndoDaysBox.Items.Add(new ComboBoxItem { Content = OrganizerService.UndoDaysLabel(d), Tag = d });
        UndoDaysBox.SelectedIndex = Array.IndexOf(OrganizerService.UndoDayChoices, undoDays);
        _loading = false;
        SetFolder(start, save: false);
        RefreshHistory();
    }

    private Window? Owner => Window.GetWindow(this);
    private bool Busy => _cts is not null;

    // ---- folder ---------------------------------------------------------------------------------------------

    private void SetFolder(string folder, bool save = true)
    {
        _folder = folder;
        FolderText.Text = folder;
        FolderText.ToolTip = folder;
        var why = OrganizerService.WhyNot(folder);
        ShowFolderNote();
        ScanButton.IsEnabled = why is null && !Busy;
        ClearResults();
        if (save) { _settings.OrganizerFolder = folder; _settings.Save(); }
    }

    /// <summary>The note under the folder: why it can't be organized, or what is always left alone (with the
    /// counts once a scan has run).</summary>
    private void ShowFolderNote(int skippedFiles = -1, int skippedFolders = 0)
    {
        var why = OrganizerService.WhyNot(_folder);
        FolderNote.Text = why ?? (skippedFiles < 0
            ? "Project folders, app folders, shortcuts, hidden files and downloads still in progress are always left alone."
            : $"Left alone: {skippedFiles:n0} file(s) and {skippedFolders:n0} folder(s) (shortcuts, hidden files, downloads in progress, app folders, code projects). They are never touched.");
        FolderNote.SetResourceReference(TextBlock.ForegroundProperty, why is null ? "Muted" : "Amber");
    }

    /// <summary>The tiles keep a fixed width (at least three columns, like the mockup's row), so a lone first tile
    /// before a scan doesn't stretch, and "Not scanned" never clips on a small window.</summary>
    private void UpdateTileColumns()
    {
        var shown = OrgTiles.Children.OfType<UIElement>().Count(c => c.Visibility == Visibility.Visible);
        OrgTiles.Columns = Math.Max(3, shown);
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picked = FolderPicker.Pick(Owner, "Choose a folder to organize", _folder);
        if (!string.IsNullOrWhiteSpace(picked)) SetFolder(picked!);
    }

    private void OnQuickFolder(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        var f = tag switch
        {
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            _ => DownloadService.DownloadsFolder(),
        };
        if (!string.IsNullOrWhiteSpace(f)) SetFolder(f);
    }

    private void OnIncludeSubfoldersChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.OrganizerIncludeSubfolders = IncludeSubfolders.IsChecked == true;
        _settings.Save();
        ClearResults();
    }

    private void OnOptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DestinationBox is null || DateSplitBox is null) return;
        _settings.OrganizerUseLibraries = DestinationBox.SelectedIndex == 1;
        _settings.OrganizerDateSplit = DateSplitBox.SelectedIndex switch { 1 => "year", 2 => "month", _ => "none" };
        _settings.Save();
        HidePreview();
    }

    // ---- scan -----------------------------------------------------------------------------------------------

    private void ClearResults()
    {
        if (_files is null || ResultsPanel is null) return;
        _files.Clear();
        _dupeGroups.Clear();
        ResultsPanel.Visibility = Visibility.Collapsed;
        TypeChipsPanel.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Visible;
        EmptyText.Text = "Pick a folder above, then click Scan. Scanning only reads file names, sizes and dates.";
        // Tiles show real numbers only: until a scan has counted them, the count tiles say so or stay hidden.
        StatFiles.Text = "Not scanned";
        StatFilesLabel.Text = "press Scan to count the files";
        StatSizeTile.Visibility = StatDupesTile.Visibility = Visibility.Collapsed;
        UpdateTileColumns();
        ShowFolderNote();
        OrganizeButton.IsEnabled = false;
        DupesOnly.IsChecked = false;
        DupesOnly.Visibility = MoveDupesButton.Visibility = Visibility.Collapsed;
        HidePreview();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (ModeSpace.IsChecked == true) await SpaceScanAsync();
        else await ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (Busy) return;
        var why = OrganizerService.WhyNot(_folder);
        if (why is not null)
        {
            MessageBox.Show(Owner, why, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ClearResults();
        SetBusy(true);
        var t = BeginTask?.Invoke("Scanning " + Path.GetFileName(_folder.TrimEnd('\\')) + "...", false);
        var progress = new Progress<(int files, string current)>(p => t?.Report($"Found {p.files:n0} files... {p.current}"));
        OrgScanResult res;
        try { res = await OrganizerService.ScanAsync(_folder, IncludeSubfolders.IsChecked == true, progress, _cts!.Token, CurrentRules()); }
        catch (Exception ex)
        {
            t?.Done();
            SetBusy(false);
            EmptyText.Text = "The scan couldn't finish: " + ex.Message;
            return;
        }
        t?.Done($"{AppVersion()} · organize · {res.Files.Count:n0} file(s) in {Path.GetFileName(_folder.TrimEnd('\\'))}");
        SetBusy(false);

        foreach (var f in res.Files) _files.Add(f);
        _enabledKinds.Clear();
        foreach (var k in OrganizerService.AllKinds) _enabledKinds.Add(k);
        BuildTypeChips();
        StatFiles.Text = res.Files.Count.ToString("n0");
        StatFilesLabel.Text = res.Files.Count == 1 ? "file found" : "files found";
        StatSize.Text = OrganizerService.FormatBytes(res.Files.Sum(f => f.SizeBytes));
        StatSizeLabel.Text = "in " + (Path.GetFileName(_folder.TrimEnd('\\')) is { Length: > 0 } n ? n : _folder);
        StatSizeLabel.ToolTip = _folder;
        StatSizeTile.Visibility = Visibility.Visible;
        UpdateTileColumns();
        ShowFolderNote(res.SkippedFiles, res.SkippedFolders);
        if (res.Files.Count == 0)
        {
            EmptyText.Text = res.Cancelled ? "Scan cancelled." : "Nothing to organize here, no loose files were found. (Tidy already!)";
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;
        TypeChipsPanel.Visibility = Visibility.Visible;
        OrganizeButton.IsEnabled = !Busy;
        ApplySort();
        UpdateCount();
        if (res.Truncated) StatusChanged?.Invoke("organize · stopped at 50,000 files, organize a smaller folder for the rest");
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        if (busy) _cts = new CancellationTokenSource();
        else { _cts?.Dispose(); _cts = null; }
        CancelButton.Visibility = SpaceCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !busy && OrganizerService.WhyNot(_folder) is null;
        SpaceScanButton.IsEnabled = !busy && Directory.Exists(_spaceTarget);
        ModeOrganize.IsEnabled = ModeSpace.IsEnabled = SpaceChooseButton.IsEnabled = SpaceDriveBar.IsEnabled = !busy;
        foreach (var b in new UIElement[] { ChooseButton, QuickDownloads, QuickDesktop, QuickDocuments, IncludeSubfolders,
                     MoveTickedButton, FindDupesButton, MoveDupesButton, ConfirmButton, UndoLastButton, HistoryList })
            b.IsEnabled = !busy;
        OrganizeButton.IsEnabled = !busy && _files.Count > 0;
    }

    // ---- type chips, filter, sort ---------------------------------------------------------------------------

    private void BuildTypeChips()
    {
        TypeChips.Children.Clear();
        foreach (var g in _files.GroupBy(f => f.Kind).OrderByDescending(g => g.Count()))
        {
            var cb = new CheckBox
            {
                Style = (Style)FindResource("PillCheck"),
                Content = ChipText(OrganizerService.KindLabel(g.Key), $"{g.Count():n0} · {OrganizerService.FormatBytes(g.Sum(f => f.SizeBytes))}"),
                Margin = new Thickness(0, 0, 8, 8),
                IsChecked = true,
                Tag = g.Key,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cb.Checked += OnTypeChip;
            cb.Unchecked += OnTypeChip;
            TypeChips.Children.Add(cb);
        }
    }

    /// <summary>A chip label: the type name, then its count and size in a quieter colour.</summary>
    private static TextBlock ChipText(string name, string detail)
    {
        var tb = new TextBlock();
        tb.Inlines.Add(new System.Windows.Documents.Run(name));
        var d = new System.Windows.Documents.Run("  " + detail) { FontSize = 11.5 };
        d.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
        tb.Inlines.Add(d);
        return tb;
    }

    private void OnTypeChip(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: OrgKind k } cb)
        {
            if (cb.IsChecked == true) _enabledKinds.Add(k); else _enabledKinds.Remove(k);
            RefreshView();
        }
    }

    private bool Shown(OrgFile f)
    {
        if (!_enabledKinds.Contains(f.Kind)) return false;
        var q = FilterBox?.Text?.Trim() ?? "";
        if (q.Length > 0 && f.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (DupesOnly?.IsChecked == true && !f.HasDupMark) return false;
        var days = AgeBox?.SelectedIndex switch { 1 => 30, 2 => 90, 3 => 365, _ => 0 };
        if (days > 0 && f.Modified > DateTime.Now.AddDays(-days)) return false;
        return true;
    }

    private void RefreshView()
    {
        _view?.Refresh();
        UpdateCount();
        HidePreview();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = string.IsNullOrEmpty(FilterBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshView();
    }

    private void OnAgeChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) RefreshView(); }
    private void OnDupesOnlyChanged(object sender, RoutedEventArgs e) => RefreshView();
    private void OnSortChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) ApplySort(); }

    private void ApplySort()
    {
        if (_view is null || SortBox is null) return;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            switch (SortBox.SelectedIndex)
            {
                case 1: _view.SortDescriptions.Add(new SortDescription(nameof(OrgFile.SizeBytes), ListSortDirection.Descending)); break;
                case 2: _view.SortDescriptions.Add(new SortDescription(nameof(OrgFile.Modified), ListSortDirection.Descending)); break;
                case 3: _view.SortDescriptions.Add(new SortDescription(nameof(OrgFile.Modified), ListSortDirection.Ascending)); break;
                case 4: _view.SortDescriptions.Add(new SortDescription(nameof(OrgFile.KindLabel), ListSortDirection.Ascending)); break;
            }
            _view.SortDescriptions.Add(new SortDescription(nameof(OrgFile.Name), ListSortDirection.Ascending));
        }
    }

    private IEnumerable<OrgFile> ShownFiles() => _view is null ? Enumerable.Empty<OrgFile>() : _view.Cast<OrgFile>();

    private void UpdateCount()
    {
        var shown = ShownFiles().ToList();
        var ticked = _files.Count(f => f.IsSelected);
        CountText.Text = $"{shown.Count:n0} of {_files.Count:n0} shown · {OrganizerService.FormatBytes(shown.Sum(f => f.SizeBytes))}" +
                         (ticked > 0 ? $" · {ticked:n0} ticked" : "");
        SelectAllButton.Content = shown.Count > 0 && shown.All(f => f.IsSelected) ? "Untick all shown" : "Tick all shown";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        var shown = ShownFiles().ToList();
        var all = shown.Count > 0 && shown.All(f => f.IsSelected);
        foreach (var f in shown) f.IsSelected = !all;
        UpdateCount();
    }

    // ---- actions -> preview ---------------------------------------------------------------------------------

    private void OnPreviewOrganize(object sender, RoutedEventArgs e)
    {
        var dest = DestinationBox.SelectedIndex == 1 ? OrgDestination.WindowsLibraries : OrgDestination.SubfoldersHere;
        var split = DateSplitBox.SelectedIndex switch { 1 => OrgDateSplit.Year, 2 => OrgDateSplit.YearMonth, _ => OrgDateSplit.None };
        ShowPreview(OrganizerService.PlanByType(_folder, ShownFiles().ToList(), _enabledKinds, dest, split, CurrentRules()));
    }

    private void OnMoveTicked(object sender, RoutedEventArgs e)
    {
        var ticked = _files.Where(f => f.IsSelected).ToList();
        if (ticked.Count == 0)
        {
            MessageBox.Show(Owner, "Tick the files you want to move first (or use Tick all shown).", Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var target = FolderPicker.Pick(Owner, $"Move {ticked.Count} file(s) to...", _folder);
        if (string.IsNullOrWhiteSpace(target)) return;
        if (MainWindow.IsSystemLocation(Path.GetFullPath(target!)))
        {
            MessageBox.Show(Owner, "Please pick a folder outside Windows and Program Files.", Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowPreview(OrganizerService.PlanMoveTo(_folder, ticked, target!));
    }

    private async void OnFindDuplicates(object sender, RoutedEventArgs e)
    {
        if (Busy || _files.Count == 0) return;
        SetBusy(true);
        var t = BeginTask?.Invoke("Looking for duplicate files...", true);
        var progress = new Progress<(int done, int total)>(p => t?.Report(p.total > 0 ? 100.0 * p.done / p.total : 100, $"Comparing files... {p.done:n0} of {p.total:n0}"));
        try { _dupeGroups = await OrganizerService.FindDuplicatesAsync(_files.ToList(), progress, _cts!.Token); }
        catch (OperationCanceledException) { _dupeGroups = new(); }
        catch (Exception ex) { _dupeGroups = new(); StatusChanged?.Invoke("organize · duplicate search failed: " + ex.Message); }
        finally { t?.Done(); SetBusy(false); }

        foreach (var f in _files) f.DupMark = "";
        foreach (var g in _dupeGroups)
        {
            g[0].DupMark = "keep";
            foreach (var extra in g.Skip(1)) extra.DupMark = "duplicate";
        }
        var extras = _dupeGroups.Sum(g => g.Count - 1);
        var wasted = _dupeGroups.Sum(g => g.Skip(1).Sum(f => f.SizeBytes));
        DupesText.Text = extras == 0
            ? "No duplicates found, every file here is unique."
            : $"{extras:n0} extra cop{(extras == 1 ? "y" : "ies")} of {_dupeGroups.Count:n0} file(s), {OrganizerService.FormatBytes(wasted)} that could be freed. The copy marked \"keep\" stays where it is.";
        DupesOnly.Visibility = MoveDupesButton.Visibility = extras > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatDupes.Text = _dupeGroups.Count.ToString("n0");
        StatDupesTile.ToolTip = extras == 0 ? null : $"{extras:n0} extra cop{(extras == 1 ? "y" : "ies")}, {OrganizerService.FormatBytes(wasted)} that could be freed";
        StatDupesTile.Visibility = Visibility.Visible;
        UpdateTileColumns();
        if (extras > 0) DupesOnly.IsChecked = true; else RefreshView();
    }

    private void OnMoveDuplicates(object sender, RoutedEventArgs e)
    {
        if (_dupeGroups.Count == 0) return;
        ShowPreview(OrganizerService.PlanDuplicates(_folder, _dupeGroups));
    }

    private void ShowPreview(OrgPlan plan)
    {
        if (plan.Moves.Count == 0)
        {
            HidePreview();
            MessageBox.Show(Owner,
                plan.AlreadyInPlace > 0 ? "Everything shown is already where it belongs, nothing to move."
                                        : "Nothing to move (check the type chips and filters).",
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _pendingPlan = plan;
        ResultPanel.Visibility = Visibility.Collapsed;
        PreviewTitle.Text = $"Ready to move {plan.Moves.Count:n0} file(s), {OrganizerService.FormatBytes(plan.TotalBytes)}";
        var groups = plan.Groups;
        // The list uses a monospace font, so pad the folder and count columns to line up.
        var shownGroups = groups.Take(14).ToList();
        var labelWidth = shownGroups.Count == 0 ? 0 : Math.Min(40, shownGroups.Max(g => g.Label.Length));
        var countWidth = shownGroups.Count == 0 ? 0 : shownGroups.Max(g => g.Count.ToString("n0").Length);
        var lines = shownGroups.Select(g => $"→ {g.Label.PadRight(labelWidth)}   {g.Count.ToString("n0").PadLeft(countWidth)} file(s) · {OrganizerService.FormatBytes(g.Bytes)}").ToList();
        if (groups.Count > 14) lines.Add($"   ...and {groups.Count - 14} more folder(s)");
        PreviewList.ItemsSource = lines;
        PreviewNote.Text = (plan.AlreadyInPlace > 0 ? $"{plan.AlreadyInPlace:n0} file(s) are already in the right place and stay put. " : "") +
                           "Nothing is deleted or overwritten, and you can undo this afterwards.";
        ConfirmButton.Content = $"Move {plan.Moves.Count:n0} file(s)";
        PreviewPanel.Visibility = Visibility.Visible;
        PreviewPanel.BringIntoView();
    }

    private void HidePreview()
    {
        _pendingPlan = null;
        if (PreviewPanel is not null) PreviewPanel.Visibility = Visibility.Collapsed;
    }

    private void OnDiscardPreview(object sender, RoutedEventArgs e) => HidePreview();

    // ---- run + undo -----------------------------------------------------------------------------------------

    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        var plan = _pendingPlan;
        if (plan is null || Busy) return;
        HidePreview();
        SetBusy(true);
        var t = BeginTask?.Invoke("Moving files...", true);
        var progress = new Progress<(int done, int total, string current)>(p =>
            t?.Report(p.total > 0 ? 100.0 * p.done / p.total : 100, $"Moving {p.done:n0} of {p.total:n0} · {p.current}"));
        OrgRunResult res;
        try { res = await OrganizerService.ExecuteAsync(plan, progress, _cts!.Token); }
        catch (Exception ex)
        {
            t?.Done(); SetBusy(false);
            MessageBox.Show(Owner, "The move couldn't finish.\n\n" + ex.Message, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        t?.Done($"{AppVersion()} · organize · moved {res.Moved:n0} file(s)");
        SetBusy(false);

        _lastLog = res.LogPath;
        _lastResultFolder = plan.Moves.Select(m => m.DestFolder).GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? _folder;
        ShowResult((res.Cancelled ? "Stopped. " : "") + $"Moved {res.Moved:n0} file(s)" +
                   (res.Skipped > 0 ? $", {res.Skipped:n0} skipped (in use or gone)" : "") + ".", canUndo: res.Moved > 0);
        RefreshHistory();
        await RescanQuietly();
    }

    private async void OnUndoLast(object sender, RoutedEventArgs e) => await UndoAsync(_lastLog);

    private void OnKeepLast(object sender, RoutedEventArgs e)
    {
        if (Busy || string.IsNullOrWhiteSpace(_lastLog)) return;
        OrganizerService.KeepRun(_lastLog);
        _lastLog = "";
        ShowResult("Changes kept. This run is final and won't be offered for undo.", canUndo: false);
        RefreshHistory();
    }

    private void OnKeepHistory(object sender, RoutedEventArgs e)
    {
        if (Busy || (sender as FrameworkElement)?.Tag is not string log || string.IsNullOrWhiteSpace(log)) return;
        OrganizerService.KeepRun(log);
        if (string.Equals(log, _lastLog, StringComparison.OrdinalIgnoreCase))
        {
            _lastLog = "";
            UndoLastButton.Visibility = KeepButton.Visibility = Visibility.Collapsed;
        }
        StatusChanged?.Invoke($"{AppVersion()} · organize · changes kept, that run is final");
        RefreshHistory();
    }

    private async void OnUndoHistory(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string log) await UndoAsync(log);
    }

    private async Task UndoAsync(string logPath)
    {
        if (Busy || string.IsNullOrWhiteSpace(logPath)) return;
        RefreshHistory();                                        // applies retention, so an expired run can't be undone
        if (!File.Exists(logPath))
        {
            MessageBox.Show(Owner, "That run can no longer be undone, its undo window has passed.", "Undo organize", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var entry = OrganizerService.History().FirstOrDefault(h => string.Equals(h.LogPath, logPath, StringComparison.OrdinalIgnoreCase));
        var count = entry?.Count ?? 0;
        if (MessageBox.Show(Owner, $"Put {count:n0} file(s) back where they were?\n\nFiles you've changed or moved since are left where they are.",
                "Undo organize", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)   // No is the default: Enter never undoes
            return;
        SetBusy(true);
        var t = BeginTask?.Invoke("Putting files back...", true);
        var progress = new Progress<(int done, int total, string current)>(p =>
            t?.Report(p.total > 0 ? 100.0 * p.done / p.total : 100, $"Restoring {p.done:n0} of {p.total:n0} · {p.current}"));
        OrgRunResult res;
        try { res = await OrganizerService.UndoAsync(logPath, progress, _cts!.Token); }
        catch (Exception ex)
        {
            t?.Done(); SetBusy(false);
            MessageBox.Show(Owner, "Undo couldn't finish.\n\n" + ex.Message, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        t?.Done($"{AppVersion()} · organize · put back {res.Moved:n0} file(s)");
        SetBusy(false);
        _lastResultFolder = entry?.Root ?? _folder;
        ShowResult($"Put back {res.Moved:n0} file(s)" + (res.Skipped > 0 ? $", {res.Skipped:n0} left in place (see the details)" : "") + ".", canUndo: false);
        if (res.Notes.Count > 0) ResultText.ToolTip = string.Join("\n", res.Notes.Take(20));
        // Once a run is fully undone there's nothing left for the banner's Undo button to do.
        if (string.Equals(logPath, _lastLog, StringComparison.OrdinalIgnoreCase) && OrganizerService.LoadLog(logPath)?.Undone != false) _lastLog = "";
        RefreshHistory();
        await RescanQuietly();
    }

    private void ShowResult(string text, bool canUndo)
    {
        ResultText.Text = text;
        ResultText.ToolTip = null;
        UndoLastButton.Visibility = canUndo && !string.IsNullOrEmpty(_lastLog) ? Visibility.Visible : Visibility.Collapsed;
        KeepButton.Visibility = UndoLastButton.Visibility;
        ResultPanel.Visibility = Visibility.Visible;
        StatusChanged?.Invoke($"{AppVersion()} · organize · {text}");
    }

    private async Task RescanQuietly()
    {
        // Keep the result banner while the list refreshes to the folder's new state.
        var keep = ResultPanel.Visibility;
        await ScanAsync();
        ResultPanel.Visibility = keep;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var f = Directory.Exists(_lastResultFolder) ? _lastResultFolder : _folder;
        MainWindow.OpenExternal(f);
    }

    // ---- what's using space (read-only) ---------------------------------------------------------------------

    private string _spaceTarget = "";
    private SpaceScanResult? _space;
    private SpaceNode? _spaceNode;

    // The tabs react to their CHECKED state (not Click) so keyboard, screen readers and UI Automation can switch
    // them too. Unticking the active tab is undone (one tab is always selected).
    private bool _switchingMode;
    private void OnModeOrganize(object sender, RoutedEventArgs e) { if (!_switchingMode && ModeSpace is not null) SetMode(space: false); }
    private void OnModeSpace(object sender, RoutedEventArgs e) { if (!_switchingMode) SetMode(space: true); }
    private void OnModeTabUnchecked(object sender, RoutedEventArgs e)
    {
        if (_switchingMode || sender is not CheckBox cb) return;
        _switchingMode = true; cb.IsChecked = true; _switchingMode = false;
    }

    /// <summary>Called by the Files page tabs (1.4.0): Organize or "What's using space".</summary>
    public void ShowMode(bool space) => SetMode(space);

    private void SetMode(bool space)
    {
        if (OrganizeMode is null || SpaceMode is null) return;   // during InitializeComponent
        _switchingMode = true;
        ModeOrganize.IsChecked = !space;
        ModeSpace.IsChecked = space;
        _switchingMode = false;
        OrganizeMode.Visibility = space ? Visibility.Collapsed : Visibility.Visible;
        SpaceMode.Visibility = space ? Visibility.Visible : Visibility.Collapsed;
        // The intro and the "nothing moves" note describe organizing; the space view only reads.
        OrgIntro.Text = space
            ? "See which folders and files take up the most room on a drive or in a folder. Read-only, nothing is moved or deleted."
            : "Tidy a cluttered folder like Downloads or the Desktop. See every file by type, then sort them into folders (Pictures, Videos, Music, Documents, Installers...) automatically, or move just the files you tick.";
        OrgInfoBar.Visibility = space ? Visibility.Collapsed : Visibility.Visible;
        if (space && SpaceDriveBar.Children.Count == 0) BuildSpaceDrives();
        ScanButton.IsEnabled = !Busy && OrganizerService.WhyNot(_folder) is null;
        SpaceScanButton.IsEnabled = !Busy && Directory.Exists(_spaceTarget);
    }

    private void BuildSpaceDrives()
    {
        SpaceDriveBar.Children.Clear();
        foreach (var d in DriveList.Ready())
        {
            var b = new Button { Style = (Style)FindResource("ChromeButton"), Content = d.Caption, Tag = d.Root, Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => SetSpaceTarget(d.Root);
            SpaceDriveBar.Children.Add(b);
        }
        if (string.IsNullOrEmpty(_spaceTarget))
            SetSpaceTarget(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\");
    }

    private void SetSpaceTarget(string path)
    {
        _spaceTarget = path;
        SpaceTargetText.Text = path;
        SpaceResults.Visibility = Visibility.Collapsed;
        SpaceEmpty.Visibility = Visibility.Visible;
        SpaceEmpty.Text = "Click Scan to see what's using space in " + path + ".";
        SpaceScanButton.IsEnabled = !Busy && Directory.Exists(path);
    }

    private void OnSpaceChoose(object sender, RoutedEventArgs e)
    {
        var picked = FolderPicker.Pick(Owner, "Choose a drive or folder to measure", _spaceTarget);
        if (!string.IsNullOrWhiteSpace(picked)) SetSpaceTarget(picked!);
    }

    private async Task SpaceScanAsync()
    {
        if (Busy || !Directory.Exists(_spaceTarget)) return;
        SetBusy(true);
        var t = BeginTask?.Invoke("Measuring " + _spaceTarget + "...", false);
        var progress = new Progress<(int folders, long bytes, string current)>(p =>
            t?.Report($"{p.folders:n0} folders · {OrganizerService.FormatBytes(p.bytes)} so far · {p.current}"));
        try { _space = await SpaceService.ScanAsync(_spaceTarget, progress, _cts!.Token); }
        catch (Exception ex)
        {
            t?.Done(); SetBusy(false);
            SpaceEmpty.Text = "The scan couldn't finish: " + ex.Message;
            return;
        }
        t?.Done($"{AppVersion()} · space · {OrganizerService.FormatBytes(_space.Root.Size)} in {_space.FilesCount:n0} files");
        SetBusy(false);

        if (_space.DriveTotal > 0)
        {
            var used = _space.DriveTotal - _space.DriveFree;
            SpaceDriveText.Text = $"Drive {Path.GetPathRoot(_spaceTarget)}  {OrganizerService.FormatBytes(used)} used of {OrganizerService.FormatBytes(_space.DriveTotal)} · " +
                                  $"{OrganizerService.FormatBytes(_space.DriveFree)} free" + (_space.Cancelled ? " · scan stopped early, sizes are partial" : "");
            SpaceDriveBarFill.Value = 100.0 * used / _space.DriveTotal;
        }
        SpaceEmpty.Visibility = Visibility.Collapsed;
        SpaceResults.Visibility = Visibility.Visible;
        ShowSpaceNode(_space.Root);
        SpaceFileList.ItemsSource = _space.LargestFiles.Take(40).Select(f => new
        {
            f.FullPath,
            Name = Path.GetFileName(f.FullPath),
            Folder = Path.GetDirectoryName(f.FullPath),
            SizeText = OrganizerService.FormatBytes(f.Size),
        }).ToList();
    }

    private sealed class SpaceRow
    {
        public SpaceNode? Node { get; init; }
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public string SizeText { get; init; } = "";
        public double Percent { get; init; }
        public bool CanOpen { get; init; }
    }

    private void ShowSpaceNode(SpaceNode node)
    {
        _spaceNode = node;
        SpacePathText.Text = node.FullPath;
        SpaceNodeText.Text = $"{OrganizerService.FormatBytes(node.Size)} in {node.Files:n0} file(s)" +
                             (node.Partial ? " · some folders couldn't be read, so this is a minimum" : "");
        SpaceUpButton.IsEnabled = node.Parent is not null;
        SpaceOrganizeButton.IsEnabled = OrganizerService.WhyNot(node.FullPath) is null;
        var max = Math.Max(1, Math.Max(node.LooseSize, node.Children.Count > 0 ? node.Children[0].Size : 0));
        var rows = node.Children.Take(40).Select(c => new SpaceRow
        {
            Node = c,
            Name = c.Name,
            Detail = $"{c.Files:n0} file(s)" + (c.Partial ? " · partly unreadable" : ""),
            SizeText = OrganizerService.FormatBytes(c.Size),
            Percent = 100.0 * c.Size / max,
            CanOpen = c.Children.Count > 0 || c.LooseFiles > 0,
        }).ToList();
        if (node.LooseFiles > 0)
            rows.Add(new SpaceRow
            {
                Name = "(files directly in this folder)",
                Detail = $"{node.LooseFiles:n0} file(s)",
                SizeText = OrganizerService.FormatBytes(node.LooseSize),
                Percent = 100.0 * node.LooseSize / max,
                CanOpen = false,
            });
        SpaceFolderList.ItemsSource = rows.OrderByDescending(r => r.Percent).ToList();
    }

    private void OnSpaceFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SpaceNode n) ShowSpaceNode(n);
    }

    private void OnSpaceUp(object sender, RoutedEventArgs e)
    {
        if (_spaceNode?.Parent is { } p) ShowSpaceNode(p);
    }

    private void OnSpaceShow(object sender, RoutedEventArgs e)
    {
        if (_spaceNode is not null) MainWindow.OpenExternal(_spaceNode.FullPath);
    }

    private void OnSpaceFileShow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) MainWindow.RevealInExplorer(path);   // shown selected in Explorer, never opened
    }

    private void OnSpaceOrganize(object sender, RoutedEventArgs e)
    {
        if (_spaceNode is null) return;
        SetMode(space: false);
        SetFolder(_spaceNode.FullPath);
    }

    // ---- your rules -----------------------------------------------------------------------------------------

    private OrgRules CurrentRules()
        => OrgRules.From(_settings.OrganizerRules.Select(r => (r.Match, r.Folder)));

    private sealed class RuleRow
    {
        public int Index { get; init; }
        public string Text { get; init; } = "";
    }

    private void RefreshRules()
    {
        RulesList.ItemsSource = _settings.OrganizerRules.Select((r, i) => new RuleRow
        {
            Index = i,
            Text = (r.Match.StartsWith(".", StringComparison.Ordinal) ? r.Match : r.Match + " (type)") + "  \u2192  " + r.Folder,
        }).ToList();
    }

    private void OnRuleWhatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuleExtFrame is null) return;
        RuleExtFrame.Visibility = RuleWhatBox.SelectedIndex <= 0 ? Visibility.Visible : Visibility.Collapsed;
        RuleError.Visibility = Visibility.Collapsed;
    }

    private void OnRuleTextChanged(object sender, TextChangedEventArgs e)
    {
        RuleExtPlaceholder.Visibility = string.IsNullOrEmpty(RuleExtBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RuleFolderPlaceholder.Visibility = string.IsNullOrEmpty(RuleFolderBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RuleError.Visibility = Visibility.Collapsed;
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        string? match;
        if (RuleWhatBox.SelectedIndex <= 0)
        {
            match = OrganizerService.CleanExtension(RuleExtBox.Text);
            if (match is null) { ShowRuleError("Type an extension like .psd (letters and numbers only)."); return; }
        }
        else match = OrganizerService.KindLabel((OrgKind)((ComboBoxItem)RuleWhatBox.SelectedItem).Tag);
        var why = OrganizerService.WhyBadFolder(RuleFolderBox.Text);
        if (why is not null) { ShowRuleError(why); return; }
        var folder = OrganizerService.CleanFolder(RuleFolderBox.Text);
        _settings.OrganizerRules.RemoveAll(r => string.Equals(r.Match, match, StringComparison.OrdinalIgnoreCase));
        _settings.OrganizerRules.Add(new OrganizerRuleSetting { Match = match!, Folder = folder });
        _settings.Save();
        RuleExtBox.Text = ""; RuleFolderBox.Text = "";
        RefreshRules();
        HidePreview();
        StatusChanged?.Invoke($"organize · rule added: {match} \u2192 {folder}");
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not int i || i < 0 || i >= _settings.OrganizerRules.Count) return;
        _settings.OrganizerRules.RemoveAt(i);
        _settings.Save();
        RefreshRules();
        HidePreview();
    }

    private void ShowRuleError(string text)
    {
        RuleError.Text = text;
        RuleError.Visibility = Visibility.Visible;
    }

    // ---- history --------------------------------------------------------------------------------------------

    private sealed class HistoryRow
    {
        public string LogPath { get; init; } = "";
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public bool CanUndo { get; init; }
        public string Hint { get; init; } = "";
        public Visibility HintVisibility => Hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string HintText => Hint.Length > 0 ? " · " + Hint : "";
        public Visibility KeepVisibility => CanUndo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUndoDaysChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _revertingUndoDays || (UndoDaysBox.SelectedItem as ComboBoxItem)?.Tag is not int days) return;
        // A shorter window takes undo away from runs that have it now, so say so first (No = keep the old setting).
        var now = DateTime.Now;
        var losing = OrganizerService.History().Count(h => h.CanUndo && OrganizerService.UndoExpires(h.When, days) is { } exp && exp <= now);
        if (losing > 0 && MessageBox.Show(Owner,
                $"{losing:n0} earlier run(s) will no longer be undoable with this setting. Their files stay where they are.\n\nChange it anyway?",
                "Undo available for", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            _revertingUndoDays = true;
            UndoDaysBox.SelectedIndex = Array.IndexOf(OrganizerService.UndoDayChoices, OrganizerService.NormalizeUndoDays(_settings.OrganizerUndoDays));
            _revertingUndoDays = false;
            return;
        }
        _settings.OrganizerUndoDays = days;
        _settings.Save();
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        // Retention first: runs past the undo window lose their undo record (the files themselves stay put).
        var undoDays = OrganizerService.NormalizeUndoDays(_settings.OrganizerUndoDays);
        OrganizerService.PruneHistory(undoDays);
        if (!string.IsNullOrEmpty(_lastLog) && !File.Exists(_lastLog))
        {
            _lastLog = "";
            UndoLastButton.Visibility = KeepButton.Visibility = Visibility.Collapsed;
        }
        // Kept runs are final, so they're left out of the list entirely.
        var rows = OrganizerService.History().Where(h => !h.Kept).Take(20).Select(h => new HistoryRow
        {
            LogPath = h.LogPath,
            Title = $"{h.Action} · {h.Count:n0} file(s)" + (h.Undone ? " · undone" : ""),
            Detail = $"{h.When:g} · {h.Root}",
            CanUndo = h.CanUndo,
            Hint = OrganizerService.UndoHint(h, undoDays),
        }).ToList();
        HistoryList.ItemsSource = rows;
        StatUndo.Text = undoDays == 0 ? "No limit" : OrganizerService.UndoDaysLabel(undoDays);
        StatUndoLabel.Text = "undo window";
        HistoryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string AppVersion() => MainWindow.AppVersion;
}
