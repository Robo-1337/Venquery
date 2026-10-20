using System.Collections.Generic;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the pure formatting/summary logic of the health report (the WMI collection itself
/// is environment-dependent and verified in the running app).</summary>
public class HealthReportTests
{
    private const long GB = 1024L * 1024 * 1024;

    [Fact]
    public void Volume_flags_low_space_under_ten_percent()
    {
        Assert.True(new VolumeInfo("C:", "", 100 * GB, 9 * GB).Low);
        Assert.False(new VolumeInfo("C:", "", 100 * GB, 11 * GB).Low);
    }

    [Fact]
    public void Volume_used_fraction_is_correct()
        => Assert.Equal(0.75, new VolumeInfo("D:", "", 100 * GB, 25 * GB).UsedFraction, 3);

    [Fact]
    public void Summary_reads_healthy_when_nothing_is_wrong()
    {
        var h = new HealthReport();
        h.Disks.Add(new DiskHealth("SSD", "SSD", 256 * GB, "NVMe", "Healthy", null, null, null));
        h.Volumes.Add(new VolumeInfo("C:", "", 256 * GB, 128 * GB));   // 50% used — not low
        h.Battery = new BatteryHealth(5, 50000, 47500, 100);           // barely worn
        var s = h.SummaryLine();
        Assert.Contains("storage healthy", s);
        Assert.DoesNotContain("problem", s);
        Assert.DoesNotContain("low on space", s);
        Assert.DoesNotContain("worn", s);
    }

    [Fact]
    public void Summary_flags_a_failing_disk_low_space_and_worn_battery()
    {
        var h = new HealthReport();
        h.Disks.Add(new DiskHealth("Old HDD", "HDD", 500 * GB, "SATA", "Warning", null, null, null));
        h.Volumes.Add(new VolumeInfo("C:", "", 100 * GB, 5 * GB));   // 5% free -> low
        h.Battery = new BatteryHealth(40, 50000, 30000, 88);
        var s = h.SummaryLine();
        Assert.Contains("1 disk(s) reporting a problem", s);
        Assert.Contains("C: low on space", s);
        Assert.Contains("battery 40% worn", s);
    }

    [Fact]
    public void Report_text_covers_each_section_and_marks_missing_wear()
    {
        var h = new HealthReport
        {
            LastBoot = new System.DateTime(2026, 8, 1, 9, 0, 0),
            Memory = new MemoryInfo(16 * GB, 2, 2, new List<MemoryModule>
            {
                new(8 * GB, 2400, "DIMM0"), new(8 * GB, 2400, "DIMM1"),
            }),
        };
        h.Disks.Add(new DiskHealth("WD Blue", "SSD", 250 * GB, "SATA", "Healthy", null, null, null));
        h.Volumes.Add(new VolumeInfo("C:", "OS", 231 * GB, 160 * GB));
        h.Battery = new BatteryHealth(33, 48944, 32634, 100);

        var text = h.ToReportText();
        Assert.Contains("SYSTEM HEALTH", text);
        Assert.Contains("WD Blue", text);
        Assert.Contains("health: Healthy", text);
        Assert.Contains("wear/temperature: not reported", text);   // best-effort field absent
        Assert.Contains("C:", text);
        Assert.Contains("16 GB total", text);
        Assert.Contains("33% worn", text);
    }

    [Fact]
    public void Report_includes_ram_part_numbers_and_battery_electricals()
    {
        var h = new HealthReport
        {
            Memory = new MemoryInfo(8 * GB, 1, 2, new List<MemoryModule>
            {
                new(8 * GB, 2400, "DIMM0", "Crucial", "CT8G4SFS832A.M8FR"),
            }),
            Battery = new BatteryHealth(33, 48944, 32634, 100, 16664, 0, 0, "DELL ABC123", "Li-ion"),
        };
        var text = h.ToReportText();
        Assert.Contains("CT8G4SFS832A.M8FR", text);   // RAM part number = part identity
        Assert.Contains("Crucial", text);
        Assert.Contains("Li-ion", text);              // battery chemistry
        Assert.Contains("16.66 V", text);             // battery voltage 16664 mV
    }

    [Fact]
    public void Report_omits_disk_serial_for_privacy()
    {
        var h = new HealthReport();
        h.Disks.Add(new DiskHealth("WD Blue", "SSD", 250 * GB, "SATA", "Healthy", null, null, null, "WD-SECRET-SERIAL-123"));
        Assert.DoesNotContain("WD-SECRET-SERIAL-123", h.ToReportText());
    }
}
