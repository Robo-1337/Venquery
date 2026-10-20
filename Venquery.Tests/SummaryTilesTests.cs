using System;
using System.Collections.Generic;
using System.Linq;
using Venquery.Models;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the 1.4.0 summary tiles: every value comes from real data, and unknown data means NO tile
/// (never a placeholder or a guess). Also guards the "updates available" rule the Devices &amp; Drivers tile uses.</summary>
public class SummaryTilesTests
{
    private const long GB = 1024L * 1024 * 1024;

    // ---- Devices & Drivers ----

    [Fact]
    public void Driver_tiles_are_empty_until_the_scan_has_finished()
        => Assert.Empty(SummaryTiles.ForDrivers(scanned: false, devices: 7, updates: 1, verified: 7, stability: null));

    [Fact]
    public void Driver_tiles_show_the_scan_counts_and_flag_updates()
    {
        var t = SummaryTiles.ForDrivers(true, devices: 7, updates: 1, verified: 6, stability: null);
        Assert.Equal(3, t.Count);                                  // no stability tile until stability was read
        Assert.Equal(("1", "driver update available", TileTone.Accent), (t[0].Value, t[0].Label, t[0].Tone));
        Assert.Equal(("7", "devices listed"), (t[1].Value, t[1].Label));
        Assert.Equal(("6", "with an official source"), (t[2].Value, t[2].Label));
    }

    [Fact]
    public void No_updates_is_a_neutral_tile_with_plural_wording()
    {
        var t = SummaryTiles.ForDrivers(true, 1, 0, 1, null);
        Assert.Equal(("0", "driver updates available", TileTone.Normal), (t[0].Value, t[0].Label, t[0].Tone));
        Assert.Equal("device listed", t[1].Label);
    }

    [Fact]
    public void Stability_tile_counts_only_serious_problems_and_says_problems_not_crashes()
    {
        var s = new StabilityReport { Days = 30 };
        s.Events.Add(new StabilityEvent(DateTime.Now, "crash", "BSOD", ""));
        s.Events.Add(new StabilityEvent(DateTime.Now, "freeze", "Hang", ""));
        s.Events.Add(new StabilityEvent(DateTime.Now, "app", "App crash", ""));      // app crashes don't count
        s.Events.Add(new StabilityEvent(DateTime.Now, "device", "Driver", ""));     // nor device faults
        var tile = SummaryTiles.Stability(s)!;
        Assert.Equal("2", tile.Value);
        Assert.Equal("stability problems, last 30 days", tile.Label);
        Assert.Equal(TileTone.Warning, tile.Tone);
        Assert.DoesNotContain("crash", tile.Label);

        var clean = SummaryTiles.Stability(new StabilityReport { Days = 30 })!;
        Assert.Equal(("0", TileTone.Normal), (clean.Value, clean.Tone));
        Assert.Null(SummaryTiles.Stability(null));
    }

    // ---- the updates rule (DeviceRow.NewestIsUpdate) ----

    private static DeviceRow Nv(string installed, params string[] offered)
    {
        var def = new Definition { Label = "NVIDIA GeForce GPUs", Resolver = "nvidia-geforce" };
        var resolved = offered.Select(v => new VersionEntry { Version = v, Url = $"https://nv/{v}.exe" });
        var hw = new ScannedDevice { Name = "NVIDIA GeForce GTX 1050", PnpClass = "Display", InstalledVersion = installed };
        return new DeviceRow(hw, def, null, null, resolved);
    }

    [Fact]
    public void An_update_stays_counted_when_an_older_version_is_picked()
    {
        var row = Nv("32.0.15.6636", "580.88", "566.36");   // installed = 566.36, newest offered = 580.88
        Assert.True(row.NewestIsUpdate);
        row.ShowOlderVersions = true;
        row.SelectedVersion = row.Versions.Last();           // user picks the installed (older) build
        Assert.Equal("current", row.StatusKind);             // the dropdown status follows the pick...
        Assert.True(row.NewestIsUpdate);                     // ...the tile's count does not
    }

    [Fact]
    public void Up_to_date_tool_installers_and_unknown_installs_are_not_updates()
    {
        Assert.False(Nv("32.0.15.6636", "566.36").NewestIsUpdate);                    // already current

        var dsa = new DeviceRow(new ScannedDevice { Name = "Intel Graphics", PnpClass = "Display", InstalledVersion = "31.0.101.5000" },
            new Definition { Label = "Intel DSA" }, null, null,
            new[] { new VersionEntry { Version = "Latest", Comparable = false, Url = "https://intel/dsa" } });
        Assert.False(dsa.NewestIsUpdate);                                              // can't honestly tell

        Assert.False(Nv("", "580.88").NewestIsUpdate);                                 // installed version unknown
        var pageOnly = new DeviceRow(new ScannedDevice { Name = "Board", PnpClass = "System" }, null,
            new VendorFallback { Name = "Acer", Domain = "acer.com" });
        Assert.False(pageOnly.NewestIsUpdate);                                         // support page only
        Assert.True(pageOnly.HasVerifiedSource);
    }

