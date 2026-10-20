namespace Venquery.Services;

/// <summary>How a summary tile is drawn: neutral, something to act on (accent), or something to look at (amber).</summary>
public enum TileTone { Normal, Accent, Warning }

/// <summary>One summary tile: a real value, what it counts, and the key of its icon (an App.xaml Ico* geometry).</summary>
public sealed record SummaryTile(string Value, string Label, TileTone Tone = TileTone.Normal, string Icon = "");

/// <summary>Builds the summary tiles shown at the top of Devices &amp; Drivers, Health &amp; Repair and Startup &amp;
/// Tasks. WPF-free and pure so it is unit-tested. The rule that keeps the tiles honest: every value comes straight
/// from data Venquery actually read, and when something is unknown (not scanned yet, not reported by Windows, no
/// battery) that tile is LEFT OUT rather than shown with a guess or a placeholder.</summary>
public static class SummaryTiles
{
    /// <summary>Devices &amp; Drivers. <paramref name="updates"/> must count devices whose NEWEST offered,
    /// comparable version is newer than the installed one (see DeviceRow.NewestIsUpdate), so picking an older
    /// version in the dropdown doesn't change it and tool installers are never counted as updates. Empty until
    /// the scan has finished.</summary>
    public static IReadOnlyList<SummaryTile> ForDrivers(bool scanned, int devices, int updates, int verified,
        StabilityReport? stability)
    {
        var tiles = new List<SummaryTile>();
        if (!scanned) return tiles;
        tiles.Add(new SummaryTile(updates.ToString(), updates == 1 ? "driver update available" : "driver updates available",
            updates > 0 ? TileTone.Accent : TileTone.Normal, "IcoUpdate"));
        tiles.Add(new SummaryTile(devices.ToString(), devices == 1 ? "device listed" : "devices listed", Icon: "IcoDrivers"));
        tiles.Add(new SummaryTile(verified.ToString(), "with an official source", Icon: "IcoBackupAll"));
        var stab = Stability(stability);
        if (stab is not null) tiles.Add(stab);
        return tiles;
    }

    /// <summary>The stability tile: serious problems only (crashes, freezes, hardware and disk errors), labelled
    /// as "problems", not "crashes", because that is what it counts. Null until stability has been read.</summary>
    public static SummaryTile? Stability(StabilityReport? stability)
    {
        if (stability is null) return null;
        var n = stability.SeriousCount;
        return new SummaryTile(n.ToString(),
            (n == 1 ? "stability problem" : "stability problems") + $", last {stability.Days} days",
            n > 0 ? TileTone.Warning : TileTone.Normal, "IcoHealth");
    }

    /// <summary>Health &amp; Repair overview: storage condition, the fullest drive, battery wear, memory and
    /// stability, each only when Windows actually reported it.</summary>
    public static IReadOnlyList<SummaryTile> ForHealth(HealthReport? health, StabilityReport? stability)
    {
        var tiles = new List<SummaryTile>();
        if (health is null) return tiles;

        // Storage: count only the disks whose condition Windows reported; with none reported, say nothing.
        var reported = health.Disks.Where(d => !string.IsNullOrWhiteSpace(d.Health)).ToList();
        if (reported.Count > 0)
        {
            var bad = reported.Count(d => !d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase));
            var drives = health.Disks.Count == 1 ? "1 drive" : $"{health.Disks.Count} drives";
            tiles.Add(bad == 0
                ? new SummaryTile("Healthy", $"storage ({drives})", Icon: "IcoDisk")
                : new SummaryTile(bad == 1 ? "1 problem" : $"{bad} problems", $"storage ({drives})", TileTone.Warning, "IcoDisk"));
        }

        // The fullest drive (what most often needs attention).
        var fullest = health.Volumes.Where(v => v.TotalBytes > 0).OrderByDescending(v => v.UsedFraction).FirstOrDefault();
        if (fullest is not null)
            tiles.Add(new SummaryTile($"{Math.Round(fullest.UsedFraction * 100):0}%", $"of {fullest.Drive} used",
                fullest.Low ? TileTone.Warning : TileTone.Normal, "IcoFiles"));

        // Battery wear only when the PC has a battery and Windows reports its capacities (25%+ matches the summary line).
        if (health.Battery?.WearPercent is int wear)
            tiles.Add(new SummaryTile($"{wear}%", "battery wear", wear >= 25 ? TileTone.Warning : TileTone.Normal, "IcoBattery"));

        if (health.Memory is { TotalBytes: > 0 } mem)
            tiles.Add(new SummaryTile(Gb(mem.TotalBytes),
                mem.SlotsTotal > 0 ? $"memory, {mem.SlotsUsed} of {mem.SlotsTotal} slots used" : "memory", Icon: "IcoMemory"));

        var stab = Stability(stability);
        if (stab is not null) tiles.Add(stab);
        return tiles;
    }

    /// <summary>Startup &amp; Tasks: what starts with Windows and the scheduled tasks (Windows' own tasks counted
    /// only when the user chose to show them). Empty until the lists have been read.</summary>
    public static IReadOnlyList<SummaryTile> ForStartup(IReadOnlyList<StartupItem>? items,
        IReadOnlyList<ScheduledTaskItem>? tasks, bool includeMicrosoftTasks)
    {
        var tiles = new List<SummaryTile>();
        if (items is not null)
        {
            var on = items.Count(i => i.Enabled);
            tiles.Add(new SummaryTile(items.Count.ToString(), items.Count == 1 ? "startup program" : "startup programs", Icon: "IcoList"));
            tiles.Add(new SummaryTile(on.ToString(), "start with Windows", Icon: "IcoClock"));
        }
        if (tasks is not null)
        {
            var shown = tasks.Where(t => includeMicrosoftTasks || !t.IsMicrosoft).ToList();
            var off = shown.Count(t => !t.Enabled);
            tiles.Add(new SummaryTile(shown.Count.ToString(), shown.Count == 1 ? "scheduled task" : "scheduled tasks", Icon: "IcoClock"));
            tiles.Add(new SummaryTile(off.ToString(), off == 1 ? "task turned off" : "tasks turned off", Icon: "IcoList"));
        }
        return tiles;
    }

    /// <summary>Installed memory as Windows shows it: whole GB from 4 GB up (15.9 GB of usable reporting rounds to
    /// the module total the user bought), one decimal below.</summary>
    public static string Gb(long bytes)
    {
        var gb = bytes / 1073741824.0;
        return gb >= 4 ? $"{Math.Round(gb):0} GB" : $"{gb:0.#} GB";
    }
}
