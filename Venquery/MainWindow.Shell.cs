using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Venquery.Services;

namespace Venquery;

// The 1.4.0 shell: a labelled rail of PAGES (sections), each with a shared header (title, subtitle, actions) and
// optional Pivot TABS. Every tab maps to one of the existing view tags ("drivers", "backup", "portable", ...), so
// SwitchView, the Safe Mode pill wording and each view's lazy loading keep working unchanged.
public partial class MainWindow
{
    /// <summary>Width of the nav rail (also used by ApplyNavSide for left-hand mode).</summary>
    private const double RailWidth = 84;

    private sealed record PageTab(string View, string Label, string AutomationId);

    private sealed class Section
    {
        public Section(string id, string title, string subtitle, List<PageTab> tabs, Func<IEnumerable<UIElement>>? actions = null)
        { Id = id; Title = title; Subtitle = subtitle; Tabs = tabs; Actions = actions; }
        public string Id { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public List<PageTab> Tabs { get; }
        public Func<IEnumerable<UIElement>>? Actions { get; }
    }

    private readonly Dictionary<string, Section> _sections = new();
    private readonly Dictionary<string, string> _sectionTab = new();   // last tab (view) used per section
    private string _currentSection = "drivers";

    /// <summary>Define the pages and their tabs for this edition. Called once from the ctor, after the optional
    /// pages (Repair Kit) have been built.</summary>
    private void InitSections()
    {
        var one = (Func<string, List<PageTab>>)(v => new List<PageTab> { new PageTab(v, "", "") });

        _sections["drivers"] = new Section("drivers", "Devices & Drivers",
            "Your hardware, matched to official driver sources.", one("drivers"),
            () => new UIElement[]
            {
                HeaderButton("Windows Update drivers", "IcoUpdate", OnCheckWindowsUpdateDrivers, "HeaderWuDrivers",
                    "Ask Windows Update which driver updates it has for this PC (read-only, installs nothing)"),
            });

        var backupTabs = new List<PageTab>();
        if (Edition.Current.HasWinget) backupTabs.Add(new PageTab("backup", "Installed apps", "TabBackupApps"));
        backupTabs.Add(new PageTab("portable", "Portable apps", "TabBackupPortable"));
        backupTabs.Add(new PageTab("documents", "Documents", "TabBackupDocuments"));
#if !LEGACY
        backupTabs.Add(new PageTab("restore", "Restore", "TabBackupRestore"));
#endif
        _sections["backup"] = new Section("backup", "Backup & Restore",
            Edition.Current.HasWinget
                ? "Save your apps, portable apps and documents before a reset, then bring them back afterwards."
                : "Copy your portable apps and loose documents to a folder or a USB stick.",
            backupTabs,
            () => Edition.Current.HasWinget
                ? new UIElement[]
                {
                    HeaderButton("Back up everything...", "IcoBackupAll", OnOpenResetWizard, "HeaderBackupAll",
                        "Prepare for Reset: back up apps, portable apps, documents and drivers in one go before you reinstall Windows", accent: true),
                }
                : Array.Empty<UIElement>());

        _sections["files"] = new Section("files", "Files",
            "Tidy a cluttered folder, and see what's using space.",
            new List<PageTab>
            {
                new PageTab("organize", "Organize", "TabFilesOrganize"),
                new PageTab("space", "What's using space", "TabFilesSpace"),
            });

        var healthTabs = new List<PageTab> { new PageTab("health", "Overview", "TabHealthOverview") };
#if PROTOOLS
        healthTabs.Add(new PageTab("protools", Edition.Current.ToolsName, "TabHealthTools"));
#endif
        healthTabs.Add(new PageTab("startup", "Startup & Tasks", "TabHealthStartup"));
        _sections["health"] = new Section("health", "Health & Repair",
            "A read-only look at this PC's health from Windows' own data, plus repairs you approve first.",
            healthTabs,
            () => new UIElement[]
            {
                HeaderButton("Showcase", "IcoShowcase", OnOpenShowcase, "HeaderShowcase",
                    "A clean fullscreen spec screen to show off this PC (Esc to exit)"),
                HeaderButton("Refresh", "IcoRefresh", OnHeaderRefresh, "HeaderRefresh",
                    "Read this PC's health again (or the startup lists on Startup & Tasks)"),
            });

        _sections["settings"] = new Section("settings", "Settings",
            "Theme, background, layout and where backups go.", one("settings"));
    }