    // ---- Health & Repair ----

    [Fact]
    public void Health_tiles_report_real_readings_and_flag_problems()
    {
        var h = new HealthReport
        {
            Memory = new MemoryInfo(16 * GB, 2, 2, new List<MemoryModule>()),
            Battery = new BatteryHealth(34, 50000, 33000, 80),
        };
        h.Disks.Add(new DiskHealth("WD Blue", "SSD", 250 * GB, "SATA", "Healthy", null, null, null));
        h.Volumes.Add(new VolumeInfo("C:", "", 232 * GB, 56 * GB));     // 76% used
        h.Volumes.Add(new VolumeInfo("D:", "", 100 * GB, 90 * GB));     // 10% used

        var t = SummaryTiles.ForHealth(h, null);
        Assert.Equal(new[] { "Healthy", "76%", "34%", "16 GB" }, t.Select(x => x.Value));
        Assert.Equal("storage (1 drive)", t[0].Label);
        Assert.Equal("of C: used", t[1].Label);                          // the FULLEST drive
        Assert.Equal(TileTone.Warning, t[2].Tone);                       // 25%+ battery wear, as the summary line
        Assert.Equal("memory, 2 of 2 slots used", t[3].Label);
    }

    [Fact]
    public void Unknown_health_readings_are_left_out_never_guessed()
    {
        var h = new HealthReport();                                      // desktop: no battery, nothing reported
        h.Disks.Add(new DiskHealth("Disk", "HDD", 1000 * GB, "SATA", "", null, null, null));   // condition not reported
        Assert.Empty(SummaryTiles.ForHealth(h, null));
        Assert.Empty(SummaryTiles.ForHealth(null, null));
    }

    [Fact]
    public void A_failing_disk_and_a_nearly_full_drive_are_warnings()
    {
        var h = new HealthReport();
        h.Disks.Add(new DiskHealth("Old HDD", "HDD", 500 * GB, "SATA", "Warning", null, null, null));
        h.Disks.Add(new DiskHealth("SSD", "SSD", 500 * GB, "NVMe", "Healthy", null, null, null));
        h.Volumes.Add(new VolumeInfo("C:", "", 100 * GB, 5 * GB));
        var t = SummaryTiles.ForHealth(h, null);
        Assert.Equal(("1 problem", "storage (2 drives)", TileTone.Warning), (t[0].Value, t[0].Label, t[0].Tone));
        Assert.Equal(("95%", TileTone.Warning), (t[1].Value, t[1].Tone));
    }

    [Theory]
    [InlineData(16L * GB, "16 GB")]
    [InlineData(17045651456L, "16 GB")]       // 15.9 GiB reported -> what the user bought
    [InlineData(2L * GB, "2 GB")]
    [InlineData(3221225472L + 536870912L, "3.5 GB")]
    public void Memory_is_shown_like_Windows_shows_it(long bytes, string expected)
        => Assert.Equal(expected, SummaryTiles.Gb(bytes));

    // ---- Startup & Tasks ----

    [Fact]
    public void Startup_tiles_follow_the_microsoft_tasks_filter()
    {
        var items = new List<StartupItem>
        {
            new("Dolby", "dolby.exe", "HKLM"),
            new("FDM", "fdm.exe", "HKCU") { Enabled = false },
        };
        var tasks = new List<ScheduledTaskItem>
        {
            new(@"\Vendor\Update", "Update", true, ""),
            new(@"\Vendor\Telemetry", "Telemetry", false, ""),
            new(@"\Microsoft\Windows\Defrag\ScheduledDefrag", "ScheduledDefrag", true, ""),
        };
        var hidden = SummaryTiles.ForStartup(items, tasks, includeMicrosoftTasks: false);
        Assert.Equal(new[] { "2", "1", "2", "1" }, hidden.Select(x => x.Value));
        Assert.Equal(new[] { "startup programs", "start with Windows", "scheduled tasks", "task turned off" }, hidden.Select(x => x.Label));
        Assert.Equal("3", SummaryTiles.ForStartup(items, tasks, includeMicrosoftTasks: true)[2].Value);
        Assert.Empty(SummaryTiles.ForStartup(null, null, false));
    }
}
