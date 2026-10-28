using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Venquery.Models;
using Venquery.Services;

namespace Venquery;

public partial class MainWindow : Window
{
    private readonly DefinitionStore _definitions = new();
    private readonly DownloadService _downloader = new();
    private readonly DriverResolver _resolver = new();
    private readonly ObservableCollection<DeviceRow> _rows = new();
    // Interesting-class devices that matched no definition or vendor fallback. Not shown in
    // the UI (most are in-box-driver devices), surfaced only in the export when the user
    // opts in, so a curious/advanced user can look an unknown hardware ID up.
    private readonly List<ScannedDevice> _unmatched = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private ICollectionView _view = null!;
    private string _query = "";
    private bool _busy;

    // --- Toolbox features (Backup & Restore, Portable Apps) ---
    // Backup & Restore is winget-based, so it's excluded from the Legacy (Win 7/8.1) build, which has no
    // winget, the WingetService type isn't compiled there. The Backup nav/page is hidden at runtime too.
#if !LEGACY
    private readonly WingetService _winget = new();
#endif
    private readonly ObservableCollection<AppBackupEntry> _backupApps = new();
    private readonly ObservableCollection<AppBackupEntry> _manualApps = new();
    private readonly ObservableCollection<PortableExe> _portableExes = new();
    private ICollectionView _portableView = null!;
    private ICollectionView _docView = null!;
    private string _portableQuery = "";
    private string _docQuery = "";
    private Venquery.Services.DocCategory _docCategoryFilter = Venquery.Services.DocumentScanner.AllCategories;
    private readonly List<System.Windows.Controls.CheckBox> _portableDriveChecks = new();
    private readonly List<System.Windows.Controls.CheckBox> _docDriveChecks = new();
    private bool _portableSortDesc;
    private bool _docSortDesc;

#if !LEGACY
    private WingetExportResult? _lastExport;
#endif
    private string _savedBackupJson = "", _savedBackupScript = "";
    private string _machineIdentity = "";
    private CancellationTokenSource? _portableCts;
    private CancellationTokenSource? _portableBackupCts;
    private string _savedPortableBackupFolder = "";

    // --- Loose Documents backup ---
    private readonly ObservableCollection<DocEntry> _docEntries = new();
    private CancellationTokenSource? _docScanCts;
    private CancellationTokenSource? _docBackupCts;
    private string _savedDocBackupFolder = "";

    private bool _wingetBusy;
    private string _currentView = "drivers";

    // Detected PC-maker guidance (laptops only), set during scan.
    private (string Maker, string Domain, string Support, bool IsLaptop) _oem;
    private StabilityReport? _stability;
    private SystemInfo? _sysInfo;
    private HealthReport? _health;
    private bool _healthBusy;
    // Guarded maintenance scripts written from the Health page (shared across every edition). Venquery only
    // writes them, the Run buttons (gated below) launch them behind confirm + UAC + the script's typed-Y.
    private string _savedSfcScript = "";
    private string _savedRestoreScript = "";

    // Startup & Tasks page state (loaded lazily on first open).
    private bool _startupLoaded, _startupBusy, _showMsTasks;
    private List<StartupItem>? _startupItems;
    private List<ScheduledTaskItem>? _tasks;
    // Enabling/disabling a scheduled task uses `schtasks /Change /ENABLE|/DISABLE`, which needs Win8+, so the
    // toggle is offered on the .NET 8 editions but not the Win7-capable Legacy build (review-only there).
    private readonly bool _canToggleTasks = Edition.Current.Id != "legacy";


    // Derived from the assembly version (set by <Version> in the .csproj), so it's per-edition automatically
    // and there's no version string to hand-edit — each build can carry its own version
    // on their own track.
    internal static readonly string AppVersion =
        "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

    public MainWindow()
    {
        InitializeComponent();
        ApplyEditionBranding();
        StatusLine.Text = $"{AppVersion} · ready";
        // Every status-bar message (from every page) also lands in the session log, so the log tells the whole
        // story of a session without each page having to log separately.
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(StatusLine, (_, _) => Venquery.Services.AppLog.Write("status: " + StatusLine.Text));
        Venquery.Services.AppLog.Write("app started");
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => o is DeviceRow r &&
            (string.IsNullOrWhiteSpace(_query) || r.Name.Contains(_query, StringComparison.OrdinalIgnoreCase));
        DeviceList.ItemsSource = _view;
        DownloadsPathRun.Text = System.IO.Path.GetFileName(DownloadService.DownloadsFolder());

        // Toolbox views: bind their lists and show which folders the portable scan skips.
        _backupView = CollectionViewSource.GetDefaultView(_backupApps);
        _backupView.Filter = o => o is AppBackupEntry a &&
            (string.IsNullOrWhiteSpace(_backupQuery) || a.Id.Contains(_backupQuery, StringComparison.OrdinalIgnoreCase));
        BackupList.ItemsSource = _backupView;
        BackupDestText.Text = DownloadService.DownloadsFolder();
        BackupDestTileValue.Text = System.IO.Path.GetFileName(DownloadService.DownloadsFolder().TrimEnd('\\'));
        BackupEmpty.Text = "Press “Read apps” to list the apps installed on this PC. Nothing is written to disk until you back up.";
        ManualList.ItemsSource = _manualApps;
        _portableView = CollectionViewSource.GetDefaultView(_portableExes);
        _portableView.Filter = o => o is PortableExe p &&
            (string.IsNullOrWhiteSpace(_portableQuery)
             || p.Name.Contains(_portableQuery, StringComparison.OrdinalIgnoreCase)
             || p.Folder.Contains(_portableQuery, StringComparison.OrdinalIgnoreCase));
        PortableList.ItemsSource = _portableView;
        foreach (var dir in PortableAppScanner.DefaultExcludes())
            PortableExcludes.Children.Add(MakeExcludeChip(dir));

        _docView = CollectionViewSource.GetDefaultView(_docEntries);
        _docView.Filter = o => o is DocEntry d
            && (d.Categories & _docCategoryFilter) != 0
            && (string.IsNullOrWhiteSpace(_docQuery)
                || d.Name.Contains(_docQuery, StringComparison.OrdinalIgnoreCase));
        DocList.ItemsSource = _docView;
        // The Organize page (every edition).
        InitOrganizer();
#if !LEGACY
        // The "Restore from a backup" tab of Backup & Restore (.NET 8 editions; Legacy has no Backup page).
        _restoreView = new Venquery.Controls.RestoreView
        {
            BeginTask = (title, determinate) => BeginTask(title, determinate),
            WriteReceipts = () => _settings.BackupReceipts,
        };
        _restoreView.StatusChanged += s => StatusLine.Text = s;
        RestoreHost.Content = _restoreView;
#endif
#if PROTOOLS
        // Build the Pro Tools nav entry + page (editions built with PROTOOLS).
        InitProTools();
#endif
        // The pages + tabs of the 1.4.0 shell (after the optional pages above exist). Editions without winget
        // (Legacy, Win 7/8.1) keep Backup & Restore with just its Portable apps + Documents tabs, and no
        // "Back up everything" (the reset wizard needs winget / driver export).
        InitSections();

        // Restore saved preferences.
        if (_settings.Width >= MinWidth && _settings.Height >= MinHeight)
        {
            Width = _settings.Width;
            Height = _settings.Height;
        }
        // Always open windowed (never auto-maximize) so the title bar + min/max/close are clearly
        // visible and the window doesn't fill the screen on launch, the user can maximize from there.
        // The last windowed size is still remembered above.
        // Never open taller/wider than the screen's work area (1.4.1 fix): the 900px default is taller than a
        // 1080p screen at 125% scaling, which put the title bar off the top of the screen on a fresh install.
        var work = SystemParameters.WorkArea;
        if (Height > work.Height) Height = Math.Max(MinHeight, work.Height);
        if (Width > work.Width) Width = Math.Max(MinWidth, work.Width);

        // Populate the theme picker, then apply the saved theme by id. Old settings stored only
        // "dark"/"light", those are still valid theme ids, so they carry over unchanged.
        ThemeSelector.ItemsSource = Themes;
        ThemeSelector.SelectedValuePath = "Id";
        ApplyTheme(_settings.Theme);

        // Restore a custom background image, if one was set.
        BgOpacitySlider.Value = _settings.BackgroundOpacity is >= 0.1 and <= 0.9 ? _settings.BackgroundOpacity : 0.35;
        ApplyBackground(_settings.BackgroundImage);

        // Restore toolbox preferences (the "all drives" toggles reuse the old scope settings).
        PortableAllDrives.IsChecked = _settings.PortableWholeDrive;
        DocAllDrives.IsChecked = _settings.DocScanOtherDrives;

        // Left-hand mode (nav rail on the right).
        NavRightToggle.IsChecked = _settings.NavigationOnRight;
        ApplyNavSide(_settings.NavigationOnRight);
        CrossMachineToggle.IsChecked = _settings.AllowCrossMachineRestore;
        CrossMachineWarn.Visibility = _settings.AllowCrossMachineRestore ? Visibility.Visible : Visibility.Collapsed;

        // Reopen the last-used page AND tab (any view: health, startup, organize, restore... all come back now;
        // a view this edition doesn't have, falls back to Devices & Drivers).
        RestoreLastView(_settings.LastView);

        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.Theme = _themeId;
        _settings.Maximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        _settings.Width = b.Width;
        _settings.Height = b.Height;
        _settings.LastView = _currentView;
        _settings.PortableWholeDrive = PortableAllDrives.IsChecked == true;
        _settings.DocScanOtherDrives = DocAllDrives.IsChecked == true;
        _settings.NavigationOnRight = NavRightToggle.IsChecked == true;
        // Snapshot the background opacity from the live control (BackgroundImage is already kept in
        // sync by the choose/clear handlers).
        _settings.BackgroundOpacity = BgOpacitySlider.Value;
        _settings.Save();
    }

    // ------------------------------------------------------------------
    // Theme (light / dark): recolour the shared brush instances so every
    // reference across the app (XAML or code) re-renders in the new palette.
    // ------------------------------------------------------------------

    private bool _dark = true;
    private string _themeId = "dark";
    private string _lastDarkId = "dark";
    private string _lastLightId = "light";
    private bool _suppressThemeEvent;

    private const string SunPath =
        "M12,7 A5,5 0 1 0 12,17 A5,5 0 0 0 12,7 Z M12,1 V3 M12,21 V23 M4.2,4.2 L5.6,5.6 " +
        "M18.4,18.4 L19.8,19.8 M1,12 H3 M21,12 H23 M4.2,19.8 L5.6,18.4 M18.4,5.6 L19.8,4.2";
    private const string MoonPath = "M21,12.8 A9,9 0 1 1 11.2,3 A7,7 0 0 0 21,12.8 Z";

    // The palette and the selectable themes now live per-edition in Editions/Edition.cs; these
    // forward to the active edition so all the theming code below stays edition-agnostic.
    private static (string Key, string Dark, string Light)[] Palette => Edition.Current.Palette;

    public static IReadOnlyList<ThemeOption> Themes => Edition.Current.Themes;

    /// <summary>Apply the active edition's name, title-bar logo and window icon. The palette/themes
    /// come from <c>Edition.Current</c> via <see cref="ApplyTheme"/>. Called once at construction.</summary>
    private void ApplyEditionBranding()
    {
        Title = Edition.Current.DisplayName;
        AppNameText.Text = Edition.Current.DisplayName;

        var logo = new BitmapImage();
        logo.BeginInit();
        logo.UriSource = new Uri($"pack://application:,,,/{Edition.Current.LogoAsset}", UriKind.Absolute);
        logo.DecodePixelWidth = 128;
        logo.CacheOption = BitmapCacheOption.OnLoad;
        logo.EndInit();
        LogoImage.Source = logo;

        // 1.4.1: the window/taskbar icon is NOT set here. A BitmapImage of the .ico keeps only one frame, which
        // Windows then rescales, so an unpinned taskbar button looked small/blurry while a pinned shortcut (which
        // reads the exe's icon) looked right. Left unset, WPF uses the exe's own multi-size ApplicationIcon (set per
        // edition in the .csproj) and Windows picks the right frame for the DPI, exactly like the pinned shortcut.
    }

    private void ApplyTheme(string id)
    {
        var t = Themes.FirstOrDefault(x => x.Id == id) ?? Themes[0];
        _themeId = t.Id;
        _dark = t.Dark;
        if (t.Dark) _lastDarkId = t.Id; else _lastLightId = t.Id;

        // Recolour the shared brush instances in place where possible, so brushes code captured once
        // (RestoreView) follow a theme switch too; a frozen brush (e.g. the XAML defaults) is replaced,
        // and every DynamicResource reference re-resolves to the new one.
        var res = Application.Current.Resources;
        void Set(string key, string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            if (res.Contains(key) && res[key] is SolidColorBrush b && !b.IsFrozen) b.Color = c;
            else res[key] = new SolidColorBrush(c);
        }
        foreach (var (key, d, l) in Palette) Set(key, t.Dark ? d : l);
        foreach (var (key, color) in t.Overrides) Set(key, color);

        // Show the icon of the mode you'd switch TO with the quick toggle.
        ThemeIcon.Data = Geometry.Parse(t.Dark ? SunPath : MoonPath);

        // Match the native window title bar (chrome) to the app theme, dark bar for dark themes,
        // light for light. No-op until the window has an HWND (applied again in OnSourceInitialized).
        ApplyTitleBarTheme(t.Dark);

        // Keep the Settings dropdown in sync without re-entering ApplyTheme.
        if (ThemeSelector is not null && (ThemeSelector.SelectedValue as string) != t.Id)
        {
            _suppressThemeEvent = true;
            ThemeSelector.SelectedValue = t.Id;
            _suppressThemeEvent = false;
        }
    }

    // Title-bar quick toggle: flip between the last light and last dark theme the user picked.
    private void OnToggleTheme(object sender, RoutedEventArgs e) =>
        ApplyTheme(_dark ? _lastLightId : _lastDarkId);