    /// <summary>The page (section) a view tag belongs to, e.g. "portable" → "backup".</summary>
    private string SectionOf(string view)
    {
        foreach (var s in _sections.Values)
            if (s.Tabs.Any(t => t.View == view)) return s.Id;
        return "drivers";
    }

    private RadioButton? RailFor(string section) => section switch
    {
        "drivers" => NavDrivers,
        "backup" => NavBackup,
        "files" => NavFiles,
        "health" => NavHealth,
        "settings" => NavSettings,
        _ => null,
    };

    /// <summary>Open a page (and optionally a specific tab): fill the header, build the tabs, show the view.</summary>
    private void ShowSection(string id, string? view = null)
    {
        if (!_sections.TryGetValue(id, out var s)) s = _sections["drivers"];
        _currentSection = s.Id;
        var v = view ?? (_sectionTab.TryGetValue(s.Id, out var last) ? last : s.Tabs[0].View);
        if (!s.Tabs.Any(t => t.View == v)) v = s.Tabs[0].View;
        _sectionTab[s.Id] = v;

        PageTitleText.Text = s.Title;
        PageSubtitleText.Text = s.Id == "drivers" ? DriversSubtitle() : s.Subtitle;
        PageSubtitleText.ToolTip = PageSubtitleText.Text;

        HeaderActions.Children.Clear();
        if (s.Actions is not null)
            foreach (var a in s.Actions()) HeaderActions.Children.Add(a);

        PageTabs.Children.Clear();
        if (s.Tabs.Count > 1)
        {
            foreach (var t in s.Tabs)
            {
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("PivotTab"),
                    GroupName = "pagetabs",
                    Content = t.Label,
                    Tag = t.View,
                    IsChecked = t.View == v,      // set before Checked is wired, so building fires nothing
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(rb, t.AutomationId);
                rb.Checked += OnPageTabChecked;
                PageTabs.Children.Add(rb);
            }
            PageTabs.Visibility = Visibility.Visible;
        }
        else PageTabs.Visibility = Visibility.Collapsed;

        SwitchView(v);
    }

