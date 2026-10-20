using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace Venquery.Services;

public sealed record StabilityEvent(DateTime Time, string Group, string Category, string Detail);

public sealed record ReliabilityRecord(DateTime Time, string Kind, string Product, string Message);

/// <summary>
/// A read-only snapshot of recent Windows stability signals, pulled entirely from Windows' own
/// event logs, WMI, and on-disk crash dumps. Nothing is modified and nothing leaves the machine.
/// Covers: unexpected stops / blue screens, freezes &amp; hangs (GPU TDR, storage I/O timeouts),
/// hardware &amp; firmware faults (WHEA, ACPI/BIOS/EC, HAL, CPU-firmware throttling), disk/filesystem
/// errors, driver/device/service failures, and application crashes, plus the Reliability Monitor's
/// plain-English "what stopped working" history (opt-in, verbose).
/// </summary>
public sealed class StabilityReport
{
    public int Days { get; init; } = 30;
    public List<StabilityEvent> Events { get; } = new();
    public List<string> DumpFiles { get; } = new();
    public List<ReliabilityRecord> Reliability { get; } = new();
    public DateTime? LastBoot { get; set; }

    // Report groups, in output order, with their headings.
    public static readonly (string Key, string Heading)[] GroupOrder =
    {
        ("crash",    "CRASHES & BLUE SCREENS"),
        ("freeze",   "FREEZES & HANGS (brief stalls / recovered timeouts)"),
        ("hardware", "HARDWARE & FIRMWARE  (WHEA / ACPI / BIOS / EC / thermal)"),
        ("storage",  "STORAGE & DISK"),
        ("device",   "DRIVERS, DEVICES & SERVICES"),
        ("app",      "APPLICATION CRASHES & HANGS"),
    };

    // Groups that indicate genuine *system* instability, drive the title-bar warning pill. App and
    // device faults are common/benign, so they stay in the export but don't trip the pill.
    private static readonly string[] Serious = { "crash", "freeze", "hardware", "storage" };

    public int CountIn(params string[] groups) => Events.Count(e => groups.Contains(e.Group));

    /// <summary>True if any *serious* system-stability event was logged (drives the warning pill).</summary>
    public bool HasIssues => CountIn(Serious) > 0;

    /// <summary>How many *serious* events (crash, freeze, hardware, storage) were logged; the summary tile's number.</summary>
    public int SeriousCount => CountIn(Serious);

    public string SummaryLine()
    {
        if (!HasIssues) return $"No crashes, freezes, or hardware faults logged in the last {Days} days";
        var parts = new List<string>();
        void Add(int n, string label) { if (n > 0) parts.Add($"{n} {label}{(n == 1 ? "" : "s")}"); }
        Add(CountIn("crash"), "unexpected stop");
        Add(CountIn("freeze"), "freeze/timeout");
        Add(CountIn("hardware"), "hardware/firmware error");
        Add(CountIn("storage"), "disk error");
        return parts.Count > 0 ? string.Join(" · ", parts) : $"{CountIn(Serious)} stability event(s)";
    }