    // Settings dropdown: apply the chosen theme immediately.
    private void OnThemeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeEvent) return;
        if (ThemeSelector.SelectedValue is string id) ApplyTheme(id);
    }

    // ------------------------------------------------------------------
    // Native title-bar (chrome) dark mode, kept in step with the app theme
    // ------------------------------------------------------------------

    /// <summary>Whether the active theme is dark, so owned dialogs (e.g. the Export dialog) can
    /// paint their own native title bar to match instead of defaulting to a white one.</summary>
    internal bool CurrentThemeIsDark => _dark;

    /// <summary>Paint the native window title bar dark (or light) to match the app theme.</summary>
    private void ApplyTitleBarTheme(bool dark) => Services.TitleBarTheme.Apply(this, dark);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // The HWND exists now, so apply the current theme's title-bar color (the ctor's ApplyTheme
        // ran before the window was sourced).
        ApplyTitleBarTheme(_dark);
        InitChrome();
    }

    // ------------------------------------------------------------------
    // Custom background image (behind the content, over the active theme)
    // ------------------------------------------------------------------

    /// <summary>Show (or hide) the background image named <paramref name="fileName"/> from the
    /// Themes folder, and sync the Settings controls. A missing/blank name clears it.</summary>
    private void ApplyBackground(string? fileName)
    {
        var path = ThemeAssets.Resolve(fileName);
        if (path is null)
        {
            BgImage.Source = null;
            BgImage.Visibility = Visibility.Collapsed;
            ClearBgButton.Visibility = Visibility.Collapsed;
            BgFileText.Visibility = Visibility.Collapsed;
            BgOpacityRow.Visibility = Visibility.Collapsed;
            ApplyBackgroundScrims(false);
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;                 // don't lock the file
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;   // pick up a replaced file
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            BgImage.Source = bmp;
        }
        catch
        {
            // Unreadable/corrupt (or a WebP that slipped through), fail safe to no background.
            BgImage.Source = null;
            BgImage.Visibility = Visibility.Collapsed;
            ClearBgButton.Visibility = Visibility.Collapsed;
            BgFileText.Visibility = Visibility.Collapsed;
            BgOpacityRow.Visibility = Visibility.Collapsed;
            ApplyBackgroundScrims(false);
            return;
        }

        BgImage.Opacity = BgOpacitySlider.Value;
        BgImage.Visibility = Visibility.Visible;
        ApplyBackgroundScrims(true);
        ClearBgButton.Visibility = Visibility.Visible;
        BgFileText.Text = System.IO.Path.GetFileName(path);
        BgFileText.Visibility = Visibility.Visible;
        BgOpacityRow.Visibility = Visibility.Visible;
        BgOpacityValue.Text = $"{BgOpacitySlider.Value * 100:0}%";
    }

    private void OnChooseBackground(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a background image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            CheckFileExists = true,
            // Open in the Themes folder by default so images already dropped there are easy to pick
            // (re-picking one that's already there now works, it's used in place, not re-copied).
            InitialDirectory = ThemeAssets.Folder(),
        };
        if (dlg.ShowDialog(this) != true) return;

        if (ThemeAssets.IsWebp(dlg.FileName))
        {
            MessageBox.Show(this,
                "That file is a WebP image saved with a .png/.jpg name. Windows apps can't display " +
                "WebP directly, re-save it as a real PNG or JPG (e.g. open it in Paint and \"Save as\"), " +
                "then pick it again.",
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string fileName;
        try
        {
            fileName = ThemeAssets.Import(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't copy that image into the Themes folder.\n\n" + ex.Message,
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyBackground(fileName);
        if (BgImage.Source is null)   // ApplyBackground bailed → decode failed
        {
            MessageBox.Show(this, "That image couldn't be loaded. Try a different PNG or JPG.",
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.BackgroundImage = fileName;
    }

    private void OnClearBackground(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundImage = "";
        ApplyBackground(null);
    }

    private void OnBgOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BgImage is null) return;   // during template init
        BgImage.Opacity = e.NewValue;
        // Persistence is snapshotted from the live slider in OnClosing, deliberately NOT written to
        // _settings here, so the default-value ValueChanged that fires during InitializeComponent
        // can't clobber the opacity loaded from settings before the ctor restores it.
        if (BgOpacityValue is not null) BgOpacityValue.Text = $"{e.NewValue * 100:0}%";
    }

    // Auto-scan on launch so the master/detail is populated immediately.
    private async void OnWindowLoaded(object sender, RoutedEventArgs e) => await RunScan();

    // ------------------------------------------------------------------
    // Scan
    // ------------------------------------------------------------------

    private async void OnScanClick(object sender, RoutedEventArgs e) => await RunScan();

    private async Task RunScan()
    {
        if (_busy) return;
        _busy = true;
        _oem = default;
        _stability = null;
        _sysInfo = null;
        _scanFinished = false;
        UpdateSystemPanel();
        UpdateDriverTiles();      // no numbers until this scan has finished
        ScanButton.IsEnabled = false;
        StatusLine.Text = "Scanning hardware…";
        AppendConsole("---- new scan ----");
        _rows.Clear();
        _unmatched.Clear();
        EmptyDetail.Text = "Scanning your hardware…";
        EmptyDetail.Visibility = Visibility.Visible;
        DetailScroll.Visibility = Visibility.Collapsed;
        UpdateSummary();

        // Animated "working" band (a WMI scan has no measurable total) with the live scan log as its caption.
        var scanTask = BeginTask("Scanning hardware…");
        IProgress<string> log = ConsoleAndTask(scanTask);

        try
        {
            await _definitions.LoadAsync(log);

            // System identity (maker/model/OS/CPU/RAM) for the sidebar summary + export.
            _sysInfo = await HardwareScanner.ScanSystemInfoAsync(log);
            UpdateSystemPanel();

            var devices = await HardwareScanner.ScanAsync(log);

            // A motherboard / laptop chassis has no PnP hardware ID of its own, so it's
            // resolved separately and, if recognized, shown as its own top row.
            var systemIdentity = await HardwareScanner.ScanSystemIdentityAsync(log);
            if (systemIdentity is not null)
            {
                var sv = _definitions.MatchSystemVendor(systemIdentity.Manufacturer);
                if (sv is not null)
                {
                    _oem = (systemIdentity.Manufacturer, sv.Domain, sv.SupportPage, systemIdentity.IsLaptop);
                    var name = $"{systemIdentity.Manufacturer} {systemIdentity.Model}".Trim();
                    var fallback = new VendorFallback { Name = name, Domain = sv.Domain, SupportPage = sv.SupportPage };
                    var hw = new ScannedDevice
                    {
                        Name = name,
                        PnpClass = systemIdentity.Category,
                        Bus = "SYS",
                        RawHardwareId = systemIdentity.Category.ToUpperInvariant(),
                    };
                    var sysRow = new DeviceRow(hw, null, fallback);
                    sysRow.PropertyChanged += OnRowChanged;
                    _rows.Add(sysRow);
                }
            }

            // Group scanned devices by the curated definition (or vendor fallback) they
            // matched; devices with no curated match are omitted rather than shown.
            var groups = new List<(Definition? def, VendorFallback? fb, List<ScannedDevice> hw)>();
            var groupIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Parts built by a chip/ODM maker that doesn't publish public drivers (a laptop's Chicony webcam, a codec)
            // get their driver from the PC maker. On a recognised OEM system (laptop or prebuilt) that covers every
            // part; on a DIY motherboard only parts ON the board (PCI, audio), since the board maker didn't build a
            // USB webcam someone plugged in.
            var oemSystem = systemIdentity is not null ? _definitions.MatchSystemVendor(systemIdentity.Manufacturer) : null;
            bool RoutesToOem(ScannedDevice d) => oemSystem is not null
                && (systemIdentity!.Category == "System" || !string.Equals(d.Bus, "USB", StringComparison.OrdinalIgnoreCase));

            foreach (var hw in devices)
            {
                var (def, fb) = _definitions.Match(hw);
                if (def is null && fb is null && oemSystem is { } maker && RoutesToOem(hw) && _definitions.MatchOemPart(hw) is { } part)
                {
                    fb = new VendorFallback
                    {
                        Ven = hw.Vendor,
                        Name = $"{part.Maker} part for {ShortMaker(systemIdentity!.Manufacturer)}",
                        Domain = maker.Domain,
                        SupportPage = maker.SupportPage,
                        OemChipMaker = part.Maker,
                    };
                }
                if (def is null && fb is null) { _unmatched.Add(hw); continue; }

                var key = def is not null ? $"D:{def.Label}" : fb!.IsOemRoute ? $"O:{hw.Bus}|{hw.Vendor}" : $"F:{fb!.Name}";
                if (groupIndex.TryGetValue(key, out var idx)) groups[idx].hw.Add(hw);
                else { groupIndex[key] = groups.Count; groups.Add((def, fb, new List<ScannedDevice> { hw })); }
            }

            int updates = 0;
            var preCheckedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (def, fb, hwList) in groups)
            {
                var representative = PickRepresentative(hwList);
                // Pass the detected device so the resolver can cap a legacy GPU's driver list.
                var resolved = def is not null ? await _resolver.ResolveAsync(def, representative, log) : null;

                var row = new DeviceRow(representative, def, fb, hwList, resolved)
                {
                    ShowOlderVersions = OlderVersionsToggle.IsChecked == true,
                };
                // The card maker's own tool (GPU Tweak, Afterburner...) joins the optional software. Not on laptops:
                // there the SUBSYS ID is the laptop maker's, and its desktop card tools don't apply.
                if (def is not null && systemIdentity?.IsLaptop != true)
                    foreach (var t in _definitions.PartnerToolsFor(representative))
                        if (!row.OptionalTools.Any(o => string.Equals(o.Url, t.Url, StringComparison.OrdinalIgnoreCase)))
                            row.OptionalTools.Add(t);
                row.PropertyChanged += OnRowChanged;
                if (row.StatusKind == "update" && row.HasDirectUrl && preCheckedUrls.Add(row.DirectUrl))
                {
                    row.IsSelected = true;
                    updates++;
                }
                _rows.Add(row);
            }

            ((IProgress<string>)log).Report(
                $"> {groups.Count} recognized component(s) from {devices.Count} scanned devices · {updates} downloadable updates");
            if (_unmatched.Count > 0)
                ((IProgress<string>)log).Report(
                    $"> {_unmatched.Count} device(s) matched no definition (usually in-box drivers), " +
                    "tick \"unrecognized hardware\" in Export to list them");

            StatusLine.Text = $"{AppVersion} · definitions: {_definitions.Source} ({_definitions.Manifest.Updated})";

            // Open on the first device that has an actionable driver download, so the
            // detail pane lands on something useful rather than a page-only entry.
            var firstInteresting = _rows.FirstOrDefault(r => r.HasDirectUrl) ?? _rows.FirstOrDefault();
            if (firstInteresting is not null) DeviceList.SelectedItem = firstInteresting;
            else
            {
                EmptyDetail.Text = "No recognized devices found. Everything on this PC uses a Windows in-box driver.";
                EmptyDetail.Visibility = Visibility.Visible;
            }
            UpdateSummary();
            _scanFinished = true;
            UpdateDriverTiles();

            // Recent stability (unexpected shutdowns / BSODs / GPU resets), for the
            // summary tiles and the optional crash section of an exported report.
            _stability = await StabilityScanner.ScanAsync(30, log);
            UpdateStabilityBar();
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"{AppVersion} · scan failed";
            MessageBox.Show(this, "The hardware scan failed.\n\n" + ex.Message,
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            scanTask.Done();
            _busy = false;
            ScanButton.IsEnabled = true;
            UpdateSummary();
        }
    }

    private static ScannedDevice PickRepresentative(List<ScannedDevice> devices)
    {
        static int Rank(string c) => c switch
        {
            "Display" => 0, "Net" => 1, "Bluetooth" => 1, "Media" => 2, "Image" => 2, "Camera" => 2,
            "USB" => 3, "HIDClass" => 3, "Mouse" => 3, "Keyboard" => 3,
            "SCSIAdapter" => 4, "HDC" => 5, "System" => 6, _ => 9,
        };
        return devices.OrderBy(d => Rank(d.PnpClass)).First();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceRow.IsSelected) or nameof(DeviceRow.SelectedVersion))
            UpdateSummary();
    }

    /// <summary>The PC's model + specs live in the Devices &amp; Drivers subtitle (1.4.0; it was a sidebar card).</summary>
    private void UpdateSystemPanel()
    {
        if (_currentSection != "drivers") return;
        PageSubtitleText.Text = DriversSubtitle();
        PageSubtitleText.ToolTip = PageSubtitleText.Text;
    }

    /// <summary>Stability is shown as a summary tile on Devices &amp; Drivers and Health &amp; Repair (1.4.0; it was a
    /// strip under the title bar on every page).</summary>
    private void UpdateStabilityBar()
    {
        UpdateDriverTiles();
        UpdateHealthTiles();
        // The Health grid has a Stability card; show it as soon as the stability read lands.
        if (_health is not null && HealthContent.Children.Count > 0) RenderHealth(_health);
    }

    // ------------------------------------------------------------------
    // Master list / selection
    // ------------------------------------------------------------------

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = DeviceList.SelectedItem as DeviceRow;
        var has = row is not null;
        DetailScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        EmptyDetail.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

        // Laptop OEM guidance, for the device classes where the PC maker's own,
        // validated driver is usually preferable to a generic vendor one.
        // A part routed to the PC maker says so plainly: who made the chip and why the driver comes from the PC maker.
        if (has && row!.Fallback is { IsOemRoute: true } route)
        {
            var maker = ShortMaker(_oem.Maker.Length > 0 ? _oem.Maker : route.Name);
            OemGuidanceText.Text =
                $"{route.OemChipMaker} made this part for {maker}, and its driver comes from {maker}, tuned for this PC. " +
                $"Get it from {route.Domain}.";
            OemGuidanceBar.Tag = route.SupportPage;
            OemGuidanceBar.Visibility = Visibility.Visible;
            return;
        }

        var showOem = has && _oem.IsLaptop && !string.IsNullOrEmpty(_oem.Support)
            && row!.Category is "Display" or "Net" or "Bluetooth" or "Media" or "Image" or "Camera";
        OemGuidanceBar.Visibility = showOem ? Visibility.Visible : Visibility.Collapsed;
        if (showOem)
        {
            var maker = ShortMaker(_oem.Maker);
            // Phrased without an article, "a/an" can't be picked correctly for brand names and
            // acronyms alike ("an Acer", "an HP", "a Dell").
            OemGuidanceText.Text =
                $"{maker} laptop detected. For {CategoryWord(row!.Category)}, {maker}'s own validated driver " +
                $"is often the safer choice, check {_oem.Domain} first if you hit issues.";
            OemGuidanceBar.Tag = _oem.Support;
        }
    }

    private static string ShortMaker(string manufacturer)
    {
        var m = manufacturer.Trim();
        foreach (var suffix in new[] { " Inc.", " Inc", " Corporation", " Co., Ltd.", " Computer Inc.", " COMPUTER INC." })
            if (m.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                m = m[..^suffix.Length];
        return m.Split(' ')[0];   // e.g. "Dell", "ASUSTeK", "Hewlett-Packard"
    }

    private static string CategoryWord(string category) => category switch
    {
        "Display" => "graphics",
        "Net" => "network / Wi-Fi",
        "Bluetooth" => "Bluetooth",
        "Media" => "audio",
        "Image" or "Camera" => "the camera",
        _ => "this device",
    };

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text;
        _view?.Refresh();
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        var directs = _rows.Where(r => r.HasDirectUrl).ToList();
        var allOn = directs.Count > 0 && directs.All(r => r.IsSelected);
        foreach (var r in directs) r.IsSelected = !allOn;
        UpdateSummary();
    }

    private void OnOlderVersionsToggle(object sender, RoutedEventArgs e)
    {
        var show = OlderVersionsToggle.IsChecked == true;
        foreach (var row in _rows) row.ShowOlderVersions = show;
    }

    private void OnVersionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: DeviceRow row } cb &&
            cb.SelectedItem is VersionEntry v && row.VisibleVersions.Contains(v))
        {
            row.SelectedVersion = v;
        }
    }

    private void OnToggleLog(object sender, RoutedEventArgs e) => LogPopup.IsOpen = !LogPopup.IsOpen;

    // ------------------------------------------------------------------
    // Copy / export
    // ------------------------------------------------------------------

    // Copies just the device name, the thing you'd paste into a search box. The full per-device
    // details (hardware IDs, installed version, source URL) live in the Export report instead.
    private void OnCopyInfo(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRow r || string.IsNullOrWhiteSpace(r.Name)) return;
        TrySetClipboard(r.Name);
        StatusLine.Text = $"Copied “{r.Name}” to clipboard";
    }

    private async void OnExportReport(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            // Used to silently do nothing, which looked like a broken button.
            MessageBox.Show(this, _busy
                    ? "The hardware scan is still running. Export will be ready as soon as it finishes."
                    : "There's nothing to export yet: the hardware scan hasn't produced any devices. Try Rescan, then Export.",
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var opts = new ExportDialog { Owner = this };
        if (opts.ShowDialog() != true) return;
        var includeStability = opts.IncludeStability;
        var includeUnrecognized = opts.IncludeUnrecognized;
        var includeHealth = opts.IncludeHealth;
        var includeCsv = opts.IncludeCsv;
        var includeJson = opts.IncludeJson;
        var days = opts.Days;
        var stabilityGroups = opts.StabilityGroups;
        var includeReliability = opts.IncludeReliability;
        var verboseStability = opts.VerboseStability;
        var includeLog = opts.IncludeLog;
        // No Save-As dialog: reports auto-file into <Venquery folder or Downloads>\SystemReports\<model>\<date>\
        // (see the write block below), keeping a dated per-machine history.

        // Snapshot current state before any await, so a rescan mid-export can't corrupt the report.
        var sys = _sysInfo;
        var rows = _rows.ToList();
        var unmatched = _unmatched.ToList();

        // A section that can't be read is noted in the report instead of aborting the whole export.
        var readFailures = new List<string>();

        // Honor the chosen look-back window; reuse the 30-day scan if that's what was picked.
        StabilityReport? stability = null;
        if (includeStability)
        {
            StatusLine.Text = $"Reading stability history · last {days} days…";
            // Reuse the launch scan only if it captured the same scope (30 days, no reliability pass).
            try
            {
                stability = (days == 30 && !includeReliability && _stability is not null)
                    ? _stability
                    : await StabilityScanner.ScanAsync(days, null, includeReliability);
            }
            catch (Exception ex) { readFailures.Add("stability history: " + ex.Message); AppendConsole("  export: stability read failed, " + ex.Message); }
        }

        HealthReport? health = null;
        if (includeHealth)
        {
            StatusLine.Text = "Reading system health…";
            try { health = _health ?? await HealthScanner.ScanAsync(null); }
            catch (Exception ex) { readFailures.Add("system health: " + ex.Message); AppendConsole("  export: health read failed, " + ex.Message); }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{Edition.Current.DisplayName} {AppVersion}, system & driver report");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine(Venquery.Services.AppLog.Header());
        sb.AppendLine(new string('=', 60));

        if (sys is not null && sys.HasAnything)
        {
            sb.AppendLine("SYSTEM");
            if (!string.IsNullOrWhiteSpace(sys.Manufacturer)) sb.AppendLine($"  Manufacturer : {sys.Manufacturer}");
            if (!string.IsNullOrWhiteSpace(sys.Model))        sb.AppendLine($"  Model        : {sys.Model}");
            if (!string.IsNullOrWhiteSpace(sys.Type))         sb.AppendLine($"  Type         : {sys.Type}");
            if (!string.IsNullOrWhiteSpace(sys.Os))           sb.AppendLine($"  OS           : {sys.Os}");
            if (!string.IsNullOrWhiteSpace(sys.Cpu))          sb.AppendLine($"  Processor    : {sys.Cpu}");
            if (!string.IsNullOrWhiteSpace(sys.Ram))          sb.AppendLine($"  Memory       : {sys.Ram}");
            sb.AppendLine(new string('=', 60));
        }

        foreach (var r in rows)
        {
            sb.AppendLine();
            sb.AppendLine($"{r.Name}  [{r.Category}]");
            sb.AppendLine($"  Installed : {r.InstalledVersion}{(r.HasInstalledDate ? $"  ({r.InstalledDateText})" : "")}");
            sb.AppendLine($"  Status    : {r.StatusText}");
            if (r.HasVerifiedSource) sb.AppendLine($"  Source    : {r.Domain}");
            foreach (var id in r.AllHardwareIds) sb.AppendLine($"  HWID      : {id}");
            if (r.HasDirectUrl) sb.AppendLine($"  Download  : {r.DirectUrl}");
            else if (r.HasSupportPage) sb.AppendLine($"  Support   : {r.SupportPage}");
        }

        if (includeHealth && health is not null)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine(health.ToReportText());
        }

        if (includeStability && stability is not null)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine(stability.ToReportText(stabilityGroups, includeReliability, verboseStability));
        }

        if (readFailures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("SECTIONS THAT COULDN'T BE READ");
            foreach (var f in readFailures) sb.AppendLine("  " + f);
        }

        if (includeUnrecognized)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("UNRECOGNIZED HARDWARE");
            sb.AppendLine("Devices that didn't match Venquery's driver database. Most use a built-in");
            sb.AppendLine("Windows driver and need nothing, this list is only for troubleshooting or");
            sb.AppendLine("looking up an unknown hardware ID (e.g. at devicehunt.com).");
            sb.AppendLine(new string('-', 60));
            if (unmatched.Count == 0)
                sb.AppendLine("  (none, every scanned device matched a known vendor or definition)");
            else
                foreach (var d in unmatched.OrderBy(d => d.PnpClass, StringComparer.OrdinalIgnoreCase)
                                           .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine();
                    sb.AppendLine($"  {(string.IsNullOrWhiteSpace(d.Name) ? "(unnamed device)" : d.Name)}  [{d.PnpClass}]");
                    sb.AppendLine($"      {d.RawHardwareId}");
                }
        }

        try
        {
            // Auto-file into SystemReports\<model>\<date>\, next to Venquery (or Downloads if that's read-only).
            var now = DateTime.Now;
            var modelRaw = string.IsNullOrWhiteSpace(sys?.Model) ? Environment.MachineName : sys!.Model;
            var model = OutputLocation.SafeName(modelRaw);
            var dir = ReportFolder(now);
            var stamp = now.ToString("HHmmss");
            var enc = new System.Text.UTF8Encoding(true);   // BOM so ▲ / · always render in any editor

            var txtPath = System.IO.Path.Combine(dir, $"Venquery-report-{stamp}.txt");
            System.IO.File.WriteAllText(txtPath, sb.ToString(), enc);
            var written = new List<string> { "report.txt" };

            if (includeCsv)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"devices-{stamp}.csv"), BuildDevicesCsv(rows), enc);
                written.Add("devices.csv");
            }
            if (includeJson)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"report-{stamp}.json"), BuildReportJson(sys, rows, now), enc);
                written.Add("report.json");
            }
            if (includeLog)
            {
                SaveSessionLogInto(dir);
                written.Add("log");
            }

            RevealInExplorer(txtPath);   // opens the dated folder with the report selected
            StatusLine.Text = $"Exported {string.Join(" + ", written)} · SystemReports\\{model}\\{now:yyyy-MM-dd}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not write the report.\n\n" + ex.Message, Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // A CSV of the device inventory (the driver table) for opening in a spreadsheet.
    private static string BuildDevicesCsv(List<DeviceRow> rows)
    {
        static string C(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Name,Category,Installed,Status,Source,HardwareIDs,Download");
        foreach (var r in rows)
        {
            var url = r.HasDirectUrl ? r.DirectUrl : (r.HasSupportPage ? r.SupportPage : "");
            sb.AppendLine(string.Join(",", C(r.Name), C(r.Category), C(r.InstalledVersion),
                C(r.StatusText), C(r.HasVerifiedSource ? r.Domain : ""),
                C(string.Join("; ", r.AllHardwareIds)), C(url)));
        }
        return sb.ToString();
    }

    // A machine-readable JSON report (system + device inventory) for automation. Mirrors the shape of the
    // headless CLI's --report --json.
    private static string BuildReportJson(SystemInfo? sys, List<DeviceRow> rows, DateTime now)
    {
        var payload = new
        {
            tool = "Venquery",
            version = AppVersion,
            generated = now.ToString("yyyy-MM-dd HH:mm:ss"),
            system = sys is null ? null : new
            {
                manufacturer = sys.Manufacturer, model = sys.Model, type = sys.Type,
                os = sys.Os, cpu = sys.Cpu, memory = sys.Ram,
            },
            devices = rows.Select(r => new
            {
                name = r.Name, category = r.Category, installed = r.InstalledVersion, status = r.StatusText,
                source = r.HasVerifiedSource ? r.Domain : null,
                hardwareIds = r.AllHardwareIds.ToList(),
                download = r.HasDirectUrl ? r.DirectUrl : null,
                support = r.HasSupportPage ? r.SupportPage : null,
            }).ToList(),
        };
        return System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    internal static void TrySetClipboard(string text)
    {
        // The Windows clipboard is a shared, briefly-lockable resource, another app (or a clipboard
        // manager) can hold it for a moment. A couple of quick retries makes a single Copy reliable
        // instead of silently no-op'ing while the status line claims success.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch when (attempt < 4) { System.Threading.Thread.Sleep(60); }
            catch { /* give up quietly after the last attempt */ }
        }
    }

    private void UpdateSummary()
    {
        var directs = _rows.Where(r => r.HasDirectUrl).ToList();
        var uniqueFiles = _rows.Where(r => r.IsSelected && r.HasDirectUrl)
                               .Select(r => r.DirectUrl)
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .Count();

        HardwareCount.Text = $"HARDWARE ({_rows.Count})";
        DownloadLabel.Text = uniqueFiles switch
        {
            0 => "Download selected",
            1 => "Download 1 file",
            _ => $"Download {uniqueFiles} files",
        };
        DownloadButton.IsEnabled = uniqueFiles > 0 && !_busy;

        var allOn = directs.Count > 0 && directs.All(r => r.IsSelected);
        SelectAllButton.Content = allOn ? "Clear all" : "Select All";
    }

    // ------------------------------------------------------------------
    // Download
    // ------------------------------------------------------------------

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _rows.Where(r => r.IsSelected && r.HasDirectUrl).ToList();
        if (selected.Count == 0) return;

        // Collapse rows that point at the exact same file into one download.
        var groups = new List<(string Url, List<DeviceRow> Rows)>();
        var byUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in selected)
        {
            if (byUrl.TryGetValue(r.DirectUrl, out var idx)) groups[idx].Rows.Add(r);
            else { byUrl[r.DirectUrl] = groups.Count; groups.Add((r.DirectUrl, new List<DeviceRow> { r })); }
        }

        _busy = true;
        ScanButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        DownloadLabel.Text = "Downloading…";

        int total = groups.Count, done = 0;
        var t = BeginTask(total == 1 ? "Starting download…" : $"Starting download of {total} files…", determinate: true);

        foreach (var (url, rows) in groups)
        {
            var primary = rows[0];
            foreach (var r in rows) { r.IsDownloading = true; r.Progress = 0; r.ProgressText = "connecting…"; }

            int completedSoFar = done;
            var progress = new Progress<(double pct, string text)>(p =>
            {
                foreach (var r in rows) { r.Progress = p.pct; r.ProgressText = p.text; }
                var overall = (completedSoFar + (p.pct > 0 ? p.pct / 100.0 : 0)) / total * 100.0;
                t.Report(overall, total == 1
                    ? $"Downloading {primary.Name}, {p.text}"
                    : $"Downloading {done + 1} of {total} · {primary.Name}, {p.text}");
            });

            var result = await _downloader.DownloadAsync(url, primary.Definition?.Referer, progress);

            foreach (var r in rows) r.IsDownloading = false;
            done++;
            t.Report((double)done / total * 100.0);

            if (result.Ok)
            {
                var expected = primary.SelectedVersion?.Sha256;
                var mismatch = !string.IsNullOrEmpty(expected) &&
                               !string.Equals(expected, result.Sha256, StringComparison.OrdinalIgnoreCase);
                var savedName = System.IO.Path.GetFileName(result.Path);

                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    if (mismatch)
                    {
                        r.ProgressText =
                            $"⚠ CHECKSUM MISMATCH, expected {Short(expected!)} got {Short(result.Sha256)}. " +
                            "Do not run this file. It was kept on disk for inspection only.";
                    }
                    else
                    {
                        r.ProgressText = i == 0
                            ? $"✓ saved {savedName}{SignatureNote(result, savedName)} · sha256 {Short(result.Sha256)}"
                            : $"✓ covered by {savedName} (one download shared across these devices)";
                        r.IsSelected = false;
                    }
                }
            }
            else
            {
                foreach (var r in rows)
                {
                    r.Progress = 0;
                    r.ProgressText = $"✕ download failed: {result.Error}, try the support page link";
                }
            }
        }

        // A checksum mismatch writes a "⚠ …" row and must NOT be reported as a clean success,
        // count it separately from an outright "✕" download failure.
        var failed = groups.Count(g => g.Rows[0].ProgressText.StartsWith('✕'));
        var flagged = groups.Count(g => g.Rows[0].ProgressText.StartsWith('⚠'));
        var summary = (failed, flagged) switch
        {
            (0, 0) => total == 1
                ? "✓ Download complete, saved to your Downloads folder"
                : $"✓ All {total} downloads complete, saved to your Downloads folder",
            (0, _) => $"Finished · {total - flagged} of {total} verified · " +
                      $"{flagged} checksum mismatch, do not run those files",
            _ => $"Finished · {total - failed - flagged} of {total} downloaded · {failed} failed" +
                 (flagged > 0 ? $" · {flagged} checksum mismatch" : ""),
        };
        t.Done(summary);

        _busy = false;
        ScanButton.IsEnabled = true;
        UpdateSummary();
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] + "…" : hash;

    /// <summary>Human phrase for the downloaded file's Authenticode signature.</summary>
    private static string SignatureNote(DownloadResult r, string fileName)
    {
        if (r.SignatureValid && !string.IsNullOrEmpty(r.Signer))
            return $" · ✓ signed by {r.Signer}";
        if (r.HasSignature)
            return $" · ⚠ signed by {r.Signer} but signature NOT verified";

        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".exe" or ".msi" or ".msu")
            return " · ⚠ UNSIGNED, verify the source before running";
        return "";   // archives etc. aren't signed; don't imply a problem
    }

    // ------------------------------------------------------------------
    // Links / folder
    // ------------------------------------------------------------------

    private void OnDirectFileClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string url && !string.IsNullOrWhiteSpace(url))
            OpenExternal(url);
    }

    /// <summary>Opens a web link in the browser, or a harmless local document with its default app. Anything
    /// else (a script, an exe, a shortcut, an unknown scheme) is only REVEALED in Explorer, never opened: a
    /// shell "open" of a .ps1/.bat/.exe could run it (e.g. where .ps1 is set to run with PowerShell), and
    /// links come partly from data files.</summary>
    internal static void OpenExternal(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            if (IsWebLink(target))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }
            if (System.IO.File.Exists(target))
            {
                var ext = System.IO.Path.GetExtension(target);
                if (SafeToOpenExts.Contains(ext))
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("explorer.exe"), $"/select,\"{target}\"") { UseShellExecute = true });
                return;
            }
            if (System.IO.Directory.Exists(target))
                Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("explorer.exe"), $"\"{target}\"") { UseShellExecute = true });
        }
        catch { /* non-fatal: the link is still visible for manual copying */ }
    }

    internal static bool IsWebLink(string target)
        => Uri.TryCreate(target, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    private static readonly HashSet<string> SafeToOpenExts = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".csv", ".json", ".log", ".md", ".html", ".htm", ".pdf", ".png", ".jpg", ".jpeg" };

    // ------------------------------------------------------------------
    // Nav rail (Drivers / Backup & Restore / Portable Apps)
    // ------------------------------------------------------------------

    /// <summary>Left-hand mode: put the nav rail on the right (swap the two body columns) or the left.</summary>
    private void ApplyNavSide(bool right)
    {
        // The content layer's rounded corner and edge line always face the rail.
        if (right)
        {
            BodyCol0.Width = new GridLength(1, GridUnitType.Star);
            BodyCol1.Width = new GridLength(RailWidth);
            Grid.SetColumn(NavRail, 1);
            Grid.SetColumn(ContentLayer, 0);
            ContentLayer.CornerRadius = new CornerRadius(0, 14, 0, 0);
            ContentLayer.BorderThickness = new Thickness(0, 1, 1, 0);
        }
        else
        {
            BodyCol0.Width = new GridLength(RailWidth);
            BodyCol1.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(NavRail, 0);
            Grid.SetColumn(ContentLayer, 1);
            ContentLayer.CornerRadius = new CornerRadius(14, 0, 0, 0);
            ContentLayer.BorderThickness = new Thickness(1, 1, 0, 0);
        }
    }

    private void OnNavSideToggled(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ApplyNavSide(NavRightToggle.IsChecked == true);
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        // The default RadioButton fires Checked during XAML load, before the other
        // views exist, ignore until the window is fully initialized.
        if (!IsInitialized) return;
        if (sender is System.Windows.Controls.RadioButton { Tag: string section }) ShowSection(section);
    }

    private void SwitchView(string view)
    {
        _currentView = view;
        var drivers = view == "drivers";
        DriversView.Visibility = drivers ? Visibility.Visible : Visibility.Collapsed;
        // "restore" is the Restore tab of the Backup view; "space" is the "What's using space" tab of Organize.
        BackupView.Visibility = view is "backup" or "restore" ? Visibility.Visible : Visibility.Collapsed;
        PortableView.Visibility = view == "portable" ? Visibility.Visible : Visibility.Collapsed;
        DocumentsView.Visibility = view == "documents" ? Visibility.Visible : Visibility.Collapsed;
        OrganizerHost.Visibility = view is "organize" or "space" ? Visibility.Visible : Visibility.Collapsed;
        ProToolsHost.Visibility = view == "protools" ? Visibility.Visible : Visibility.Collapsed;
        HealthView.Visibility = view == "health" ? Visibility.Visible : Visibility.Collapsed;
        StartupView.Visibility = view == "startup" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = view == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (view is "backup" or "restore") SetBackupTab(restore: view == "restore");
        if (view is "organize" or "space") ShowOrganizerMode(space: view == "space");

        // The footer (Rescan / older drivers / Download) belongs to Devices & Drivers. The task band is NOT
        // page-specific (1.4.0 fix): BeginTask/Done alone show and hide it, so a running backup, restore or
        // organize keeps its progress visible on whatever page it was started from.
        FooterBar.Visibility = drivers ? Visibility.Visible : Visibility.Collapsed;

        // The title-bar Safe Mode pill: the pages that can launch a guarded script or make a confirmed,
        // reversible change (Repair Kit, System Health's SFC/DISM + restore point, and Startup &
        // Tasks' enable/disable) get the worded caution; every other view keeps the plain write-only message.
        // (Prepare for Reset is a modal, not a page, it carries its own messaging.)
        if (view == "organize")
        {
            // The one page that MOVES the user's existing files: always previewed, confirmed and undoable.
            SafeModeDescriptor.Text = " Nothing moves without your confirmation.";
            SafeModeDescriptor.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
        }
        else if (view is "protools" or "health" or "startup" or "backup" or "restore")
        {
            SafeModeDescriptor.Text = " Nothing runs without your confirmation.";
            SafeModeDescriptor.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
        }
        else if (view is "portable" or "documents")
        {
            // Scans only read names/sizes; a backup copies to the folder you chose (never moves or deletes).
            SafeModeDescriptor.Text = " Scans only read. Backups copy, nothing is moved or deleted.";
            SafeModeDescriptor.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
        }
        else if (view == "space")
        {
            // "What's using space" only reads sizes.
            SafeModeDescriptor.Text = " Read-only, nothing is changed.";
            SafeModeDescriptor.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
        }
        else
        {
            SafeModeDescriptor.Text = " No auto-install protocol. Files are only downloaded.";
            SafeModeDescriptor.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
        }

        // Full wording on hover (the pill trims its text on a narrow window).
        SafeModePill.ToolTip = "Safe Mode:" + SafeModeDescriptor.Text;

        if (view == "backup") { UpdateBackupMachineNote(); EnsureAppsLoaded(); }
        if (view == "health") EnsureHealthLoaded();
        if (view == "startup") EnsureStartupLoaded();
        if (view is "portable" or "documents") ShowBackupDestinations();
        if (view == "portable") RebuildFolderBar(PortableFolderBar, _settings.PortableScanFolders, UpdatePortableScanEnabled);
        if (view == "documents") RebuildFolderBar(DocFolderBar, _settings.DocScanFolders, UpdateDocScanEnabled);
        if (view == "portable") { RebuildDriveBar(PortableDriveBar, _portableDriveChecks, UpdatePortableScanEnabled, false); UpdatePortableScanEnabled(); }
        if (view == "documents") { RebuildDriveBar(DocDriveBar, _docDriveChecks, UpdateDocScanEnabled, true); UpdateDocScanEnabled(); }
    }

    /// <summary>A logger that appends to the shared scan console (same as the hardware scan),
    /// so every winget/portable action is visible under "View Log".</summary>
    private IProgress<string> MakeConsoleLog() => new Progress<string>(AppendConsole);

    /// <summary>Adds a timestamped line to the "View Log" console AND to the session log file on disk, so the
    /// record survives a closed window or a crash. The on-screen console is trimmed to its newest ~300 KB; the file
    /// keeps everything.</summary>
    internal void AppendConsole(string line)
    {
        if (line is null) return;
        Venquery.Services.AppLog.Write(line);
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        if (LogBox.Text.Length > 400_000)
        {
            var t = LogBox.Text;
            var cut = t.IndexOf('\n', t.Length - 300_000);
            LogBox.Text = "(older lines trimmed here, the full log is in the log file)" + Environment.NewLine + t.Substring(cut + 1);
        }
        LogBox.ScrollToEnd();
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Venquery.Services.AppLog.Header() + Environment.NewLine + LogBox.Text); StatusLine.Text = "Log copied to the clipboard"; }
        catch (Exception ex) { StatusLine.Text = "Couldn't copy the log: " + ex.Message; }
    }

    /// <summary>Saves the full session log (the file, not just what's on screen) next to the reports:
    /// SystemReports\(model)\(date)enquery-log-(time).txt, and shows it in Explorer.</summary>
    private void OnSaveLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = SaveSessionLogInto(ReportFolder(DateTime.Now));
            LogPopup.IsOpen = false;
            RevealInExplorer(path);
            StatusLine.Text = "Saved the log · " + System.IO.Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the log.\n\n" + ex.Message, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        try { System.IO.Directory.CreateDirectory(Venquery.Services.AppLog.LogsDir); } catch { }
        LogPopup.IsOpen = false;
        OpenExternal(Venquery.Services.AppLog.LogsDir);
    }

    /// <summary>The dated per-machine reports folder: (Venquery folder or Downloads)\SystemReports\(model)\(date).</summary>
    private string ReportFolder(DateTime now)
    {
        var modelRaw = string.IsNullOrWhiteSpace(_sysInfo?.Model) ? Environment.MachineName : _sysInfo!.Model;
        var dir = System.IO.Path.Combine(OutputLocation.Base(), "SystemReports", OutputLocation.SafeName(modelRaw), now.ToString("yyyy-MM-dd"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes the whole session log (falling back to the on-screen console) into <paramref name="dir"/>.</summary>
    private string SaveSessionLogInto(string dir)
    {
        var text = Venquery.Services.AppLog.ReadSession();
        if (string.IsNullOrWhiteSpace(text)) text = Venquery.Services.AppLog.Header() + Environment.NewLine + LogBox.Text;
        var path = System.IO.Path.Combine(dir, $"venquery-log-{DateTime.Now:HHmmss}.txt");
        System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
        return path;
    }

    // ------------------------------------------------------------------
    // Backup & Restore (winget)
    // ------------------------------------------------------------------

#if !LEGACY
    // winget missing (LTSC / IoT Enterprise / Server): tell the user and offer to install it. Venquery only
    // WRITES the installer script (downloads from Microsoft's own endpoints); the user runs it (typed Y, no
    // admin needed for a per-user install).
    private void OfferWingetInstall()
    {
        var (isLtsc, editionId, product) = WingetService.DetectEdition();
        var edLine = isLtsc
            ? $"This looks like a Windows LTSC / IoT Enterprise edition ({(string.IsNullOrWhiteSpace(product) ? editionId : product)}), which ships without the Store and App Installer, so winget isn't here.\n\n"
            : "winget (Microsoft's App Installer) wasn't found on this PC.\n\n";
        var ans = MessageBox.Show(this,
            edLine +
            "Venquery can write a short script that downloads winget and its packages from Microsoft's own " +
            "endpoints and installs them for you (it asks you to type Y first; no administrator needed). " +
            "Write and run it now?",
            "winget isn't installed", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (ans != MessageBoxResult.Yes) return;

        var res = WingetService.WriteInstallScript(DownloadService.DownloadsFolder());
        if (!res.Ok)
        {
            MessageBox.Show(this, "Could not write the installer script.\n\n" + res.Error, Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            WingetService.LaunchInstallScript(res.ScriptPath, code =>
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    // The window closed: re-check (no restart needed, WingetExe() finds the fresh alias).
                    if (await WingetService.IsInstalledAsync())
                        StatusLine.Text = "winget is installed, press Back up to read your installed apps";
                    else if (code == 2)
                        StatusLine.Text = "winget install cancelled, nothing was changed";
                    else
                        StatusLine.Text = "winget didn't install, see venquery-install-winget.log in your Downloads folder";
                })));
            StatusLine.Text = "Launched the winget installer, follow the PowerShell window";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not launch the installer.\n\n" + ex.Message, Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
#endif

    private async void OnCreateBackup(object sender, RoutedEventArgs e)
    {
#if LEGACY
        await System.Threading.Tasks.Task.CompletedTask;   // Backup is disabled on Legacy (no winget)
#else
        if (_wingetBusy) return;

        // winget is missing on LTSC / IoT Enterprise (and Server), notify and offer to install it rather
        // than just failing the scan. Venquery only writes the installer script; the user runs it.
        _wingetBusy = true;
        CreateBackupButton.IsEnabled = false;
        var haveWinget = await WingetService.IsInstalledAsync();
        _wingetBusy = false;
        CreateBackupButton.IsEnabled = true;
        if (!haveWinget)
        {
            OfferWingetInstall();
            return;
        }

        _wingetBusy = true;
        CreateBackupButton.IsEnabled = false;
        BackupEmpty.Visibility = Visibility.Collapsed;
        BackupResultsPanel.Visibility = Visibility.Collapsed;
        BackupSavedBar.Visibility = Visibility.Collapsed;
        BackupStatus.Visibility = Visibility.Visible;
        BackupStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        BackupStatus.Text = "Reading installed apps via winget… this can take a few seconds.";

        // The origin PC's make+model stamps the backup for the restore-script model-lock.
        if (string.IsNullOrEmpty(_machineIdentity))
        {
            try { _machineIdentity = await Task.Run(HardwareScanner.RawMachineIdentity); }
            catch { _machineIdentity = ""; }   // the restore script then refuses to model-lock, it never crashes
        }

        WingetExportResult result;
        var t = BeginTask("Reading installed apps (winget)…");
        try
        {
            result = await _winget.ExportAsync(ConsoleAndTask(t));
        }
        finally
        {
            t.Done();
            _wingetBusy = false;
            CreateBackupButton.IsEnabled = true;
        }

        if (!result.Ok)
        {
            BackupStatus.SetResourceReference(TextBlock.ForegroundProperty, "Red");
            BackupStatus.Text = "Couldn't read installed apps, " + result.Error;
            return;
        }

        _lastExport = result;
        _backupApps.Clear();
        foreach (var a in result.Restorable) { a.PropertyChanged += (_, _) => UpdateBackupSaveLabel(); _backupApps.Add(a); }
        _manualApps.Clear();
        foreach (var a in result.Manual) _manualApps.Add(a);

        BackupCountFound.Text = (result.Restorable.Count + result.Manual.Count).ToString();
        BackupCountRestorable.Text = result.Restorable.Count.ToString();
        BackupCountManual.Text = result.Manual.Count.ToString();
        ManualPanel.Visibility = result.Manual.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ManualToggle.IsChecked = false;   // start collapsed each time, it's secondary detail
        ManualToggle.Content = $"Show {result.Manual.Count} app(s) with no winget package, reinstall manually";
        BackupStatus.Visibility = Visibility.Collapsed;
        BackupResultsPanel.Visibility = Visibility.Visible;
        BackupEmpty.Visibility = Visibility.Collapsed;
        BackupAppsHeader.Text = $"APPS ({result.Restorable.Count + result.Manual.Count})";
        SaveBackupButton.IsEnabled = true;
        UpdateBackupSaveLabel();
        UpdateBackupMachineNote();
        StatusLine.Text = $"{AppVersion} · {result.Restorable.Count} apps ready to back up";
#endif
    }

    private void OnSaveBackup(object sender, RoutedEventArgs e)
    {
#if !LEGACY
        if (_lastExport is null) return;

        var selected = _backupApps.Where(a => a.IsSelected)
            .Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "No apps are ticked, so there's nothing to back up.",
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var res = _winget.WriteBackupFiles(
            _lastExport.RawJson, selected, _manualApps.ToList(), DownloadService.DownloadsFolder(),
            _machineIdentity, _settings.AllowCrossMachineRestore);
        if (!res.Ok)
        {
            MessageBox.Show(this, "Could not write the backup files.\n\n" + res.Error,
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _savedBackupJson = res.JsonPath;
        _savedBackupScript = res.ScriptPath;
        RunCommandBox.Text = RunCommandFor(res.ScriptPath);
        BackupSavedText.Text =
            $"Saved {res.Count} app(s) to {System.IO.Path.GetFileName(res.JsonPath)} and " +
            $"{System.IO.Path.GetFileName(res.ScriptPath)} in your Downloads folder. " +
            "Review the script, then run it yourself to restore, Venquery won't.";
        BackupSavedBar.Visibility = Visibility.Visible;
        StatusLine.Text = $"Saved app backup · {System.IO.Path.GetFileName(res.JsonPath)}";
#endif
    }

    private void OnOpenBackupFolder(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_savedBackupJson)) RevealInExplorer(_savedBackupJson);
    }

    // Opens the restore script in Notepad for READING. The Windows default action for a
    // .ps1 is "Edit", not "Run", but we force Notepad to be unmistakable about it.
    private void OnOpenScript(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_savedBackupScript)) return;
        try { Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("notepad.exe"), $"\"{_savedBackupScript}\"") { UseShellExecute = true }); }
        catch { OpenExternal(_savedBackupScript); }
    }

    // The exact, policy-independent command to run the restore script. "Run with PowerShell"
    // isn't registered on every PC, and the default execution policy often blocks a plain
    // double-click, so we hand the user a command that always works: -ExecutionPolicy Bypass
    // applies to this single invocation only and changes nothing persistent.
    internal static string RunCommandFor(string scriptPath)
        => $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";

    private void OnCopyRunCommand(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_savedBackupScript)) return;
        TrySetClipboard(RunCommandFor(_savedBackupScript));
        StatusLine.Text = "Copied the restore run-command to the clipboard";
    }

    private void OnCrossMachineToggle(object sender, RoutedEventArgs e)
    {
        _settings.AllowCrossMachineRestore = CrossMachineToggle.IsChecked == true;
        _settings.Save();
        CrossMachineWarn.Visibility = _settings.AllowCrossMachineRestore ? Visibility.Visible : Visibility.Collapsed;
        UpdateBackupMachineNote();
    }

    /// <summary>Reflects, on the Backup view, whether the next saved backup will be locked
    /// to this PC or allowed to restore anywhere, driven by the Settings toggle.</summary>
    private void UpdateBackupMachineNote()
    {
        if (_settings.AllowCrossMachineRestore)
        {
            BackupMachineNote.Text =
                "⚠ Cross-PC restore is ON, the saved script will run on any PC model. Change in Settings.";
            BackupMachineNote.SetResourceReference(TextBlock.ForegroundProperty, "Amber");
        }
        else
        {
            var pc = string.IsNullOrEmpty(_machineIdentity) ? "this PC" : _machineIdentity;
            BackupMachineNote.Text =
                $"For safety, this backup will only restore on {pc}. Enable cross-PC restore in Settings to change that.";
            BackupMachineNote.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }
    }

    // ------------------------------------------------------------------
    // Portable Apps indexer
    // ------------------------------------------------------------------

    /// <summary>(Re)build a drive-chip bar from the machine's ready drives, preserving any ticks the user
    /// already made. Called when the Portable/Documents page opens so freshly-inserted drives appear. Nothing
    /// is pre-ticked: no drive (C: included) is scanned until the user picks it. <paramref name="changed"/>
    /// runs on every tick so the page can enable its Scan button.</summary>
    private void RebuildDriveBar(System.Windows.Controls.WrapPanel bar, List<System.Windows.Controls.CheckBox> checks,
        Action changed, bool documents)
    {
        var prev = checks.Where(c => c.IsChecked == true)
                         .Select(c => ((Venquery.Services.DriveChoice)c.Tag).Root)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bar.Children.Clear();
        checks.Clear();
        foreach (var d in Venquery.Services.DriveList.Ready())
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Style = (Style)FindResource("PillCheck"),
                Content = d.Caption,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = d,
                IsChecked = prev.Contains(d.Root),
            };
            if (d.IsOsDrive)
                cb.ToolTip = documents
                    ? "Windows drive: your personal folders (Desktop, Documents, Pictures, Videos, Music, Downloads, Public) plus loose files in the drive root"
                    : "Windows drive: everything except Windows, Program Files, ProgramData and AppData";
            cb.Checked += (_, _) => changed();
            cb.Unchecked += (_, _) => changed();
            checks.Add(cb);
            bar.Children.Add(cb);
        }
    }

    private void OnPortableAllDrives(object sender, RoutedEventArgs e)
    {
        if (PortableDriveBar is not null) PortableDriveBar.IsEnabled = PortableAllDrives.IsChecked != true;
        UpdatePortableScanEnabled();
    }

    /// <summary>The roots a portable scan covers: every ready drive, or exactly the ticked chips. Empty when
    /// nothing is picked, there is no implicit C: / user-folder default.</summary>
    private List<string> PortableScanRoots()
        => (PortableAllDrives?.IsChecked == true
                ? Venquery.Services.DriveList.Ready().Select(d => d.Root)
                : _portableDriveChecks.Where(c => c.IsChecked == true)
                    .Select(c => ((Venquery.Services.DriveChoice)c.Tag).Root))
            .Concat(ExistingFolders(_settings.PortableScanFolders))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The user's added scan folders that still exist (a folder on an unplugged drive is skipped).</summary>
    private static IEnumerable<string> ExistingFolders(List<string>? folders)
        => (folders ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f) && System.IO.Directory.Exists(f));

    /// <summary>(Re)builds the chips for the user's added scan folders. Each chip starts ticked; unticking it
    /// removes that folder from the scope (and from the saved list).</summary>
    private void RebuildFolderBar(System.Windows.Controls.WrapPanel bar, List<string> folders, Action changed)
    {
        bar.Children.Clear();
        foreach (var f in folders.ToList())
        {
            var exists = System.IO.Directory.Exists(f);
            var name = System.IO.Path.GetFileName(f.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = f;
            var cb = new System.Windows.Controls.CheckBox
            {
                Style = (Style)FindResource("PillCheck"),
                Content = name + (exists ? "" : " (not found)"),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = true,
                ToolTip = f + (exists ? "" : "\nThis folder isn't available right now, so it's skipped.") + "\nUntick to remove it.",
            };
            var path = f;
            cb.Unchecked += (_, _) =>
            {
                folders.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                _settings.Save();
                RebuildFolderBar(bar, folders, changed);
                changed();
            };
            bar.Children.Add(cb);
        }
    }

    /// <summary>Asks for a folder and adds it to a scan scope (no duplicates).</summary>
    private void AddScanFolder(List<string> folders, System.Windows.Controls.WrapPanel bar, Action changed)
    {
        var picked = FolderPicker.Pick(this, "Choose a folder to search", folders.LastOrDefault());
        if (string.IsNullOrWhiteSpace(picked)) return;
        string full;
        try { full = System.IO.Path.GetFullPath(picked!); } catch { return; }
        if (!folders.Any(x => string.Equals(x.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            folders.Add(full);
        _settings.Save();
        RebuildFolderBar(bar, folders, changed);
        changed();
    }

    private void OnPortableAddFolder(object sender, RoutedEventArgs e)
        => AddScanFolder(_settings.PortableScanFolders, PortableFolderBar, UpdatePortableScanEnabled);

    private void OnDocAddFolder(object sender, RoutedEventArgs e)
        => AddScanFolder(_settings.DocScanFolders, DocFolderBar, UpdateDocScanEnabled);

    private void UpdatePortableScanEnabled()
    {
        if (PortableScanButton is null || _portableCts is not null || _portableBackupCts is not null) return;
        var any = PortableScanRoots().Count > 0;
        PortableScanButton.IsEnabled = any;
        PortableScanButton.ToolTip = any ? null : "Pick at least one drive to scan";
        if (_portableExes.Count == 0 && PortableEmpty is not null)
            PortableEmpty.Text = any
                ? "Click \u201cScan\u201d to index standalone executables. Read-only, Venquery lists them and checks their signatures, it never runs them."
                : "Pick the drive(s) to search above, then click \u201cScan\u201d. Nothing is scanned until you choose.";
    }

    private void OnPortableSortChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ApplyPortableSort();
    private void OnPortableSortDir(object sender, RoutedEventArgs e)
    {
        _portableSortDesc = !_portableSortDesc;
        PortableSortDir.Content = _portableSortDesc ? "▼ Desc" : "▲ Asc";
        ApplyPortableSort();
    }
    private void ApplyPortableSort()
    {
        if (_portableView is null) return;
        var field = PortableSortBox.SelectedIndex switch { 1 => "SizeBytes", 2 => "Signed", 3 => "Modified", _ => "Name" };
        var dir = _portableSortDesc ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
        _portableView.SortDescriptions.Clear();
        _portableView.SortDescriptions.Add(new System.ComponentModel.SortDescription(field, dir));
        if (field != "Name") _portableView.SortDescriptions.Add(new System.ComponentModel.SortDescription("Name", System.ComponentModel.ListSortDirection.Ascending));
        _portableView.Refresh();
    }

    private async void OnScanPortable(object sender, RoutedEventArgs e)
    {
        if (_portableCts is not null) return;   // already scanning
        if (PortableScanRoots().Count == 0) { UpdatePortableScanEnabled(); return; }   // nothing picked

        _portableExes.Clear();
        PortableEmpty.Visibility = Visibility.Collapsed;
        PortableResultsPanel.Visibility = Visibility.Collapsed;
        PortableTruncatedNote.Visibility = Visibility.Collapsed;
        PortableBackupSavedBar.Visibility = Visibility.Collapsed;
        PortableSelectAllButton.Content = "Select all";
        PortableFilterBox.Text = "";
        PortableScanButton.IsEnabled = false;
        PortableCancelButton.Visibility = Visibility.Visible;
        PortableDriveBar.IsEnabled = false;
        PortableAllDrives.IsEnabled = false;
        PortableFolderBar.IsEnabled = PortableAddFolder.IsEnabled = false;

        var allDrives = PortableAllDrives.IsChecked == true;
        var roots = PortableScanRoots();
        var excludes = PortableAppScanner.DefaultExcludes();

        var portableTask = BeginTask("Scanning for portable apps…");
        var log = ConsoleAndTask(portableTask);
        log.Report($"> portable scan · roots: {string.Join(", ", roots)} (all-drives: {allDrives})");

        var progress = new Progress<(int folders, int exes, string current)>(p =>
            portableTask.Report(
                $"Scanned {p.folders:n0} folders · found {p.exes:n0} executables…  {Ellipsize(p.current, 54)}"));

        _portableCts = new CancellationTokenSource();
        PortableScanResult result;
        try
        {
            result = await PortableAppScanner.ScanAsync(roots, excludes, progress, _portableCts.Token);
        }
        catch (Exception ex)
        {
            log.Report($"  portable scan error: {ex.Message}");
            PortableEmpty.Visibility = Visibility.Visible;
            PortableEmpty.Text = "The scan couldn't complete: " + ex.Message;
            return;
        }
        finally
        {
            portableTask.Done();
            _portableCts.Dispose();
            _portableCts = null;
            UpdatePortableScanEnabled();
            PortableCancelButton.Visibility = Visibility.Collapsed;
            PortableAllDrives.IsEnabled = true;
            PortableDriveBar.IsEnabled = PortableAllDrives.IsChecked != true;
            PortableFolderBar.IsEnabled = PortableAddFolder.IsEnabled = true;
        }

        foreach (var exe in result.Executables) _portableExes.Add(exe);
        PortableFoldersText.Text = result.FoldersScanned.ToString("n0");
        PortableExeText.Text = result.Executables.Count.ToString("n0");
        PortableSignedText.Text = result.Signed.ToString("n0");
        PortableUnsignedText.Text = result.Unsigned.ToString("n0");
        PortableElapsedText.Text = $"{result.Elapsed.TotalSeconds:0.0}s";
        PortableTruncatedNote.Visibility = result.Truncated ? Visibility.Visible : Visibility.Collapsed;
        PortableResultsPanel.Visibility = Visibility.Visible;

        log.Report($"> portable scan {(result.Cancelled ? "cancelled" : "complete")} · " +
                   $"{result.Executables.Count} exe · {result.Signed} signed / {result.Unsigned} unsigned · " +
                   $"{result.Elapsed.TotalSeconds:0.0}s");
        StatusLine.Text = $"{AppVersion} · {result.Executables.Count} standalone executables found";

        if (result.Executables.Count == 0)
        {
            PortableResultsPanel.Visibility = Visibility.Collapsed;
            PortableEmpty.Visibility = Visibility.Visible;
            PortableEmpty.Text = result.Cancelled
                ? "Scan cancelled before any executables were found."
                : "No standalone executables found in the scanned scope.";
        }
    }

    private void OnCancelPortable(object sender, RoutedEventArgs e) => _portableCts?.Cancel();

    private void OnRevealExe(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && !string.IsNullOrEmpty(path))
            RevealInExplorer(path);
    }

    private void OnExportChecklist(object sender, RoutedEventArgs e)
    {
        var chosen = _portableExes.Where(x => x.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, "No executables are ticked.", Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Venquery-portable-apps-{DateTime.Now:yyyy-MM-dd}.txt",
            Filter = "Text checklist (*.txt)|*.txt",
            InitialDirectory = DownloadService.DownloadsFolder(),
        };
        if (dlg.ShowDialog(this) != true) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Venquery {AppVersion}, portable executables checklist");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine(new string('=', 60));
        foreach (var x in chosen)
        {
            sb.AppendLine();
            sb.AppendLine($"[ ] {x.Name}   ({x.SizeText})");
            sb.AppendLine($"      {x.FullPath}");
            sb.AppendLine($"      signature: {x.SignatureText}");
        }

        try
        {
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
            OpenExternal(dlg.FileName);
            StatusLine.Text = $"Exported checklist · {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not write the checklist.\n\n" + ex.Message, Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Filter the results list live. Select-all and "Back up selected" both respect the filter,
    // so you can narrow to e.g. "chrome" and tick just those.
    private void OnPortableFilterChanged(object sender, TextChangedEventArgs e)
    {
        _portableQuery = PortableFilterBox.Text.Trim();
        PortableFilterPlaceholder.Visibility =
            string.IsNullOrEmpty(PortableFilterBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _portableView?.Refresh();
    }

    private void OnPortableSelectAll(object sender, RoutedEventArgs e)
    {
        // Operate on the currently visible (filtered) rows only.
        var visible = _portableView.Cast<PortableExe>().ToList();
        var allOn = visible.Count > 0 && visible.All(x => x.IsSelected);
        foreach (var x in visible) x.IsSelected = !allOn;
        PortableSelectAllButton.Content = allOn ? "Select all" : "Clear all";
    }

    /// <summary>Copies each ticked app's whole folder into a "Portable Apps" folder (Downloads by default, or
    /// the folder chosen under "Save backups to"),
    /// which the user can then move to a USB stick. Pure copy, nothing is run or installed, and no
    /// existing folder is overwritten (see <see cref="PortableBackupService"/>).</summary>
    private async void OnBackupPortableApps(object sender, RoutedEventArgs e)
    {
        if (_portableBackupCts is not null) return;   // already copying

        // One main exe per folder, so its folder is the app folder to copy. Distinct for safety.
        var folders = _portableExes.Where(x => x.IsSelected && !string.IsNullOrEmpty(x.Folder))
            .Select(x => x.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count == 0)
        {
            MessageBox.Show(this, "No apps are ticked, so there's nothing to back up.",
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dest = System.IO.Path.Combine(BackupParent(_settings.PortableBackupParent), "Portable Apps");
        var confirm = MessageBox.Show(this,
            $"Copy {folders.Count} app folder(s) into:\n\n{dest}\n\n" +
            "The full folders are copied (this can be large and take a while). Nothing is installed " +
            "or run, and existing folders are never overwritten. Continue?",
            "Back up portable apps", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (confirm != MessageBoxResult.Yes) return;

        PortableBackupSavedBar.Visibility = Visibility.Collapsed;
        BackupPortableButton.IsEnabled = false;
        PortableScanButton.IsEnabled = false;
        var t = BeginTask("Backing up portable apps…", determinate: true);

        var log = MakeConsoleLog();
        log.Report($"> portable backup · {folders.Count} app folder(s) -> {dest}");
        var progress = new Progress<(int done, int total, string current)>(p =>
            t.Report(p.total > 0 ? (double)p.done / p.total * 100.0 : 0,
                     $"Copying {p.done} of {p.total} · {Ellipsize(p.current, 48)}"));

        _portableBackupCts = new CancellationTokenSource();
        var portableReceipt = _settings.BackupReceipts ? new BackupReceipt() : null;
        PortableBackupResult res;
        try
        {
            res = await PortableBackupService.BackupAsync(folders, dest, progress, _portableBackupCts.Token, portableReceipt);
        }
        catch (Exception ex)
        {
            log.Report($"  portable backup error: {ex.Message}");
            MessageBox.Show(this, "The backup couldn't complete.\n\n" + ex.Message, Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            t.Done();
            _portableBackupCts.Dispose();
            _portableBackupCts = null;
            BackupPortableButton.IsEnabled = true;
            UpdatePortableScanEnabled();
        }

        if (!res.Ok)
        {
            MessageBox.Show(this, "Could not create the Portable Apps folder.\n\n" + res.Error,
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _savedPortableBackupFolder = res.DestFolder;
        var head = res.Cancelled
            ? $"Cancelled, copied {res.Copied} app(s) before stopping"
            : $"Copied {res.Copied} app(s) to {res.DestFolder}";
        var tail = res.Skipped > 0 ? $" · {res.Skipped} skipped" : "";
        tail += ReceiptSummary(portableReceipt, res.DestFolder, "Portable Apps backup", log);
        PortableBackupSavedText.Text = head + tail + ".";
        PortableBackupSavedBar.Visibility = Visibility.Visible;
        log.Report($"> portable backup {(res.Cancelled ? "cancelled" : "done")} · {res.Copied} copied / {res.Skipped} skipped");
        foreach (var n in res.Notes) log.Report("  " + n);
        StatusLine.Text = $"{AppVersion} · portable backup · {res.Copied} app(s) copied";
    }

    private void OnCancelPortableBackup(object sender, RoutedEventArgs e) => _portableBackupCts?.Cancel();

    private void OnOpenPortableBackupFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_savedPortableBackupFolder)) return;
        try { Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("explorer.exe"), $"\"{_savedPortableBackupFolder}\"") { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    // ------------------------------------------------------------------
    // Loose Documents, scan Desktop / C:\ root, copy selected to Portable Documents
    // ------------------------------------------------------------------

    private async void OnScanDocuments(object sender, RoutedEventArgs e)
    {
        if (_docScanCts is not null) return;   // already scanning
        if (DocScanRoots().Count == 0) { UpdateDocScanEnabled(); return; }   // nothing picked

        _docEntries.Clear();
        DocEmpty.Visibility = Visibility.Collapsed;
        DocResultsPanel.Visibility = Visibility.Collapsed;
        DocTruncatedNote.Visibility = Visibility.Collapsed;
        DocBackupSavedBar.Visibility = Visibility.Collapsed;
        DocSelectAllButton.Content = "Select all";
        DocFilterBox.Text = "";
        DocScanButton.IsEnabled = false;
        DocAllDrives.IsEnabled = false;
        DocDriveBar.IsEnabled = false;
        DocFolderBar.IsEnabled = DocAddFolder.IsEnabled = false;
        DocScanCancelButton.Visibility = Visibility.Visible;

        // Only the drives the user picked (the Windows drive = personal folders + its root), never C: by default.
        var roots = DocScanRoots();
        var docTask = BeginTask("Scanning for loose documents…");
        var log = ConsoleAndTask(docTask);
        log.Report($"> loose-documents scan · roots: {string.Join(", ", roots)}");
        var progress = new Progress<(int items, string current)>(p =>
            docTask.Report($"Scanning… found {p.items:n0} so far  ·  {Ellipsize(p.current, 48)}"));

        _docScanCts = new CancellationTokenSource();
        DocScanResult result;
        try
        {
            result = await DocumentScanner.ScanAsync(roots, progress, _docScanCts.Token);
        }
        catch (Exception ex)
        {
            log.Report($"  loose-documents scan error: {ex.Message}");
            DocEmpty.Visibility = Visibility.Visible;
            DocEmpty.Text = "The scan couldn't complete: " + ex.Message;
            return;
        }
        finally
        {
            docTask.Done();
            _docScanCts.Dispose();
            _docScanCts = null;
            UpdateDocScanEnabled();
            DocAllDrives.IsEnabled = true;
            DocDriveBar.IsEnabled = DocAllDrives.IsChecked != true;
            DocFolderBar.IsEnabled = DocAddFolder.IsEnabled = true;
            DocScanCancelButton.Visibility = Visibility.Collapsed;
        }

        foreach (var it in result.Items)
            _docEntries.Add(new DocEntry
            {
                Name = it.Name, FullPath = it.FullPath, IsFolder = it.IsFolder,
                SizeBytes = it.SizeBytes, FileCount = it.FileCount, Categories = it.Categories, Modified = it.Modified,
            });
        DocItemsText.Text = result.Items.Count.ToString("n0");
        DocSizeText.Text = FormatBytes(result.TotalBytes);
        DocElapsedText.Text = $"{result.Elapsed.TotalSeconds:0.0}s";
        DocTruncatedNote.Visibility = result.Truncated ? Visibility.Visible : Visibility.Collapsed;
        DocResultsPanel.Visibility = Visibility.Visible;

        log.Report($"> loose-documents scan {(result.Cancelled ? "cancelled" : "complete")} · " +
                   $"{result.Items.Count} item(s) · {FormatBytes(result.TotalBytes)} · {result.Elapsed.TotalSeconds:0.0}s");
        StatusLine.Text = $"{AppVersion} · {result.Items.Count} loose document item(s) found";

        if (result.Items.Count == 0)
        {
            DocResultsPanel.Visibility = Visibility.Collapsed;
            DocEmpty.Visibility = Visibility.Visible;
            DocEmpty.Text = result.Cancelled
                ? "Scan cancelled before anything was found."
                : "No loose documents found on the drives you picked. (Nothing at risk here, good!)";
        }
    }

    private void OnCancelDocScan(object sender, RoutedEventArgs e) => _docScanCts?.Cancel();

    // ------------------------------------------------------------------
    // System Health (read-only)
    // ------------------------------------------------------------------

    // Loaded lazily the first time the page is opened, then cached until Refresh.
    private async void EnsureHealthLoaded() { if (_health is null && !_healthBusy) await RunHealthScan(); }

    private async void OnRefreshHealth(object sender, RoutedEventArgs e) => await RunHealthScan();

    private async Task RunHealthScan()
    {
        if (_healthBusy) return;
        _healthBusy = true;
        HealthRefreshButton.IsEnabled = false;
        HealthContent.Children.Clear();
        HealthSummaryCard.Visibility = Visibility.Collapsed;
        HealthNote.Visibility = Visibility.Collapsed;
        var t = BeginTask("Reading system health…");
        try
        {
            _health = await HealthScanner.ScanAsync(ConsoleAndTask(t));
            RenderHealth(_health);
            StatusLine.Text = $"{AppVersion} · health · {_health.SummaryLine()}";
        }
        catch (Exception ex)
        {
            HealthContent.Children.Add(HealthText("Couldn't read system health: " + ex.Message, 13, "Dim"));
        }
        finally
        {
            t.Done();
            HealthRefreshButton.IsEnabled = true;
            _healthBusy = false;
        }
    }

    private void RenderHealth(HealthReport h)
    {
        HealthContent.Children.Clear();
        HealthSummary.Text = h.SummaryLine();
        // 1.4.0: the summary tiles replace the one-line summary card (same data, at a glance).
        UpdateHealthTiles();

        // --- Storage ---
        var disks = new StackPanel();
        if (h.Disks.Count == 0) disks.Children.Add(HealthText("No physical disks reported.", 12.5, "Dim"));
        for (var i = 0; i < h.Disks.Count; i++)
        {
            var d = h.Disks[i];
            if (i > 0) disks.Children.Add(HealthSeparator());

            disks.Children.Add(HealthText(d.Model, 15, "Text", FontWeights.SemiBold));
            var spec = string.Join("  ·  ", new[] { d.Kind, HealthReport.FormatBytes(d.SizeBytes), d.Bus }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            var healthy = d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            line.Children.Add(HealthText(spec + "   ", 12.5, "Muted"));
            var badge = HealthText("● " + d.Health, 12.5, healthy ? "Green" : "Amber", FontWeights.SemiBold);
            badge.ToolTip = "Windows' own drive status. It flags a drive that is failing or predicted to fail, " +
                            "it is not a wear grade. Detailed wear/temperature come from the drive's SMART data.";
            line.Children.Add(badge);
            disks.Children.Add(line);

            var extra = new List<string>();
            if (d.WearPercent is { } w) extra.Add($"{w}% life used");
            if (d.TemperatureC is { } t) extra.Add($"{t} °C");
            if (d.PowerOnHours is { } ph) extra.Add($"{ph:n0} power-on hours");
            disks.Children.Add(extra.Count > 0
                ? HealthText(string.Join("  ·  ", extra), 12, "Muted", margin: new Thickness(0, 3, 0, 0))
                : HealthText("wear / temperature not reported", 12, "Dim", margin: new Thickness(0, 3, 0, 0)));
            if (!string.IsNullOrWhiteSpace(d.Serial))
                disks.Children.Add(HealthText($"S/N  {d.Serial}", 11.5, "Dim", margin: new Thickness(0, 2, 0, 0)));
        }
        HealthContent.Children.Add(HealthCard("Storage", disks));

        // --- Volumes (free space) ---
        if (h.Volumes.Count > 0)
        {
            var vols = new StackPanel();
            foreach (var v in h.Volumes)
            {
                if (vols.Children.Count > 0) vols.Children.Add(new Border { Height = 10 });
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(HealthText(v.Drive + (string.IsNullOrEmpty(v.Label) ? "" : $"  {v.Label}"), 15, "Text", FontWeights.SemiBold));
                vols.Children.Add(head);
                vols.Children.Add(UsageBar(v.UsedFraction, v.Low));
                vols.Children.Add(HealthText(
                    $"{HealthReport.FormatBytes(v.FreeBytes)} free of {HealthReport.FormatBytes(v.TotalBytes)}  ·  {v.UsedFraction * 100:0}% used",
                    12, v.Low ? "Amber" : "Muted"));
            }
            HealthContent.Children.Add(HealthCard("Free space", vols));
        }

        // --- Memory ---
        if (h.Memory is { } m)
        {
            var mem = new StackPanel();
            mem.Children.Add(HealthText(
                $"{HealthReport.FormatBytes(m.TotalBytes)} total  ·  {m.SlotsUsed} of {m.SlotsTotal} slot(s) used", 15, "Text", FontWeights.SemiBold));
            foreach (var mod in m.Modules)
            {
                var partId = string.Join(" ", new[] { mod.Manufacturer, mod.PartNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
                mem.Children.Add(HealthText(
                    $"{mod.Slot}: {HealthReport.FormatBytes(mod.SizeBytes)}{(mod.SpeedMhz > 0 ? $" @ {mod.SpeedMhz} MHz" : "")}" +
                    (partId.Length > 0 ? $"  ·  {partId}" : ""),
                    12.5, "Muted", margin: new Thickness(0, 4, 0, 0)));
            }
            HealthContent.Children.Add(HealthCard("Memory", mem));
        }

        // --- Battery (laptops only) ---
        if (h.Battery is { } b)
        {
            var bat = new StackPanel();
            var idbits = string.Join("  ·  ", new[] { b.Model, b.Chemistry }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (idbits.Length > 0) bat.Children.Add(HealthText(idbits, 12.5, "Muted", margin: new Thickness(0, 0, 0, 4)));
            var worn = b.WearPercent is { } bw ? $"{bw}% worn" : "wear not reported by this battery";
            bat.Children.Add(HealthText(worn, 15, b.WearPercent >= 25 ? "Amber" : "Text", FontWeights.SemiBold));
            // Mockup C: a bar of the capacity that is left (100% minus wear), amber once it is 25%+ worn.
            if (b.WearPercent is { } bwear)
            {
                var left = Math.Max(0, 100 - bwear);
                bat.Children.Add(UsageBar(left / 100.0, bwear >= 25));
                bat.Children.Add(HealthText($"{left}% of design capacity left" +
                    (b.DesignedCapacity is { } dc && b.FullChargedCapacity is { } fc ? $"  ·  design {dc:n0} mWh → now {fc:n0} mWh" : ""),
                    12, "Muted"));
            }
            var extra = new List<string>();
            if (b.ChargePercent is { } cp) extra.Add($"currently {cp}% charged");
            if (b.VoltageMv is { } v) extra.Add($"{v / 1000.0:0.00} V");
            if (b.ChargeRateMw is { } cr && cr > 0) extra.Add($"charging {cr / 1000.0:0.0} W");
            if (b.DischargeRateMw is { } dr && dr > 0) extra.Add($"discharging {dr / 1000.0:0.0} W");
            if (extra.Count > 0) bat.Children.Add(HealthText(string.Join("  ·  ", extra), 12.5, "Muted", margin: new Thickness(0, 4, 0, 0)));
            HealthContent.Children.Add(HealthCard("Battery", bat));
        }

        // --- Stability (from the 30-day stability read; mockup C shows it as a card here) ---
        if (_stability is { } st)
        {
            var stab = new StackPanel();
            var n = st.SeriousCount;
            stab.Children.Add(HealthText(n == 0 ? "No problems" : (n == 1 ? "1 problem" : $"{n} problems"), 15, n == 0 ? "Green" : "Amber", FontWeights.SemiBold));
            stab.Children.Add(HealthText(st.SummaryLine(), 12.5, "Muted", margin: new Thickness(0, 4, 0, 0)));
            HealthContent.Children.Add(HealthCard("Stability", stab));
        }

        // --- Uptime / install ---
        var sys = new StackPanel();
        if (h.LastBoot is { } lb)
        {
            sys.Children.Add(HealthText(FormatUptimeLong(DateTime.Now - lb), 15, "Text", FontWeights.SemiBold));
            sys.Children.Add(HealthText($"Last restart {lb:yyyy-MM-dd HH:mm}", 12.5, "Muted", margin: new Thickness(0, 4, 0, 0)));
        }
        if (h.InstallDate is { } id) sys.Children.Add(HealthText($"Windows installed {id:yyyy-MM-dd}", 12.5, "Muted", margin: new Thickness(0, 2, 0, 0)));
        if (sys.Children.Count > 0) HealthContent.Children.Add(HealthCard("Uptime", sys));

        HealthNote.Visibility = Visibility.Visible;
    }

    // --- little builders for the health cards (theme-brush aware) ---

    private static string FormatUptimeLong(TimeSpan t)
    {
        string P(int n, string w) => n == 1 ? $"1 {w}" : $"{n} {w}s";
        if (t.TotalDays >= 1) return $"{P((int)t.TotalDays, "day")}, {P(t.Hours, "hour")}";
        if (t.TotalHours >= 1) return $"{P((int)t.TotalHours, "hour")}, {P(t.Minutes, "minute")}";
        return P(Math.Max(1, (int)t.TotalMinutes), "minute");
    }

    private static string FormatUptimeShort(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalMinutes}m";

    private static TextBlock HealthText(string text, double size, string fgKey, FontWeight? weight = null, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (weight is { } w) t.FontWeight = w;
        if (margin is { } mg) t.Margin = mg;
        t.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
        return t;
    }

    private static Border HealthSeparator()
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
        b.SetResourceReference(Border.BackgroundProperty, "Line");
        return b;
    }

    private static Border HealthCard(string title, UIElement body)
    {
        var stack = new StackPanel();
        stack.Children.Add(HealthText(title.ToUpperInvariant(), 11.5, "Muted", FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        stack.Children.Add(body);
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 14, 14),
            Child = stack,
        };
        border.SetResourceReference(Border.BackgroundProperty, "Card");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        return border;
    }

    private static FrameworkElement HealthRow(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = HealthText(label, 12.5, "Muted");
        var v = HealthText(value, 12.5, "Text");
        Grid.SetColumn(v, 1);
        g.Children.Add(l);
        g.Children.Add(v);
        return g;
    }

    private static FrameworkElement UsageBar(double fraction, bool low)
    {
        fraction = Math.Max(0.0, Math.Min(1.0, fraction));
        var g = new Grid { Height = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, fraction), GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - fraction), GridUnitType.Star) });
        var fill = new Border { CornerRadius = new CornerRadius(4) };
        fill.SetResourceReference(Border.BackgroundProperty, low ? "Amber" : "Accent");
        g.Children.Add(fill);
        var track = new Border { CornerRadius = new CornerRadius(4), Height = 8, Margin = new Thickness(0, 6, 0, 3), Child = g };
        track.SetResourceReference(Border.BackgroundProperty, "Ctl");
        return track;
    }

    // --- Guarded system-maintenance scripts on the Health page (SFC/DISM + restore point) ---
    // Shared across every edition. Venquery only WRITES the script; each Run button lives inside a bar that
    // stays hidden until the script has been generated, so a run is never possible before the script exists.
    // Even then it is gated by a confirm dialog, the Windows UAC prompt, and the script's own typed Y.

    // Shared helpers so both cards (and any future one) behave identically.
    private void ShowGeneratedScript(string path, Border bar, TextBlock savedText, TextBox cmdBox)
    {
        cmdBox.Text = RunCommandFor(path);
        savedText.Text =
            $"Saved {System.IO.Path.GetFileName(path)} to your Downloads folder. " +
            "Open it to review first, or press Run. It still asks you to type Y as Administrator before it changes anything.";
        bar.Visibility = Visibility.Visible;
        StatusLine.Text = $"{AppVersion} · script saved · {System.IO.Path.GetFileName(path)}";
    }

    private void OpenInNotepad(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("notepad.exe"), $"\"{path}\"") { UseShellExecute = true }); }
        catch { OpenExternal(path); }
    }

    /// <summary>Launch a generated script elevated, the one action that runs rather than only writes, gated
    /// by this confirmation AND the script's own admin check + typed-Y.</summary>
    private void LaunchScriptElevated(string path, string dialogTitle, string what)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            MessageBox.Show(this, "Generate the script first.", Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ok = MessageBox.Show(this,
            "This opens the script in an Administrator PowerShell window and starts it.\n\n" +
            $"Venquery normally only writes files, this is the one action that launches a script. It uses {what} " +
            "and still waits for you to type Y before doing anything.\n\nOpen it and run it now?",
            dialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("powershell.exe"),
                $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"")
            { UseShellExecute = true, Verb = "runas" });
            StatusLine.Text = "Launched the script (elevated), confirm inside the PowerShell window";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            StatusLine.Text = "Run cancelled, administrator permission was not granted";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not launch the script.\n\n" + ex.Message, Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // SFC + DISM card
    private void OnGenerateSfcDism(object sender, RoutedEventArgs e)
    {
        var res = SystemRepairService.WriteSfcDismScript(DownloadService.DownloadsFolder());
        if (!res.Ok) { MessageBox.Show(this, "Could not write the repair script.\n\n" + res.Error, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _savedSfcScript = res.ScriptPath;
        ShowGeneratedScript(res.ScriptPath, SfcSavedBar, SfcSavedText, SfcRunCommandBox);
    }
    private void OnOpenSfcFolder(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_savedSfcScript)) RevealInExplorer(_savedSfcScript); }
    private void OnOpenSfcScript(object sender, RoutedEventArgs e) => OpenInNotepad(_savedSfcScript);
    private void OnCopySfcRunCommand(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_savedSfcScript)) { TrySetClipboard(RunCommandFor(_savedSfcScript)); StatusLine.Text = "Copied the run-command to the clipboard"; } }
    private void OnRunSfcScript(object sender, RoutedEventArgs e) => LaunchScriptElevated(_savedSfcScript, "Run repair script", "Windows' own DISM and sfc");

    // Create-restore-point card
    private void OnGenerateRestorePoint(object sender, RoutedEventArgs e)
    {
        var res = SystemRepairService.WriteRestorePointScript(DownloadService.DownloadsFolder());
        if (!res.Ok) { MessageBox.Show(this, "Could not write the script.\n\n" + res.Error, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _savedRestoreScript = res.ScriptPath;
        ShowGeneratedScript(res.ScriptPath, RpSavedBar, RpSavedText, RpRunCommandBox);
    }
    private void OnOpenRpFolder(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_savedRestoreScript)) RevealInExplorer(_savedRestoreScript); }
    private void OnOpenRpScript(object sender, RoutedEventArgs e) => OpenInNotepad(_savedRestoreScript);
    private void OnCopyRpRunCommand(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_savedRestoreScript)) { TrySetClipboard(RunCommandFor(_savedRestoreScript)); StatusLine.Text = "Copied the run-command to the clipboard"; } }
    private void OnRunRpScript(object sender, RoutedEventArgs e) => LaunchScriptElevated(_savedRestoreScript, "Run restore-point script", "Windows' own System Restore");

    // ------------------------------------------------------------------
    // Prepare for Reset, a modal wizard (launched from the rail action button)
    // ------------------------------------------------------------------
#if !LEGACY
    private Venquery.Controls.RestoreView? _restoreView;
#endif

    // Tabs react to their checked state (works for keyboard/screen readers/UI Automation); one is always selected.
    private ICollectionView? _backupView;
    private string _backupQuery = "";

    private void OnBackupFilterChanged(object sender, TextChangedEventArgs e)
    {
        _backupQuery = BackupFilterBox.Text.Trim();
        _backupView?.Refresh();
    }

    /// <summary>The big button says how many apps will go into backup.json (mockup C: "Back up 5 selected apps").</summary>
    private void UpdateBackupSaveLabel()
    {
        var n = _backupApps.Count(a => a.IsSelected);
        SaveBackupLabel.Text = n == 1 ? "Back up 1 selected app" : $"Back up {n:n0} selected apps";
#if !LEGACY
        SaveBackupButton.IsEnabled = n > 0 && _lastExport is not null;
#else
        SaveBackupButton.IsEnabled = false;   // Legacy has no winget backup (the tab is hidden there)
#endif
    }

    /// <summary>Installed apps loads its list when the tab first opens (read-only: it only asks winget what is
    /// installed) so the page shows the list like mockup C. With no winget it stays on the "Read apps" button,
    /// which offers the install, so there is never a surprise dialog.</summary>
    private bool _appsAutoRead;
    private async void EnsureAppsLoaded()
    {
#if !LEGACY
        if (_appsAutoRead || _lastExport is not null || _wingetBusy || !Edition.Current.HasWinget) return;
        _appsAutoRead = true;
        if (await WingetService.IsInstalledAsync()) OnCreateBackup(CreateBackupButton, new RoutedEventArgs());
        else BackupEmpty.Text = "winget (Windows' own app installer) isn't on this PC yet. Press “Read apps” and Venquery offers a guarded script to install it.";
#else
        await System.Threading.Tasks.Task.CompletedTask;
#endif
    }
    private bool _switchingBackupTab;
    private void OnBackupTabApps(object sender, RoutedEventArgs e) { if (!_switchingBackupTab) SetBackupTab(restore: false); }
    private void OnBackupTabRestore(object sender, RoutedEventArgs e) { if (!_switchingBackupTab) SetBackupTab(restore: true); }
    private void OnBackupTabUnchecked(object sender, RoutedEventArgs e)
    {
        if (_switchingBackupTab || sender is not System.Windows.Controls.CheckBox cb) return;
        _switchingBackupTab = true; cb.IsChecked = true; _switchingBackupTab = false;
    }

    private void SetBackupTab(bool restore)
    {
        if (BackupAppsMode is null || RestoreHost is null || BackupTabRestore is null) return;   // during InitializeComponent
        _switchingBackupTab = true;
        BackupTabApps.IsChecked = !restore;
        BackupTabRestore.IsChecked = restore;
        _switchingBackupTab = false;
        BackupAppsMode.Visibility = restore ? Visibility.Collapsed : Visibility.Visible;
        RestoreHost.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
#if !LEGACY
        if (restore && _restoreView is not null) _ = _restoreView.RefreshFoundAsync();
#endif
    }

    /// <summary>Opens the read-only Windows Update driver check. Each offered driver is matched back to a scanned
    /// device by hardware ID (either ID may be the more specific one).</summary>
    private void OnCheckWindowsUpdateDrivers(object sender, RoutedEventArgs e)
    {
        // Match on vendor + device (PCI\VEN_x&DEV_y / USB\VID_x&PID_y / ACPI\XXXX): a vendor-only prefix would
        // label every Intel update "for" whichever Intel device came first.
        var known = _rows.SelectMany(r => r.AllHardwareIds.Select(id => (key: DeviceKey(id), r.Name)))
            .Concat(_unmatched.Select(d => (key: DeviceKey(d.RawHardwareId), d.Name)))
            .Where(x => x.key is not null).ToList();
        string? Lookup(string wuId)
        {
            var k = DeviceKey(wuId);
            return k is null ? null : known.FirstOrDefault(x => string.Equals(x.key, k, StringComparison.OrdinalIgnoreCase)).Name;
        }
        new Venquery.Controls.WuDriversWindow(Lookup) { Owner = this }.ShowDialog();
    }

    /// <summary>"PCI\VEN_8086&amp;DEV_A112&amp;SUBSYS..." → "PCI\VEN_8086&amp;DEV_A112"; "USB\VID_046D&amp;PID_C52B..." →
    /// "USB\VID_046D&amp;PID_C52B"; null for an ID without a specific device part (too vague to match on).</summary>
    internal static string? DeviceKey(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return null;
        var parts = hardwareId!.Trim().Split('&');
        if (parts.Length >= 2 && (parts[0].IndexOf("VEN_", StringComparison.OrdinalIgnoreCase) >= 0 || parts[0].IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0)
            && (parts[1].StartsWith("DEV_", StringComparison.OrdinalIgnoreCase) || parts[1].StartsWith("PID_", StringComparison.OrdinalIgnoreCase)))
            return parts[0] + "&" + parts[1];
        if (hardwareId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) && hardwareId.Length > 5)
            return hardwareId.Split('\\').Length >= 2 ? "ACPI\\" + hardwareId.Split('\\')[1] : null;
        return null;
    }

    private void OnOpenResetWizard(object sender, RoutedEventArgs e)
    {
#if !LEGACY
        // The wizard uses winget/driver export, which the Win7 Legacy build doesn't ship, the launcher is
        // hidden there (see ctor), so this body is compiled out on Legacy.
        var dlg = new ResetWizard { Owner = this, WriteReceipts = _settings.BackupReceipts };
        dlg.ShowDialog();
#endif
    }

    // ------------------------------------------------------------------
    // Showcase, a fullscreen spec screen (launched from the rail action button). Every edition.
    // ------------------------------------------------------------------
    private async void OnOpenShowcase(object sender, RoutedEventArgs e)
    {
        try
        {
            var sys = _sysInfo ?? await HardwareScanner.ScanSystemInfoAsync(null);
            _sysInfo ??= sys;
            StatusLine.Text = "Preparing showcase, reading specs...";
            // Storage + memory-module details live in the health scan; load it once so every headline spec is
            // available (and the details toggle has data). MachineFingerprint gives the board + GPU list.
            _health ??= await HealthScanner.ScanAsync(null);
            var fp = await Task.Run(MachineFingerprint.Compute);
            var win = new Controls.ShowcaseWindow(sys, fp, _health, _settings) { Owner = this };
            win.Show();
            StatusLine.Text = AppVersion;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't open the showcase.\n\n" + ex.Message,
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------
    // Startup & Tasks
    // ------------------------------------------------------------------
    private async void EnsureStartupLoaded() { if (!_startupLoaded && !_startupBusy) await RunStartupScan(); }
    private async void OnRefreshStartup(object sender, RoutedEventArgs e) => await RunStartupScan();
    private void OnToggleShowMsTasks(object sender, RoutedEventArgs e) { _showMsTasks = ShowMsTasksCheck.IsChecked == true; RenderTasks(); }

    private async Task RunStartupScan()
    {
        if (_startupBusy) return;
        _startupBusy = true;
        StartupRefreshButton.IsEnabled = false;
        StartupList.Children.Clear();
        TaskList.Children.Clear();
        var t = BeginTask("Reading startup items & tasks…");
        try
        {
            var result = await Task.Run(() =>
                (Items: StartupTaskScanner.ReadStartupItems(), Tasks: StartupTaskScanner.ReadScheduledTasks()));
            _startupItems = result.Items;
            _tasks = result.Tasks;
            _startupLoaded = true;
            RenderStartup();
            RenderTasks();
            StatusLine.Text = $"{AppVersion} · {result.Items.Count} startup item(s) · {result.Tasks.Count} task(s)";
        }
        catch (Exception ex)
        {
            StartupList.Children.Add(HealthText("Couldn't read startup/tasks: " + ex.Message, 12.5, "Dim"));
        }
        finally
        {
            t.Done();
            StartupRefreshButton.IsEnabled = true;
            _startupBusy = false;
        }
    }

    private void RenderStartup()
    {
        UpdateStartupTiles();
        StartupList.Children.Clear();
        if (_startupItems is null || _startupItems.Count == 0)
        {
            StartupList.Children.Add(HealthText("No startup programs reported.", 12.5, "Dim"));
            return;
        }
        // Say up front which entries will ask for administrator approval, before anyone clicks.
        var adminCount = _canToggleTasks ? _startupItems.Count(i => i.Target?.NeedsAdmin == true) : 0;
        if (adminCount > 0)
            StartupList.Children.Add(HealthText(
                $"{adminCount} of these {(adminCount == 1 ? "is" : "are")} set up for everyone on this PC, so turning {(adminCount == 1 ? "it" : "them")} on or off " +
                "asks for administrator approval (a UAC prompt). Only that one quick change runs as administrator and it ends straight away; " +
                "Venquery itself never stays elevated. Your own startup programs switch without admin.",
                12, "Amber", margin: new Thickness(0, 0, 0, 8)));

        foreach (var it in _startupItems)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var stack = new StackPanel();
            stack.Children.Add(HealthText(it.Name, 13, "Text", FontWeights.SemiBold));
            if (!string.IsNullOrWhiteSpace(it.Command))
                stack.Children.Add(HealthText(it.Command, 11.5, "Muted", margin: new Thickness(0, 2, 0, 0)));
            if (!string.IsNullOrWhiteSpace(it.Location))
                stack.Children.Add(HealthText(it.Location, 11, "Dim", margin: new Thickness(0, 2, 0, 0)));
            if (_canToggleTasks && it.Target is not null)
            {
                var meta = new List<string> { it.Enabled ? "\u25CF Starts at sign-in" : "\u25CB Turned off" };
                meta.Add(it.Target.NeedsAdmin ? "Everyone on this PC \u00b7 needs admin" : "Just you \u00b7 no admin needed");
                stack.Children.Add(HealthText(string.Join("   \u00b7   ", meta), 11.5, it.Enabled ? "Green" : "Muted", margin: new Thickness(0, 3, 0, 0)));
            }
            else if (_canToggleTasks)
                stack.Children.Add(HealthText("Review only (this kind of entry can't be switched here)", 11, "Dim", margin: new Thickness(0, 3, 0, 0)));
            grid.Children.Add(stack);

            if (_canToggleTasks && it.Target is not null)
            {
                var btn = new Button
                {
                    Style = (Style)FindResource("ChromeButton"),
                    Content = (it.Enabled ? "Turn off" : "Turn on") + (it.Target.NeedsAdmin ? " (admin)" : ""),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = it,
                    ToolTip = it.Target.NeedsAdmin
                        ? "Set up for everyone on this PC: Windows will ask for administrator approval (UAC) for this one change."
                        : "Your own startup program: switches straight away, no administrator approval needed.",
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(btn, "StartupToggle_" + it.Name);
                btn.Click += OnToggleStartupItem;
                Grid.SetColumn(btn, 1);
                grid.Children.Add(btn);
            }
            StartupList.Children.Add(WrapCard(grid));
        }
    }

    private async void OnToggleStartupItem(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not StartupItem it || it.Target is null) return;
        var enable = !it.Enabled;
        var admin = it.Target.NeedsAdmin;
        var ok = MessageBox.Show(this,
            $"Turn {(enable ? "on" : "off")} \"{it.Name}\" at sign-in?\n\n" +
            (admin
                ? "This program is set up for everyone on this PC, so Windows will ask for administrator approval (UAC). " +
                  "Only this one change runs as administrator and it ends straight away.\n\n"
                : "This is your own startup program, so no administrator approval is needed.\n\n") +
            "It only flips the on/off switch (the same one Task Manager uses). The program itself isn't touched or removed, " +
            "and you can switch it back here.",
            "Change startup program", MessageBoxButton.YesNo, admin ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;
        b.IsEnabled = false;
        var target = it.Target;
        var (success, msg) = await Task.Run(() => StartupTaskScanner.SetStartupEnabled(target, enable));
        StatusLine.Text = $"{it.Name}: {msg}";
        if (success) await RunStartupScan();
        else { b.IsEnabled = true; MessageBox.Show(this, msg, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void RenderTasks()
    {
        UpdateStartupTiles();
        TaskList.Children.Clear();
        if (_tasks is null) return;
        var shown = _tasks.Where(t => _showMsTasks || !t.IsMicrosoft).ToList();
        if (shown.Count == 0)
        {
            TaskList.Children.Add(HealthText(
                _tasks.Count > 0 ? "No third-party scheduled tasks. Tick “Show Windows tasks” to see them all." : "No scheduled tasks reported.",
                12.5, "Dim"));
            return;
        }
        foreach (var t in shown)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(HealthText(t.Name, 13, "Text", FontWeights.SemiBold));
            info.Children.Add(HealthText(t.Path, 11, "Dim", margin: new Thickness(0, 2, 0, 0)));
            var meta = new List<string> { t.Enabled ? "● Enabled" : "○ Disabled" };
            if (!string.IsNullOrWhiteSpace(t.NextRun) && !t.NextRun.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                meta.Add("next: " + t.NextRun);
            info.Children.Add(HealthText(string.Join("   ·   ", meta), 11.5, t.Enabled ? "Green" : "Muted", margin: new Thickness(0, 3, 0, 0)));
            grid.Children.Add(info);

            if (_canToggleTasks)
            {
                var btn = new Button
                {
                    Style = (Style)FindResource("ChromeButton"),
                    Content = t.Enabled ? "Disable" : "Enable",
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = t,
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(btn, "TaskToggle_" + t.Name);
                btn.Click += OnToggleTask;
                Grid.SetColumn(btn, 1);
                grid.Children.Add(btn);
            }
            TaskList.Children.Add(WrapCard(grid));
        }
        if (!_canToggleTasks)
            TaskList.Children.Add(HealthText("On this edition (Windows 7/8.1) tasks are shown for review only.", 11.5, "Dim", margin: new Thickness(2, 6, 0, 0)));
    }

    // A small bordered container matching the Health cards, for the startup/task rows.
    private static Border WrapCard(UIElement child)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Child = child,
        };
        b.SetResourceReference(Border.BackgroundProperty, "Card");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }

    private async void OnToggleTask(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ScheduledTaskItem t) return;
        var enable = !t.Enabled;
        var ok = MessageBox.Show(this,
            $"{(enable ? "Enable" : "Disable")} this scheduled task?\n\n{t.Path}\n\n" +
            "Windows will ask for administrator approval (UAC). Only this one change runs as administrator and it " +
            "ends straight away. It only flips the task's on/off state, it does not delete anything, and you can " +
            "switch it back here.",
            "Change scheduled task", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;
        b.IsEnabled = false;
        var (success, msg) = await Task.Run(() => StartupTaskScanner.SetTaskEnabled(t.Path, enable));
        StatusLine.Text = msg;
        if (success) await RunStartupScan();
        else { b.IsEnabled = true; MessageBox.Show(this, msg, Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void OnRevealDoc(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && !string.IsNullOrEmpty(path))
            RevealInExplorer(path);
    }

    private void OnDocFilterChanged(object sender, TextChangedEventArgs e)
    {
        _docQuery = DocFilterBox.Text.Trim();
        DocFilterPlaceholder.Visibility =
            string.IsNullOrEmpty(DocFilterBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _docView?.Refresh();
    }

    private void OnDocAllDrives(object sender, RoutedEventArgs e)
    {
        if (DocDriveBar is not null) DocDriveBar.IsEnabled = DocAllDrives.IsChecked != true;
        UpdateDocScanEnabled();
    }

    /// <summary>The roots a documents scan covers, from the picked drives (or all ready drives). The Windows
    /// drive expands to the personal folders + its root (<see cref="DocumentScanner.RootsFor"/>). Empty when
    /// nothing is picked.</summary>
    private IReadOnlyList<string> DocScanRoots()
        => DocumentScanner.RootsFor(DocAllDrives?.IsChecked == true
                ? Venquery.Services.DriveList.Ready().Select(d => d.Root)
                : _docDriveChecks.Where(c => c.IsChecked == true)
                    .Select(c => ((Venquery.Services.DriveChoice)c.Tag).Root))
            .Concat(ExistingFolders(_settings.DocScanFolders))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void UpdateDocScanEnabled()
    {
        if (DocScanButton is null || _docScanCts is not null || _docBackupCts is not null) return;
        var any = DocScanRoots().Count > 0;
        DocScanButton.IsEnabled = any;
        DocScanButton.ToolTip = any ? null : "Pick at least one drive to scan";
    }

    private void OnDocSortChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ApplyDocSort();
    private void OnDocSortDir(object sender, RoutedEventArgs e)
    {
        _docSortDesc = !_docSortDesc;
        DocSortDir.Content = _docSortDesc ? "▼ Desc" : "▲ Asc";
        ApplyDocSort();
    }
    private void ApplyDocSort()
    {
        if (_docView is null) return;
        var field = DocSortBox.SelectedIndex switch { 1 => "SizeBytes", 2 => "CategoryText", 3 => "Modified", _ => "Name" };
        var dir = _docSortDesc ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
        _docView.SortDescriptions.Clear();
        _docView.SortDescriptions.Add(new System.ComponentModel.SortDescription(field, dir));
        if (field != "Name") _docView.SortDescriptions.Add(new System.ComponentModel.SortDescription("Name", System.ComponentModel.ListSortDirection.Ascending));
        _docView.Refresh();
    }

    private void OnDocCategoryChanged(object sender, RoutedEventArgs e)
    {
        var m = Venquery.Services.DocCategory.None;
        if (DocCatDocuments?.IsChecked == true) m |= Venquery.Services.DocCategory.Documents;
        if (DocCatPictures?.IsChecked == true) m |= Venquery.Services.DocCategory.Pictures;
        if (DocCatVideos?.IsChecked == true) m |= Venquery.Services.DocCategory.Videos;
        if (DocCatAudio?.IsChecked == true) m |= Venquery.Services.DocCategory.Audio;
        if (DocCatArchives?.IsChecked == true) m |= Venquery.Services.DocCategory.Archives;
        if (DocCatDiskImages?.IsChecked == true) m |= Venquery.Services.DocCategory.DiskImages;
        _docCategoryFilter = m;
        _docView?.Refresh();
    }

    private void OnDocSelectAll(object sender, RoutedEventArgs e)
    {
        // Operate on the currently visible (filtered) rows only.
        var visible = _docView.Cast<DocEntry>().ToList();
        var allOn = visible.Count > 0 && visible.All(x => x.IsSelected);
        foreach (var x in visible) x.IsSelected = !allOn;
        DocSelectAllButton.Content = allOn ? "Select all" : "Clear all";
    }

    // ---- backup destinations (Portable Apps + Loose Documents) ----
    // The user picks a PARENT folder; Venquery always creates "Portable Apps" / "Portable Documents" inside it,
    // which keeps the scanners' own-output exclusion working wherever the backups end up.

    /// <summary>The chosen parent folder, or Downloads when none is set or it no longer exists (e.g. an
    /// unplugged USB drive), so a backup never fails just because the saved location went away.</summary>
    private static string BackupParent(string setting)
        => !string.IsNullOrWhiteSpace(setting) && System.IO.Directory.Exists(setting) ? setting : DownloadService.DownloadsFolder();

    private bool _syncingReceipt;

    private void OnReceiptToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingReceipt || sender is not System.Windows.Controls.CheckBox cb) return;
        _settings.BackupReceipts = cb.IsChecked == true;
        _settings.Save();
        ShowBackupDestinations();
    }

    private void ShowBackupDestinations()
    {
        _syncingReceipt = true;
        if (PortableReceiptCheck is not null) PortableReceiptCheck.IsChecked = _settings.BackupReceipts;
        if (DocReceiptCheck is not null) DocReceiptCheck.IsChecked = _settings.BackupReceipts;
        _syncingReceipt = false;
        if (PortableBackupDestText is not null)
        {
            var p = System.IO.Path.Combine(BackupParent(_settings.PortableBackupParent), "Portable Apps");
            PortableBackupDestText.Text = p;
            PortableBackupDestText.ToolTip = p;
            PortableBackupDestReset.Visibility = string.IsNullOrWhiteSpace(_settings.PortableBackupParent) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (DocBackupDestText is not null)
        {
            var d = System.IO.Path.Combine(BackupParent(_settings.DocBackupParent), "Portable Documents");
            DocBackupDestText.Text = d;
            DocBackupDestText.ToolTip = d;
            DocBackupDestReset.Visibility = string.IsNullOrWhiteSpace(_settings.DocBackupParent) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Asks for a new parent folder. Refuses a Windows/system location, the picked folder must be
    /// one the user can write to, and the choice is saved straight away.</summary>
    /// <summary>Verifies a finished backup and writes its receipt (when the option is on). Returns the text to add to
    /// the "saved" message, e.g. " · all 212 files checked, receipt saved".</summary>
    private static string ReceiptSummary(BackupReceipt? receipt, string destFolder, string title, IProgress<string> log)
    {
        if (receipt is null || receipt.Files.Count == 0) return "";
        var (ok, bad, path) = receipt.WriteAndVerify(destFolder, title);
        log.Report($"  receipt: {ok} verified, {bad} to check · {System.IO.Path.GetFileName(path)}");
        if (bad > 0) return $" · {bad} file(s) need a look, see the receipt";
        return path.Length > 0 ? $" · all {ok} file(s) checked, receipt saved" : $" · all {ok} file(s) checked";
    }

    private string? PickBackupParent(string current, string what)
    {
        var picked = FolderPicker.Pick(this, $"Choose where to save {what} backups", BackupParent(current));
        if (string.IsNullOrWhiteSpace(picked)) return null;
        string full;
        try { full = System.IO.Path.GetFullPath(picked); } catch { return null; }
        if (IsSystemLocation(full))
        {
            MessageBox.Show(this, "Please pick a folder outside Windows and Program Files.", Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        try
        {
            // Prove it's writable before saving it (a read-only share or a locked folder would only fail later).
            var probe = System.IO.Path.Combine(full, ".venquery-write-test-" + Guid.NewGuid().ToString("N"));
            System.IO.File.WriteAllText(probe, "");
            System.IO.File.Delete(probe);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Venquery can't write to that folder.\n\n" + ex.Message, Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return full;
    }

    internal static bool IsSystemLocation(string full)
    {
        string[] sys =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        var f = full.TrimEnd('\\') + "\\";
        return sys.Where(x => !string.IsNullOrWhiteSpace(x))
                  .Any(x => f.StartsWith(x.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private void OnPortableBackupDestChange(object sender, RoutedEventArgs e)
    {
        var p = PickBackupParent(_settings.PortableBackupParent, "portable app");
        if (p is null) return;
        _settings.PortableBackupParent = p;
        _settings.Save();
        ShowBackupDestinations();
    }

    private void OnPortableBackupDestReset(object sender, RoutedEventArgs e)
    {
        _settings.PortableBackupParent = "";
        _settings.Save();
        ShowBackupDestinations();
    }

    private void OnDocBackupDestChange(object sender, RoutedEventArgs e)
    {
        var p = PickBackupParent(_settings.DocBackupParent, "document");
        if (p is null) return;
        _settings.DocBackupParent = p;
        _settings.Save();
        ShowBackupDestinations();
    }

    private void OnDocBackupDestReset(object sender, RoutedEventArgs e)
    {
        _settings.DocBackupParent = "";
        _settings.Save();
        ShowBackupDestinations();
    }

    /// <summary>Copies each ticked file/folder into a "Portable Documents" folder (Downloads by default, or
    /// the folder chosen under "Save backups to").
    /// Pure copy (reuses <see cref="PortableBackupService"/>), nothing is run or overwritten.</summary>
    private async void OnBackupDocuments(object sender, RoutedEventArgs e)
    {
        if (_docBackupCts is not null) return;

        var sources = _docEntries.Where(x => x.IsSelected && !string.IsNullOrEmpty(x.FullPath))
            .Select(x => x.FullPath).ToList();
        if (sources.Count == 0)
        {
            MessageBox.Show(this, "Nothing is ticked, so there's nothing to back up.",
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dest = System.IO.Path.Combine(BackupParent(_settings.DocBackupParent), "Portable Documents");
        if (MessageBox.Show(this,
            $"Copy {sources.Count} item(s) into:\n\n{dest}\n\n" +
            "Your files are copied (this can be large and take a while). Nothing is opened, run, or " +
            "overwritten. Continue?",
            "Back up loose documents", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
            != MessageBoxResult.Yes) return;

        DocBackupSavedBar.Visibility = Visibility.Collapsed;
        BackupDocsButton.IsEnabled = false;
        DocScanButton.IsEnabled = false;
        var t = BeginTask("Backing up documents…", determinate: true);

        var log = MakeConsoleLog();
        log.Report($"> documents backup · {sources.Count} item(s) -> {dest}");
        var progress = new Progress<(int done, int total, string current)>(p =>
            t.Report(p.total > 0 ? (double)p.done / p.total * 100.0 : 0,
                     $"Copying {p.done} of {p.total} · {Ellipsize(p.current, 48)}"));

        _docBackupCts = new CancellationTokenSource();
        var docReceipt = _settings.BackupReceipts ? new BackupReceipt() : null;
        PortableBackupResult res;
        try
        {
            // Sort each item's content into per-type sub-folders, honouring the category filter.
            res = await PortableBackupService.BackupByCategoryAsync(sources, dest, _docCategoryFilter, progress, _docBackupCts.Token, docReceipt);
        }
        catch (Exception ex)
        {
            log.Report($"  documents backup error: {ex.Message}");
            MessageBox.Show(this, "The backup couldn't complete.\n\n" + ex.Message, Edition.Current.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            t.Done();
            _docBackupCts.Dispose();
            _docBackupCts = null;
            BackupDocsButton.IsEnabled = true;
            UpdateDocScanEnabled();
        }

        if (!res.Ok)
        {
            MessageBox.Show(this, "Could not create the Portable Documents folder.\n\n" + res.Error,
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _savedDocBackupFolder = res.DestFolder;
        var head = res.Cancelled
            ? $"Cancelled, copied {res.Copied} item(s) before stopping"
            : $"Copied {res.Copied} item(s) to {res.DestFolder}";
        var tail = res.Skipped > 0 ? $" · {res.Skipped} skipped" : "";
        tail += ReceiptSummary(docReceipt, res.DestFolder, "Loose Documents backup", log);
        DocBackupSavedText.Text = head + tail + ".";
        DocBackupSavedBar.Visibility = Visibility.Visible;
        log.Report($"> documents backup {(res.Cancelled ? "cancelled" : "done")} · {res.Copied} copied / {res.Skipped} skipped");
        foreach (var n in res.Notes) log.Report("  " + n);
        StatusLine.Text = $"{AppVersion} · documents backup · {res.Copied} item(s) copied";
    }

    private void OnCancelDocBackup(object sender, RoutedEventArgs e) => _docBackupCts?.Cancel();

    private void OnOpenDocBackupFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_savedDocBackupFolder)) return;
        try { Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("explorer.exe"), $"\"{_savedDocBackupFolder}\"") { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    /// <summary>Human byte size for the stats tile.</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):0.0} KB";
        return $"{bytes} B";
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    internal static void RevealInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo(Venquery.Services.SystemTools.Exe("explorer.exe"), $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    private static string Ellipsize(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : "…" + s[^(max - 1)..];
    }

    private System.Windows.Controls.Border MakeExcludeChip(string path)
    {
        var text = new TextBlock
        {
            Text = "✕  " + path,
            FontSize = 11.5,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        var chip = new System.Windows.Controls.Border
        {
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(0, 0, 7, 7),
            BorderThickness = new Thickness(1),
            Child = text,
        };
        chip.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Ctl");
        chip.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "CtlBd");
        return chip;
    }
}