    private void OnPageTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string view }) return;
        _sectionTab[_currentSection] = view;
        SwitchView(view);
    }

    /// <summary>Reopen a saved view (old single-page tags like "portable" or "startup" map to their new page + tab).</summary>
    private void RestoreLastView(string? saved)
    {
        var view = string.IsNullOrWhiteSpace(saved) ? "drivers" : saved!;
        var section = SectionOf(view);
        if (!_sections[section].Tabs.Any(t => t.View == view)) { section = "drivers"; view = "drivers"; }
        _sectionTab[section] = view;
        var rail = RailFor(section);
        if (rail is { IsChecked: true } || rail is null) ShowSection(section, view);
        else rail.IsChecked = true;   // fires OnNavChanged → ShowSection(section), which picks the saved tab
    }

    /// <summary>The Devices &amp; Drivers subtitle: this PC's real model and specs once read, a plain line before.</summary>
    private string DriversSubtitle()
    {
        if (_sysInfo is { HasAnything: true } s)
            return string.IsNullOrWhiteSpace(s.SpecLine) ? s.DisplayTitle : $"{s.DisplayTitle}  ·  {s.SpecLine}";
        return "Your hardware, matched to official driver sources.";
    }

    /// <summary>Health &amp; Repair's header Refresh: re-reads whatever the open tab shows.</summary>
    private void OnHeaderRefresh(object sender, RoutedEventArgs e)
    {
        if (_currentView == "startup") OnRefreshStartup(sender, e);
        else OnRefreshHealth(sender, e);
    }

    /// <summary>Build a rail entry for an optional page and append it after the shared pages.</summary>
    private RadioButton AddNavItem(string tag, string label, string iconKey, string automationId)
    {
        var rb = new RadioButton
        {
            Style = (Style)FindResource("NavItem"),
            GroupName = "nav",
            Tag = tag,
            ToolTip = label,
            Content = new StackPanel
            {
                Children =
                {
                    new Path { Style = (Style)FindResource("NavRailIcon"), Data = (Geometry)FindResource(iconKey) },
                    new TextBlock { Style = (Style)FindResource("NavLabel"), Text = label },
                },
            },
        };
        rb.Checked += OnNavChanged;
        // Injected controls have no x:Name, so set the AutomationId the XAML entries get (UI Automation / page tour).
        System.Windows.Automation.AutomationProperties.SetAutomationId(rb, automationId);
        NavStack.Children.Add(rb);
        return rb;
    }

    private Button HeaderButton(string text, string iconKey, RoutedEventHandler click, string automationId, string tip, bool accent = false)
    {
        var icon = new Path { Style = (Style)FindResource("BtnIcon"), Data = (Geometry)FindResource(iconKey) };
        var b = new Button
        {
            Style = (Style)FindResource(accent ? "AccentButton" : "ChromeButton"),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = tip,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { icon, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        b.Click += click;
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, automationId);
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>Draw summary tiles into a WrapPanel (collapsed when there are none, e.g. before a scan).</summary>
    private void RenderTiles(Panel host, IReadOnlyList<SummaryTile> tiles)
    {
        host.Children.Clear();
        for (var i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            // Mockup C: the FIRST tile of a row is the solid-accent "hero" tile; the rest are neutral cards. A warning
            // keeps its meaning through an amber icon (the number and label stay exactly as read).
            var hero = i == 0;
            var content = new Grid();
            if (!string.IsNullOrEmpty(t.Icon) && TryFindResource(t.Icon) is Geometry geo)
            {
                var icon = new Path { Style = (Style)FindResource(hero ? "TileHeroIcon" : "TileIcon"), Data = geo };
                if (!hero && t.Tone == TileTone.Warning) { icon.SetResourceReference(Shape.StrokeProperty, "Amber"); icon.Opacity = 1; }
                content.Children.Add(icon);
            }
            content.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = t.Value, Style = (Style)FindResource(hero ? "TileHeroValue" : "TileValue") },
                    new TextBlock { Text = t.Label, Style = (Style)FindResource(hero ? "TileHeroLabel" : "TileLabel") },
                },
            });
            var border = new Border { Style = (Style)FindResource(hero ? "TileHero" : "Tile"), Child = content };
            System.Windows.Automation.AutomationProperties.SetName(border, $"{t.Value} {t.Label}");
            host.Children.Add(border);
        }
        if (host is System.Windows.Controls.Primitives.UniformGrid grid)
        {
            // Even columns across the full content width, like mockup C.
            grid.Columns = Math.Max(1, tiles.Count);
        }
        host.Visibility = tiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _scanFinished;   // true once the current hardware scan has produced its device list

    // Health shows stability as a card in its grid (mockup C), so its tile row is storage / space / battery / memory.
    private void UpdateHealthTiles() => RenderTiles(HealthTiles, SummaryTiles.ForHealth(_health, null));

    private void UpdateStartupTiles() =>
        RenderTiles(StartupTiles, SummaryTiles.ForStartup(_startupItems, _tasks, _showMsTasks));

    /// <summary>Recompute the Devices &amp; Drivers tiles from the scanned rows and stability report.</summary>
    private void UpdateDriverTiles()
    {
        var scanned = _scanFinished;
        RenderTiles(DriverTiles, SummaryTiles.ForDrivers(scanned, _rows.Count, _rows.Count(r => r.NewestIsUpdate),
            _rows.Count(r => r.HasVerifiedSource), _stability));
    }

    /// <summary>With a background image showing, give the title bar, rail and content layer a scrim so text stays
    /// readable over any picture; without one they're transparent over the theme colour.</summary>
    private void ApplyBackgroundScrims(bool hasImage)
    {
        if (hasImage)
        {
            TitleBar.SetResourceReference(Panel.BackgroundProperty, "ChromeOverImage");
            NavRail.SetResourceReference(Border.BackgroundProperty, "ChromeOverImage");
            ContentLayer.SetResourceReference(Border.BackgroundProperty, "LayerOverImage");
        }
        else
        {
            TitleBar.Background = Brushes.Transparent;
            NavRail.Background = Brushes.Transparent;
            ContentLayer.SetResourceReference(Border.BackgroundProperty, "Layer");
        }
    }
}