    /// <summary>Render the crash/stability section for an exported report. Only the requested
    /// <paramref name="groups"/> are included; <paramref name="verbose"/> keeps full event text and
    /// <paramref name="includeReliability"/> appends the Reliability Monitor history.</summary>
    public string ToReportText(ISet<string>? groups = null, bool includeReliability = false, bool verbose = false)
    {
        groups ??= new HashSet<string>(GroupOrder.Select(g => g.Key));
        var sb = new StringBuilder();
        sb.AppendLine($"SYSTEM STABILITY & CRASH HISTORY, last {Days} days");
        if (LastBoot is { } lb)
            sb.AppendLine($"Last boot: {lb:yyyy-MM-dd HH:mm}  ·  up {FormatUptime(DateTime.Now - lb)}");
        sb.AppendLine("(Read-only from Windows' own logs. Review before sharing, event text can contain");
        sb.AppendLine(" file paths, device names, or your username.)");
        sb.AppendLine(new string('=', 66));
        sb.AppendLine("SUMMARY: " + SummaryLine());
        var correlated = Events.Count(e => (e.Group is "crash" or "freeze") && PrecursorsFor(e).Any());
        if (correlated > 0)
            sb.AppendLine($"NOTE: {correlated} crash/freeze event(s) had a hardware or thermal event logged within " +
                          $"{CorrelationWindowMinutes} min before them, see the \"↳ nearby\" lines. (Windows keeps no live " +
                          "temperature/voltage readings, so this correlates the events it does record, not sensor values.)");
        sb.AppendLine();

        foreach (var (key, heading) in GroupOrder)
        {
            if (!groups.Contains(key)) continue;
            var evs = Events.Where(e => e.Group == key).OrderByDescending(e => e.Time).ToList();
            sb.AppendLine(new string('-', 66));
            sb.AppendLine($"{heading}  ({evs.Count})");
            if (evs.Count == 0)
                sb.AppendLine("  (none logged) ✓");
            else
                foreach (var e in evs)
                {
                    sb.AppendLine($"  {e.Time:yyyy-MM-dd HH:mm}  {e.Category}, {(verbose ? e.Detail : Truncate(e.Detail, 150))}");
                    // For crashes/freezes, surface hardware/firmware/thermal events logged just before, 
                    // the honest, sensor-free answer to "what was going wrong when it went down."
                    if (key is "crash" or "freeze")
                        foreach (var p in PrecursorsFor(e))
                            sb.AppendLine($"      ↳ nearby ({FormatGap(e.Time - p.Time)} earlier): {p.Category}");
                }

            if (key == "crash" && DumpFiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  Crash dump files on disk (open in WinDbg / a debugger to find the faulting driver):");
                foreach (var d in DumpFiles) sb.AppendLine($"    {d}");
            }
            sb.AppendLine();
        }

        if (includeReliability)
        {
            var recs = Reliability.OrderByDescending(r => r.Time).ToList();
            sb.AppendLine(new string('-', 66));
            sb.AppendLine($"RELIABILITY MONITOR HISTORY  ({recs.Count})");
            sb.AppendLine("  Windows' own plain-English record of what stopped working / responding, and when.");
            if (recs.Count == 0)
                sb.AppendLine("  (none available)");
            else
                foreach (var r in recs)
                {
                    sb.AppendLine($"  {r.Time:yyyy-MM-dd HH:mm}  {r.Kind}{(string.IsNullOrEmpty(r.Product) ? "" : $"  ·  {r.Product}")}");
                    if (!string.IsNullOrWhiteSpace(r.Message))
                        sb.AppendLine($"      {(verbose ? r.Message : Truncate(r.Message, 160))}");
                }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // How far before a crash a hardware/thermal event still counts as "related."
    private const int CorrelationWindowMinutes = 10;

    /// <summary>Hardware/firmware events (WHEA faults, thermal or firmware throttling, ACPI/BIOS/EC,
    /// HAL) logged in the few minutes BEFORE a crash or freeze. Windows records no live temperatures or
    /// voltages at the moment of a crash, so this correlates the fault events it does keep, the closest
    /// honest answer to "what was going wrong when it went down."</summary>
    public IEnumerable<StabilityEvent> PrecursorsFor(StabilityEvent ev)
        => Events.Where(h => h.Group == "hardware"
                          && h.Time <= ev.Time
                          && (ev.Time - h.Time).TotalMinutes <= CorrelationWindowMinutes)
                 .OrderBy(h => h.Time);

    private static string FormatGap(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds}s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m"
        : $"{(int)t.TotalHours}h {t.Minutes}m";

    private static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{(int)t.TotalMinutes}m";

    internal static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

public static class StabilityScanner
{
    private const int MaxEventsPerLog = 400; // keep the report bounded on a badly failing machine
    private const int MaxReliability = 250;

    // One row per Windows event source we care about. Providers are matched exactly (case-insensitive);
    // an empty Ids array means "any event from these providers at Level ≤ MaxLevel" (Critical/Error).
    private sealed record Src(string Log, string[] Providers, int[] Ids, string Group, string Category, int MaxLevel = 2);

    private static readonly Src[] Sources =
    {
        // ---- crashes / unexpected stops ----
        new("System", new[]{"Microsoft-Windows-Kernel-Power"}, new[]{41}, "crash", "Unexpected stop"),
        new("System", new[]{"EventLog"}, new[]{6008}, "crash", "Unexpected shutdown"),
        new("System", new[]{"Microsoft-Windows-WER-SystemErrorReporting"}, new[]{1001}, "crash", "Blue screen (bugcheck)"),
        // ---- freezes / hangs (brief, recovered) ----
        new("System", new[]{"Display"}, new[]{4101, 4098}, "freeze", "GPU driver reset (TDR)"),
        new("System", new[]{"storahci","stornvme","storport","iaStorA","iaStorAC","iaStorAVC","vhdmp","nvme","amdsata","amdxata"},
                       new[]{129}, "freeze", "Storage I/O timeout (device reset)"),
        // ---- hardware & firmware (WHEA / ACPI / BIOS / EC / thermal) ----
        new("System", new[]{"Microsoft-Windows-WHEA-Logger"}, new[]{1, 17, 18, 19, 46, 47}, "hardware", "Hardware error (WHEA)"),
        new("System", new[]{"Microsoft-Windows-Kernel-Acpi","ACPI"}, Array.Empty<int>(), "hardware", "ACPI / BIOS / EC firmware error"),
        new("System", new[]{"Microsoft-Windows-HAL"}, new[]{12}, "hardware", "Firmware error (HAL)"),
        new("System", new[]{"Microsoft-Windows-Kernel-Processor-Power"}, new[]{35, 37}, "hardware", "CPU limited by firmware/thermal"),
        // ---- storage & filesystem ----
        new("System", new[]{"disk"}, new[]{7, 11, 153}, "storage", "Disk error"),
        // NTFS: level-filtered to Error/Critical so the extremely common Info-level "…is healthy,
        // no action needed" events (Event 98) don't get miscounted as errors, only real corruption.
        new("System", new[]{"Microsoft-Windows-Ntfs","Ntfs"}, Array.Empty<int>(), "storage", "Filesystem error (NTFS)"),
        // ---- drivers / devices / services ----
        new("System", new[]{"Microsoft-Windows-Kernel-PnP"}, new[]{219, 411, 441, 442}, "device", "Driver/device failure"),
        // SCM: crashes/timeouts/start-failures. Event 7026 ("boot driver did not load: dam / EhStorClass")
        // is deliberately excluded, it's benign on virtually every Windows install.
        new("System", new[]{"Service Control Manager"}, new[]{7000, 7001, 7011, 7022, 7023, 7031, 7034}, "device", "Service/driver failure"),
        new("System", new[]{"Microsoft-Windows-USB-USBHUB3","Microsoft-Windows-USB-USBPORT","Microsoft-Windows-USB-USBXHCI"},
                       Array.Empty<int>(), "device", "USB device error"),
        // ---- application crashes / hangs ----
        new("Application", new[]{"Application Error"}, new[]{1000}, "app", "Application crash"),
        new("Application", new[]{"Application Hang"}, new[]{1002}, "app", "Application not responding (hang)"),
        new("Application", new[]{".NET Runtime"}, new[]{1026}, "app", "Application crash (.NET)"),
    };

    public static Task<StabilityReport> ScanAsync(int days = 30, IProgress<string>? log = null, bool includeReliability = false)
        => Task.Run(() => Scan(days, log, includeReliability));

    private static StabilityReport Scan(int days, IProgress<string>? log, bool includeReliability)
    {
        var report = new StabilityReport { Days = days };
        var ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;

        foreach (var logName in Sources.Select(s => s.Log).Distinct())
        {
            var srcs = Sources.Where(s => s.Log == logName).ToArray();
            try
            {
                log?.Report($"> reading {logName} event log · stability ({days}d)");
                var query = new EventLogQuery(logName, PathType.LogName, BuildXPath(srcs, ms)) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                var read = 0;
                for (var rec = reader.ReadEvent(); rec != null && read < MaxEventsPerLog; rec = reader.ReadEvent())
                {
                    using (rec)
                    {
                        read++;
                        var src = Match(srcs, rec.ProviderName ?? "", rec.Id);
                        if (src is null) continue;
                        string desc = "";
                        try { desc = rec.FormatDescription() ?? ""; } catch { /* provider metadata missing */ }
                        report.Events.Add(new StabilityEvent(
                            rec.TimeCreated ?? DateTime.MinValue, src.Group, src.Category, BuildDetail(rec.Id, desc)));
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Report($"  {logName} stability scan unavailable ({ex.GetType().Name})");
            }
        }

        CollectDumps(report);
        CollectLastBoot(report);
        if (includeReliability) CollectReliability(report, days, log);

        log?.Report($"> stability · {report.SummaryLine()}");
        return report;
    }

    private static string BuildXPath(IEnumerable<Src> srcs, long ms)
    {
        var clauses = new List<string>();
        foreach (var s in srcs)
        {
            var provs = string.Join(" or ", s.Providers.Select(p => $"Provider[@Name='{p}']"));
            clauses.Add(s.Ids.Length > 0
                ? $"(({provs}) and ({string.Join(" or ", s.Ids.Select(i => $"EventID={i}"))}))"
                : $"(({provs}) and Level<={s.MaxLevel})");
        }
        return $"*[System[TimeCreated[timediff(@SystemTime) <= {ms}] and ({string.Join(" or ", clauses)})]]";
    }

    private static Src? Match(IEnumerable<Src> srcs, string provider, int id)
    {
        foreach (var s in srcs)
            if (s.Providers.Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase))
                && (s.Ids.Length == 0 || s.Ids.Contains(id)))
                return s;
        return null;
    }

    private static string BuildDetail(int id, string desc)
    {
        desc = Regex.Replace(desc ?? "", @"\s+", " ").Trim();
        if (id == 1001)
        {
            var code = Regex.Match(desc, "0x[0-9A-Fa-f]{8}").Value;
            if (!string.IsNullOrEmpty(code)) return $"Stop code {code}. {desc}";
        }
        if (id is 4101 or 4098)
        {
            var drv = Regex.Match(desc, @"driver (\w+) stopped").Groups[1].Value;
            if (!string.IsNullOrEmpty(drv)) return $"Display driver {drv} reset & recovered ({DriverVendor(drv)}). {desc}";
        }
        return desc.Length == 0 ? "(no description text available for this event)" : desc;
    }

    private static void CollectDumps(StabilityReport report)
    {
        // Best-effort (folders are usually admin-only; ignore denials).
        foreach (var dir in new[] { @"C:\Windows\Minidump", @"C:\Windows\LiveKernelReports" })
        {
            try
            {
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.dmp"))
                        report.DumpFiles.Add($"{f}  ({File.GetLastWriteTime(f):yyyy-MM-dd})");
            }
            catch { /* access denied without admin */ }
        }
        try
        {
            const string mem = @"C:\Windows\MEMORY.DMP";
            if (File.Exists(mem)) report.DumpFiles.Add($"{mem}  ({File.GetLastWriteTime(mem):yyyy-MM-dd})");
        }
        catch { }
    }

    private static void CollectLastBoot(StabilityReport report)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
            {
                var v = o["LastBootUpTime"]?.ToString();
                if (!string.IsNullOrEmpty(v)) report.LastBoot = ManagementDateTimeConverter.ToDateTime(v);
                o.Dispose();
                break;
            }
        }
        catch { /* WMI unavailable */ }
    }

    private static void CollectReliability(StabilityReport report, int days, IProgress<string>? log)
    {
        try
        {
            log?.Report("> reading Reliability Monitor history (WMI)");
            var cutoff = DateTime.Now.AddDays(-days);
            using var s = new ManagementObjectSearcher(
                "SELECT TimeGenerated, SourceName, ProductName, Message, EventIdentifier FROM Win32_ReliabilityRecords");
            var scanned = 0;
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    if (++scanned > 4000) break;   // hard backstop against a huge RAC store
                    DateTime t;
                    try { t = ManagementDateTimeConverter.ToDateTime(o["TimeGenerated"]?.ToString()); }
                    catch { continue; }
                    if (t < cutoff) continue;

                    var source = o["SourceName"]?.ToString() ?? "";
                    var id = 0; try { id = Convert.ToInt32(o["EventIdentifier"] ?? 0); } catch { }
                    var msg = Regex.Replace(o["Message"]?.ToString() ?? "", @"\s+", " ").Trim();
                    // Reliability Monitor mixes in *informational* successes (Windows Update / MSI
                    // installs). This is a "what went wrong" report, so keep only the failures.
                    if (!IsFailure(msg)) continue;
                    report.Reliability.Add(new ReliabilityRecord(
                        t, FriendlyKind(source, id), o["ProductName"]?.ToString() ?? "", msg));
                    if (report.Reliability.Count >= MaxReliability) break;
                }
            }
        }
        catch (Exception ex)
        {
            log?.Report($"  reliability history unavailable ({ex.GetType().Name})");
        }
    }

    /// <summary>Keep only reliability records that describe a *failure*, dropping the informational
    /// "installed / updated successfully" chatter the RAC also records.</summary>
    private static bool IsFailure(string msg)
    {
        var m = msg.ToLowerInvariant();
        return !(m.Contains("installation successful") || m.Contains("successfully installed")
            || m.Contains("installed the product") || m.Contains("reconfigured the product")
            || m.Contains("removed the product") || m.Contains("configured the product")
            || m.Contains("installed the update") || m.Contains("update was successful"));
    }

    private static string FriendlyKind(string source, int id) => (source, id) switch
    {
        ("Application Error", _) => "Application crash",
        ("Application Hang", _) => "Application hang (not responding)",
        (_, 1001) => "Blue screen / bugcheck",
        ("Microsoft-Windows-WindowsUpdateClient", _) => "Windows Update",
        ("EventLog", 6008) => "Unexpected shutdown",
        ("Microsoft-Windows-Kernel-Power", 41) => "Unexpected shutdown",
        ("Windows Error Reporting", _) => "Windows problem report",
        _ => string.IsNullOrWhiteSpace(source) ? "Reliability event" : source,
    };

    private static string DriverVendor(string drv)
    {
        var d = drv.ToLowerInvariant();
        if (d.StartsWith("nvlddmkm", StringComparison.Ordinal)) return "NVIDIA";
        if (d.StartsWith("atikmdag", StringComparison.Ordinal) || d.StartsWith("amdkmdag", StringComparison.Ordinal) || d.StartsWith("amdwddm", StringComparison.Ordinal)) return "AMD";
        if (d.StartsWith("igd", StringComparison.Ordinal) || d.StartsWith("igfx", StringComparison.Ordinal)) return "Intel";
        return "unknown vendor";
    }
}
