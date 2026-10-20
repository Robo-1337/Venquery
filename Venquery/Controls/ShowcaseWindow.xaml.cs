using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Venquery;
using Venquery.Models;
using Venquery.Services;

namespace Venquery.Controls;

/// <summary>The fullscreen "Showcase" screen: a clean, screenshot-ready spec card the user can photograph to
/// show off their PC. Four switchable presets (hero / spec sheet / poster / tiles), its OWN background image
/// with its own opacity, and an optional "details" mode that reveals per-part model numbers + manufacturers.
/// Borderless + maximized (covers the taskbar); Esc exits. No Venquery branding on the screen itself.
/// General feature, every edition, reads only cached WMI data, writes nothing but the (optional) background copy.</summary>
public partial class ShowcaseWindow : Window
{
    private readonly SystemInfo _sys;
    private readonly MachineFingerprint _fp;
    private readonly HealthReport? _health;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _hintTimer;
    private bool _details;
    private bool _ready;

    private sealed record Spec(string Label, string Value, string Detail);

    public ShowcaseWindow(SystemInfo sys, MachineFingerprint fp, HealthReport? health, AppSettings settings)
    {
        _sys = sys; _fp = fp; _health = health; _settings = settings;
        InitializeComponent();

        _details = settings.ShowcaseShowDetails;
        DetailsToggle.IsChecked = _details;
        OpacitySlider.Value = settings.ShowcaseBackgroundOpacity is >= 0.1 and <= 0.9 ? settings.ShowcaseBackgroundOpacity : 0.35;
        ApplyBg(settings.ShowcaseBackgroundImage);
        BuildPreset(string.IsNullOrWhiteSpace(settings.ShowcasePreset) ? "hero" : settings.ShowcasePreset);

        // The control strip is HIDDEN by default and only appears when the mouse hovers the top of the screen
        // (so it stays out of a photo). Show it briefly on open as a discoverability hint, then hover governs.
        MouseMove += OnShowcaseMouseMove;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); if (!PointerNearTop()) ControlBar.Visibility = Visibility.Collapsed; };
        _hintTimer.Start();
        _ready = true;
    }

    protected override void OnClosed(EventArgs e) { _hintTimer.Stop(); base.OnClosed(e); }

    // ---- control strip (hover the top edge to reveal) ---------------------
    /// <summary>Reveal band: while shown, the real bar height (+6); while hidden its ActualHeight is 0, so a
    /// 60px top strip triggers the reveal. The gap gives natural hysteresis so it doesn't flicker at the edge.</summary>
    private double RevealZone => ControlBar.ActualHeight > 0 ? ControlBar.ActualHeight + 6 : 60;
    private bool PointerNearTop() => Mouse.GetPosition(this).Y <= RevealZone;

    private void OnShowcaseMouseMove(object sender, MouseEventArgs e)
        => ControlBar.Visibility = e.GetPosition(this).Y <= RevealZone ? Visibility.Visible : Visibility.Collapsed;

    // Keep the bar up right after an on-bar action (the pointer is already in the zone, so a later move keeps it).
    private void RevealBar()
    {
        if (ControlBar.Visibility != Visibility.Visible) ControlBar.Visibility = Visibility.Visible;
    }

    private void OnPickPreset(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string p) BuildPreset(p);
        RevealBar();
    }

    private void OnToggleDetails(object sender, RoutedEventArgs e)
    {
        _details = DetailsToggle.IsChecked == true;
        _settings.ShowcaseShowDetails = _details;
        BuildPreset(_settings.ShowcasePreset);
        RevealBar();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The slider precedes OpacityText in the XAML, so this fires once during InitializeComponent before
        // OpacityText exists, guard against the named fields still being null.
        if (BgImage is null || OpacityText is null) return;
        if (BgImage.Source is not null) BgImage.Opacity = e.NewValue;
        if (_ready) _settings.ShowcaseBackgroundOpacity = e.NewValue;
        UpdateOpacityText();
        if (_ready) RevealBar();
    }

    private void UpdateOpacityText()
    {
        if (OpacityText is null) return;
        OpacityText.Text = BgImage?.Source is null ? "" : $"{OpacitySlider.Value * 100:0}%";
    }

    private void OnChooseBg(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a showcase background",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            CheckFileExists = true,
            InitialDirectory = ThemeAssets.Folder(),
        };
        if (dlg.ShowDialog(this) != true) return;
        if (ThemeAssets.IsWebp(dlg.FileName))
        {
            MessageBox.Show(this, "That file is a WebP image saved with a .png or .jpg name. Windows apps can't " +
                "display WebP directly, re-save it as a real PNG or JPG, then pick it again.",
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string name;
        try { name = ThemeAssets.Import(dlg.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't copy that image into the Themes folder.\n\n" + ex.Message,
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ApplyBg(name);
        if (BgImage.Source is null)
        {
            MessageBox.Show(this, "That image couldn't be loaded. Try a different PNG or JPG.",
                Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.ShowcaseBackgroundImage = name;
        RevealBar();
    }

    private void OnClearBg(object sender, RoutedEventArgs e)
    {
        _settings.ShowcaseBackgroundImage = "";
        ApplyBg(null);
        RevealBar();
    }

    private void ApplyBg(string? fileName)
    {
        var path = ThemeAssets.Resolve(fileName);
        if (path is null) { BgImage.Source = null; BgImage.Visibility = Visibility.Collapsed; UpdateOpacityText(); return; }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            BgImage.Source = bmp;
            BgImage.Opacity = OpacitySlider.Value;
            BgImage.Visibility = Visibility.Visible;
        }
        catch { BgImage.Source = null; BgImage.Visibility = Visibility.Collapsed; }
        UpdateOpacityText();
    }

    // ---- preset dispatch --------------------------------------------------
    private void BuildPreset(string preset)
    {
        _settings.ShowcasePreset = preset;
        HighlightPreset(preset);
        ContentHost.Children.Clear();
        FrameworkElement view = preset switch
        {
            "sheet" => BuildSheet(),
            "poster" => BuildPoster(),
            "tiles" => BuildTiles(),
            _ => BuildHero(),
        };
        ContentHost.Children.Add(view);
    }

    private void HighlightPreset(string preset)
    {
        var accent = (Style)FindResource("AccentButton");
        var chrome = (Style)FindResource("ChromeButton");
        PresetHeroBtn.Style = preset == "hero" ? accent : chrome;
        PresetSheetBtn.Style = preset == "sheet" ? accent : chrome;
        PresetPosterBtn.Style = preset == "poster" ? accent : chrome;
        PresetTilesBtn.Style = preset == "tiles" ? accent : chrome;
    }

    // ---- data helpers -----------------------------------------------------
    private string ModelName => string.IsNullOrWhiteSpace(_sys.Model)
        ? _sys.DisplayTitle
        : _sys.Model.Trim().TrimEnd('-', '_', ' ').Trim();

    private string SubLine()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_sys.Type) && _sys.Type != "PC") parts.Add(_sys.Type);
        if (!string.IsNullOrWhiteSpace(_sys.Year)) parts.Add($"manufactured {_sys.Year}");
        return string.Join("  ·  ", parts);
    }

    private string InstallLine() => _health?.InstallDate is DateTime d ? $"Windows installed {d:MMM yyyy}" : "";

    /// <summary>The machine's REAL GPUs (virtual/software display adapters filtered out), discrete first. The
    /// raw <see cref="MachineFingerprint.Gpu"/> is a "; "-joined Win32_VideoController list.</summary>
    private List<string> RealGpus() => GpuFilter.Clean((_fp.Gpu ?? "").Split(';'));
    private string GpuPrimary() => RealGpus().FirstOrDefault() ?? "";
    private string GpuAll() => string.Join(" · ", RealGpus());

    private string MemoryValue()
    {
        var baseRam = _sys.Ram;
        if (string.IsNullOrWhiteSpace(baseRam) && _health?.Memory is { } mi) baseRam = $"{HealthReport.FormatBytes(mi.TotalBytes)} RAM";
        var speed = 0;
        var mods = _health?.Memory?.Modules;
        if (mods is not null) foreach (var m in mods) if (m.SpeedMhz > speed) speed = m.SpeedMhz;
        return speed > 0 ? $"{baseRam.Replace(" RAM", "")} @ {speed} MHz" : baseRam;
    }

    private string MemoryDetail()
    {
        var mods = _health?.Memory?.Modules;
        if (mods is null || mods.Count == 0) return "";
        var parts = new List<string>();
        foreach (var m in mods)
        {
            var bits = new List<string> { HealthReport.FormatBytes(m.SizeBytes) };
            if (!string.IsNullOrWhiteSpace(m.Manufacturer)) bits.Add(m.Manufacturer.Trim());
            if (!string.IsNullOrWhiteSpace(m.PartNumber)) bits.Add(m.PartNumber.Trim());
            parts.Add(string.Join(" ", bits));
        }
        return string.Join(", ", parts);
    }

    private static string DiskShort(DiskHealth x)
    {
        var parts = new List<string> { HealthReport.FormatBytes(x.SizeBytes) };
        if (!string.IsNullOrWhiteSpace(x.Bus)) parts.Add(x.Bus);
        if (!string.IsNullOrWhiteSpace(x.Kind)) parts.Add(x.Kind);
        return string.Join(" ", parts);
    }

    private string StorageValue()
    {
        var d = _health?.Disks;
        if (d is null || d.Count == 0) return "";
        if (d.Count == 1) return DiskShort(d[0]);
        long total = 0; foreach (var x in d) total += x.SizeBytes;
        return $"{HealthReport.FormatBytes(total)} · {d.Count} drives";
    }

    private string StorageDetail()
    {
        var d = _health?.Disks;
        if (d is null || d.Count == 0) return "";
        var parts = new List<string>();
        foreach (var x in d) parts.Add(string.IsNullOrWhiteSpace(x.Model) ? DiskShort(x) : $"{x.Model.Trim()} ({DiskShort(x)})");
        return string.Join(", ", parts);
    }

    private List<Spec> Specs()
    {
        var list = new List<Spec>
        {
            new("Processor", _details ? _sys.CpuDetailed : _sys.Cpu, ""),
            new("Graphics", GpuPrimary(), _details ? GpuAll() : ""),
            new("Memory", MemoryValue(), _details ? MemoryDetail() : ""),
            new("Storage", StorageValue(), _details ? StorageDetail() : ""),
            new("System", _sys.Os, ""),
            new("Motherboard", _details ? _fp.Board : ShortBoard(), ""),
        };
        return list.FindAll(s => !string.IsNullOrWhiteSpace(s.Value));
    }

    private string ShortBoard() => string.IsNullOrWhiteSpace(_fp.Board) ? "" : _fp.Board;

    // ---- small UI builders ------------------------------------------------
    private Brush Br(string key) => (Brush)FindResource(key);

    private TextBlock T(string text, double size, string brush, FontWeight? weight = null,
        TextAlignment align = TextAlignment.Left, double maxWidth = 0)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = size, Foreground = Br(brush),
            FontWeight = weight ?? FontWeights.Normal, TextAlignment = align,
            TextWrapping = maxWidth > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        if (maxWidth > 0) tb.MaxWidth = maxWidth;
        return tb;
    }

    // ---- preset A: hero ---------------------------------------------------
    private FrameworkElement BuildHero()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(_sys.Manufacturer))
            panel.Children.Add(new TextBlock { Text = _sys.Manufacturer.ToUpperInvariant(), FontSize = 15, Foreground = Br("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = ModelName, FontSize = 52, FontWeight = FontWeights.SemiBold, Foreground = Br("Text"), Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 1000, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(SubLine()))
            panel.Children.Add(new TextBlock { Text = SubLine(), FontSize = 15, Foreground = Br("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
        if (!string.IsNullOrWhiteSpace(InstallLine()))
            panel.Children.Add(new TextBlock { Text = InstallLine(), FontSize = 13, Foreground = Br("Dim"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });

        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 34, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var s in Specs())
        {
            var cell = new StackPanel { Margin = new Thickness(22, 12, 22, 12), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 200 };
            cell.Children.Add(new TextBlock { Text = s.Label.ToUpperInvariant(), FontSize = 11, Foreground = Br("Dim"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) });
            cell.Children.Add(new TextBlock { Text = s.Value, FontSize = 17, FontWeight = FontWeights.Medium, Foreground = Br("Text"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 240, HorizontalAlignment = HorizontalAlignment.Center });
            if (!string.IsNullOrWhiteSpace(s.Detail))
                cell.Children.Add(new TextBlock { Text = s.Detail, FontSize = 12, Foreground = Br("Muted"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 240, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(cell);
        }
        panel.Children.Add(grid);
        return panel;
    }

    // ---- preset B: spec sheet --------------------------------------------
    private FrameworkElement BuildSheet()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bar = new Border { Background = Br("Accent") }; Grid.SetColumn(bar, 0);
        var body = new StackPanel { Margin = new Thickness(30, 26, 30, 26) }; Grid.SetColumn(body, 1);
        grid.Children.Add(bar); grid.Children.Add(body);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var head = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new TextBlock { Text = ModelName, FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Br("Text"), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        var makerType = string.Join("  ·  ", new[] { _sys.Manufacturer, _sys.Type == "PC" ? "" : _sys.Type }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(makerType)) head.Children.Add(new TextBlock { Text = makerType, FontSize = 13, Foreground = Br("Muted"), Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetColumn(head, 0);
        top.Children.Add(head);
        if (!string.IsNullOrWhiteSpace(_sys.Year))
        {
            var yr = new TextBlock { Text = _sys.Year, FontSize = 42, FontWeight = FontWeights.SemiBold, Foreground = Br("Accent"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(yr, 1); top.Children.Add(yr);
        }
        body.Children.Add(top);

        var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        foreach (var s in Specs())
        {
            var r = new Grid { Margin = new Thickness(0) };
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rowPanel = new StackPanel { Margin = new Thickness(0, 9, 0, 9) };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var k = new TextBlock { Text = s.Label, FontSize = 13, Foreground = Br("Muted"), VerticalAlignment = VerticalAlignment.Top }; Grid.SetColumn(k, 0);
            var vPanel = new StackPanel(); Grid.SetColumn(vPanel, 1);
            vPanel.Children.Add(new TextBlock { Text = s.Value, FontSize = 15, Foreground = Br("Text"), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(s.Detail)) vPanel.Children.Add(new TextBlock { Text = s.Detail, FontSize = 12, Foreground = Br("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            line.Children.Add(k); line.Children.Add(vPanel);
            var sep = new Border { BorderBrush = Br("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = new StackPanel { Children = { line }, Margin = new Thickness(0, 9, 0, 9) } };
            rows.Children.Add(sep);
        }
        body.Children.Add(rows);
        if (!string.IsNullOrWhiteSpace(InstallLine()))
            body.Children.Add(new TextBlock { Text = InstallLine(), FontSize = 12, Foreground = Br("Dim"), Margin = new Thickness(0, 12, 0, 0) });

        return new Border
        {
            CornerRadius = new CornerRadius(16), Background = Br("Card"), BorderBrush = Br("Line"), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 560, MaxWidth = 760, Child = grid, ClipToBounds = true,
        };
    }

    // ---- preset C: poster -------------------------------------------------
    private FrameworkElement BuildPoster()
    {
        var host = new Grid { Margin = new Thickness(64, 54, 64, 54) };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (!string.IsNullOrWhiteSpace(_sys.Year))
        {
            var wm = new TextBlock { Text = _sys.Year, FontSize = 190, FontWeight = FontWeights.SemiBold, Foreground = Br("LineStrong"), Opacity = 0.30, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -30, -10, 0) };
            Grid.SetRowSpan(wm, 2); host.Children.Add(wm);
        }
        var maker = string.Join("  ·  ", new[] { _sys.Manufacturer, _sys.Type == "PC" ? "" : _sys.Type }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var makerTb = new TextBlock { Text = maker.ToUpperInvariant(), FontSize = 15, Foreground = Br("Muted"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(makerTb, 0); host.Children.Add(makerTb);

        var model = new TextBlock { Text = ModelName, FontSize = 62, FontWeight = FontWeights.SemiBold, Foreground = Br("Text"), TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(model, 1); host.Children.Add(model);

        var ribbon = new Border { BorderBrush = Br("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 16, 0, 0) };
        var strip = new UniformGrid { Rows = 1, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var s in RibbonSpecs())
        {
            var seg = new StackPanel { Margin = new Thickness(0, 0, 22, 0) };
            seg.Children.Add(new TextBlock { Text = s.Label.ToUpperInvariant(), FontSize = 11, Foreground = Br("Dim") });
            seg.Children.Add(new TextBlock { Text = s.Value, FontSize = 16, FontWeight = FontWeights.Medium, Foreground = Br("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            strip.Children.Add(seg);
        }
        ribbon.Child = strip;
        Grid.SetRow(ribbon, 2); host.Children.Add(ribbon);
        return host;
    }

    private List<Spec> RibbonSpecs()
    {
        var wanted = new[] { "Processor", "Graphics", "Memory", "Storage", "System" };
        var all = Specs();
        var gpus = RealGpus();
        var picked = new List<Spec>();
        foreach (var w in wanted)
        {
            var m = all.Find(s => s.Label == w);
            if (m is null) continue;
            // Poster shows BOTH GPUs (dedicated + integrated), each on its own line, not just the primary.
            picked.Add(w == "Graphics" && gpus.Count > 1 ? new Spec("Graphics", string.Join("\n", gpus), "") : m with { Detail = "" });
        }
        return picked.Count > 0 ? picked : all;
    }

    // ---- preset D: tiles --------------------------------------------------
    private FrameworkElement BuildTiles()
    {
        var outer = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 900 };

        var header = new Grid { Margin = new Thickness(4, 0, 4, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var htext = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        htext.Children.Add(new TextBlock { Text = ModelName, FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Br("Text"), TextWrapping = TextWrapping.Wrap });
        var sub = SubLine(); if (!string.IsNullOrWhiteSpace(sub)) htext.Children.Add(new TextBlock { Text = sub, FontSize = 13, Foreground = Br("Muted"), Margin = new Thickness(0, 3, 0, 0) });
        if (!string.IsNullOrWhiteSpace(InstallLine())) htext.Children.Add(new TextBlock { Text = InstallLine(), FontSize = 12, Foreground = Br("Dim"), Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(htext, 0); header.Children.Add(htext);
        outer.Children.Add(header);

        var grid = new UniformGrid { Columns = 3, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var s in Specs())
        {
            var tile = new Border { CornerRadius = new CornerRadius(12), Background = Br("Card"), BorderBrush = Br("Line"), BorderThickness = new Thickness(1), Margin = new Thickness(6), Padding = new Thickness(16, 14, 16, 14), MinWidth = 240 };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = s.Label.ToUpperInvariant(), FontSize = 11, Foreground = Br("Dim"), Margin = new Thickness(0, 0, 0, 5) });
            sp.Children.Add(new TextBlock { Text = s.Value, FontSize = 18, FontWeight = FontWeights.Medium, Foreground = Br("Text"), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(s.Detail)) sp.Children.Add(new TextBlock { Text = s.Detail, FontSize = 12, Foreground = Br("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            tile.Child = sp;
            grid.Children.Add(tile);
        }
        outer.Children.Add(grid);
        return outer;
    }
}
