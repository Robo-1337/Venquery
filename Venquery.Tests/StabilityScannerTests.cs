using System.Collections.Generic;
using System.Threading.Tasks;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Smoke tests over the real machine's event logs — the scan must complete without throwing
/// and render the expected report sections, and the group filter must actually filter.</summary>
public class StabilityScannerTests
{
    [Fact]
    public async Task Scan_completes_and_renders_all_sections()
    {
        var r = await StabilityScanner.ScanAsync(90, null, includeReliability: true);
        Assert.NotNull(r);

        var text = r.ToReportText(null, includeReliability: true, verbose: true);
        Assert.Contains("SYSTEM STABILITY & CRASH HISTORY", text);
        Assert.Contains("CRASHES & BLUE SCREENS", text);
        Assert.Contains("HARDWARE & FIRMWARE", text);
        Assert.Contains("RELIABILITY MONITOR HISTORY", text);
    }

    [Fact]
    public async Task No_groups_selected_omits_event_sections_but_keeps_header()
    {
        var r = await StabilityScanner.ScanAsync(30);
        var text = r.ToReportText(new HashSet<string>(), includeReliability: false, verbose: false);
        Assert.Contains("SYSTEM STABILITY & CRASH HISTORY", text);
        Assert.DoesNotContain("CRASHES & BLUE SCREENS", text);
        Assert.DoesNotContain("RELIABILITY MONITOR HISTORY", text);
    }

    [Fact]
    public async Task Group_filter_includes_only_requested_categories()
    {
        var r = await StabilityScanner.ScanAsync(30);
        var text = r.ToReportText(new HashSet<string> { "crash" }, includeReliability: false, verbose: false);
        Assert.Contains("CRASHES & BLUE SCREENS", text);
        Assert.DoesNotContain("STORAGE & DISK", text);
    }

    // --- crash time-correlation (pure logic over the events list) ---

    [Fact]
    public void Crash_is_correlated_with_a_hardware_event_logged_shortly_before()
    {
        var t = new System.DateTime(2026, 8, 2, 21, 45, 0);
        var r = new StabilityReport { Days = 30 };
        r.Events.Add(new StabilityEvent(t, "crash", "Unexpected stop", "Kernel-Power 41"));
        r.Events.Add(new StabilityEvent(t.AddSeconds(-45), "hardware", "CPU limited by firmware/thermal", "throttle"));
        r.Events.Add(new StabilityEvent(t.AddMinutes(-30), "hardware", "Hardware error (WHEA)", "too old"));   // outside 10-min window

        var text = r.ToReportText(null, includeReliability: false, verbose: false);
        Assert.Contains("NOTE:", text);
        Assert.Contains("nearby (45s earlier): CPU limited by firmware/thermal", text);

        // The precursor helper honours the window: only the 60s-earlier event attaches to the crash.
        var precursors = new List<StabilityEvent>(r.PrecursorsFor(r.Events[0]));
        Assert.Single(precursors);
        Assert.Equal("CPU limited by firmware/thermal", precursors[0].Category);
    }

    [Fact]
    public void No_correlation_note_when_nothing_hardware_happened_near_the_crash()
    {
        var t = new System.DateTime(2026, 8, 2, 21, 45, 0);
        var r = new StabilityReport { Days = 30 };
        r.Events.Add(new StabilityEvent(t, "crash", "Unexpected stop", "x"));
        r.Events.Add(new StabilityEvent(t.AddHours(-2), "hardware", "Hardware error (WHEA)", "long before"));
        var text = r.ToReportText(null, includeReliability: false, verbose: false);
        Assert.DoesNotContain("NOTE:", text);
        Assert.DoesNotContain("↳ nearby", text);
    }
}
