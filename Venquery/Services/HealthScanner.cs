using System.Management;
using System.Text;

namespace Venquery.Services;

public sealed record DiskHealth(
    string Model, string Kind, long SizeBytes, string Bus, string Health,
    int? WearPercent, int? TemperatureC, long? PowerOnHours, string Serial = "");

public sealed record VolumeInfo(string Drive, string Label, long TotalBytes, long FreeBytes)
{
    public double UsedFraction => TotalBytes > 0 ? (double)(TotalBytes - FreeBytes) / TotalBytes : 0;
    public bool Low => TotalBytes > 0 && FreeBytes < 0.10 * TotalBytes;   // < 10% free is worth flagging
}

public sealed record MemoryModule(long SizeBytes, int SpeedMhz, string Slot, string Manufacturer = "", string PartNumber = "");

public sealed record MemoryInfo(long TotalBytes, int SlotsUsed, int SlotsTotal, IReadOnlyList<MemoryModule> Modules);

public sealed record BatteryHealth(
    int? WearPercent, long? DesignedCapacity, long? FullChargedCapacity, int? ChargePercent,
    int? VoltageMv = null, int? ChargeRateMw = null, int? DischargeRateMw = null,
    string Model = "", string Chemistry = "");

/// <summary>A read-only snapshot of hardware "health" signals, storage condition and wear, free
/// space, memory layout, battery wear, and uptime, pulled entirely from WMI/CIM. Nothing is
/// modified and nothing leaves the machine. The richer per-disk counters (wear / temperature /
/// power-on hours) are best-effort: some controllers only expose them to an administrator, so they
/// are shown when available and reported as "not reported" otherwise rather than guessed.</summary>
public sealed class HealthReport
{
    public List<DiskHealth> Disks { get; } = new();
    public List<VolumeInfo> Volumes { get; } = new();
    public MemoryInfo? Memory { get; set; }
    public BatteryHealth? Battery { get; set; }
    public DateTime? LastBoot { get; set; }
    public DateTime? InstallDate { get; set; }

    public string SummaryLine()
    {
        var parts = new List<string>();
        if (Disks.Count > 0)
        {
            var bad = Disks.Count(d => !d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                                       && !string.IsNullOrEmpty(d.Health));
            parts.Add(bad == 0 ? "storage healthy" : $"{bad} disk(s) reporting a problem");
        }
        var low = Volumes.FirstOrDefault(v => v.Low);
        if (low is not null) parts.Add($"{low.Drive} low on space");
        if (Battery is { WearPercent: >= 25 }) parts.Add($"battery {Battery.WearPercent}% worn");
        return parts.Count > 0 ? string.Join(" · ", parts) : "no health concerns detected";
    }

    public string ToReportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("SYSTEM HEALTH");
        if (LastBoot is { } lb)
            sb.AppendLine($"  Last boot   : {lb:yyyy-MM-dd HH:mm}  ·  up {FormatUptime(DateTime.Now - lb)}");
        if (InstallDate is { } id)
            sb.AppendLine($"  OS installed: {id:yyyy-MM-dd}");
        sb.AppendLine($"  Summary     : {SummaryLine()}");
        sb.AppendLine();

        sb.AppendLine("  Storage");
        if (Disks.Count == 0) sb.AppendLine("    (no physical disks reported)");
        foreach (var d in Disks)
        {
            sb.AppendLine($"    {d.Model}  ·  {d.Kind}  ·  {FormatBytes(d.SizeBytes)}  ·  {d.Bus}  ·  health: {d.Health}");
            var extra = new List<string>();
            if (d.WearPercent is { } w) extra.Add($"{w}% life used");
            if (d.TemperatureC is { } t) extra.Add($"{t} °C");
            if (d.PowerOnHours is { } h) extra.Add($"{h:n0} power-on hours");
            sb.AppendLine(extra.Count > 0 ? "      " + string.Join("  ·  ", extra) : "      wear/temperature: not reported");
        }
        sb.AppendLine();

        sb.AppendLine("  Volumes");
        foreach (var v in Volumes)
            sb.AppendLine($"    {v.Drive} {(string.IsNullOrEmpty(v.Label) ? "" : $"({v.Label}) ")}" +
                          $"{FormatBytes(v.FreeBytes)} free of {FormatBytes(v.TotalBytes)} ({v.UsedFraction * 100:0}% used)" +
                          (v.Low ? "  ← low" : ""));
        sb.AppendLine();

        if (Memory is { } m)
        {
            sb.AppendLine($"  Memory: {FormatBytes(m.TotalBytes)} total · {m.SlotsUsed} of {m.SlotsTotal} slot(s) used");
            foreach (var mod in m.Modules)
            {
                var partId = string.Join(" ", new[] { mod.Manufacturer, mod.PartNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
                sb.AppendLine($"    {mod.Slot}: {FormatBytes(mod.SizeBytes)}{(mod.SpeedMhz > 0 ? $" @ {mod.SpeedMhz} MHz" : "")}" +
                              (partId.Length > 0 ? $"  ·  {partId}" : ""));
            }
            sb.AppendLine();
        }

        if (Battery is { } b)
        {
            var idbits = string.Join("  ·  ", new[] { b.Model, b.Chemistry }.Where(s => !string.IsNullOrWhiteSpace(s)));
            sb.AppendLine(idbits.Length > 0 ? $"  Battery: {idbits}" : "  Battery");
            sb.Append("    ");
            sb.Append(b.WearPercent is { } bw ? $"{bw}% worn" : "wear not reported");
            if (b.DesignedCapacity is { } dc && b.FullChargedCapacity is { } fc)
                sb.Append($" (design {dc:n0} mWh → full charge {fc:n0} mWh)");
            if (b.ChargePercent is { } cp) sb.Append($"  ·  currently {cp}%");
            sb.AppendLine();
            var elec = new List<string>();
            if (b.VoltageMv is { } v) elec.Add($"{v / 1000.0:0.00} V");
            if (b.ChargeRateMw is { } cr && cr > 0) elec.Add($"charging {cr / 1000.0:0.0} W");
            if (b.DischargeRateMw is { } dr && dr > 0) elec.Add($"discharging {dr / 1000.0:0.0} W");
            if (elec.Count > 0) sb.AppendLine("    " + string.Join("  ·  ", elec));
        }
        return sb.ToString();
    }

    private static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{(int)t.TotalMinutes}m";

    internal static string FormatBytes(long b)
    {
        if (b <= 0) return "0 B";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}

public static class HealthScanner
{
    public static Task<HealthReport> ScanAsync(IProgress<string>? log = null) => Task.Run(() => Scan(log));

    private static HealthReport Scan(IProgress<string>? log)
    {
        var r = new HealthReport();
        log?.Report("> reading system health (storage / memory / battery)");
        CollectDisks(r, log);
        CollectVolumes(r);
        CollectMemory(r);
        CollectBattery(r);
        CollectUptime(r);
        log?.Report($"> health · {r.SummaryLine()}");
        return r;
    }

    // --- storage: prefer the modern Storage provider (HealthStatus + media type), fall back to Win32 ---
    private static void CollectDisks(HealthReport r, IProgress<string>? log)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var s = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT FriendlyName, MediaType, Size, BusType, HealthStatus, SerialNumber FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var model = (o["FriendlyName"] as string ?? "Disk").Trim();
                    var kind = (Convert.ToInt32(o["MediaType"] ?? 0)) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Disk" };
                    long size = ToLong(o["Size"]);
                    var bus = BusName(Convert.ToInt32(o["BusType"] ?? 0));
                    var health = (Convert.ToInt32(o["HealthStatus"] ?? 5)) switch
                    { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
                    var (wear, temp, hours) = TryReliability(scope, o);
                    r.Disks.Add(new DiskHealth(model, kind, size, bus, health, wear, temp, hours, (o["SerialNumber"] as string ?? "").Trim()));
                }
            }
        }
        catch (Exception ex)
        {
            log?.Report($"  storage provider unavailable ({ex.GetType().Name}), using Win32_DiskDrive");
        }

        if (r.Disks.Count == 0)   // fallback: always-available, but coarser (no media type / wear)
        {
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Model, Size, InterfaceType, Status, SerialNumber FROM Win32_DiskDrive");
                foreach (ManagementObject o in s.Get())
                    using (o)
                    {
                        var status = (o["Status"] as string ?? "").Trim();
                        var health = status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "Healthy"
                            : string.IsNullOrEmpty(status) ? "Unknown" : status;   // "Pred Fail" / "Degraded"
                        r.Disks.Add(new DiskHealth(
                            (o["Model"] as string ?? "Disk").Trim(), "Disk", ToLong(o["Size"]),
                            o["InterfaceType"] as string ?? "", health, null, null, null, (o["SerialNumber"] as string ?? "").Trim()));
                    }
            }
            catch { /* WMI unavailable entirely */ }
        }
    }

    // Best-effort per-disk wear/temperature/power-on-hours. Often admin-gated, so failures are silent.
    private static (int? wear, int? temp, long? hours) TryReliability(ManagementScope scope, ManagementObject disk)
    {
        try
        {
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                $"ASSOCIATORS OF {{{disk.Path.RelativePath}}} WHERE ResultClass=MSFT_StorageReliabilityCounter"));
            foreach (ManagementObject c in s.Get())
                using (c)
                {
                    int? wear = c["Wear"] is { } w ? Convert.ToInt32(w) : null;
                    int? temp = c["Temperature"] is { } t && Convert.ToInt32(t) > 0 ? Convert.ToInt32(t) : null;
                    long? hours = c["PowerOnHours"] is { } h && Convert.ToInt64(h) > 0 ? Convert.ToInt64(h) : null;
                    return (wear, temp, hours);
                }
        }
        catch { /* not exposed without admin, or not supported by this controller */ }
        return (null, null, null);
    }

    private static void CollectVolumes(HealthReport r)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    long size = ToLong(o["Size"]);
                    if (size <= 0) continue;
                    r.Volumes.Add(new VolumeInfo(
                        o["DeviceID"] as string ?? "", o["VolumeName"] as string ?? "", size, ToLong(o["FreeSpace"])));
                }
            r.Volumes.Sort((a, b) => string.CompareOrdinal(a.Drive, b.Drive));
        }
        catch { }
    }

    private static void CollectMemory(HealthReport r)
    {
        try
        {
            long total = 0; var mods = new List<MemoryModule>();
            using (var s = new ManagementObjectSearcher(
                "SELECT Capacity, Speed, DeviceLocator, Manufacturer, PartNumber FROM Win32_PhysicalMemory"))
            {
                foreach (ManagementObject o in s.Get())
                    using (o)
                    {
                        long cap = ToLong(o["Capacity"]);
                        total += cap;
                        mods.Add(new MemoryModule(cap, o["Speed"] is { } sp ? Convert.ToInt32(sp) : 0,
                            o["DeviceLocator"] as string ?? "DIMM",
                            (o["Manufacturer"] as string ?? "").Trim(), (o["PartNumber"] as string ?? "").Trim()));
                    }
            }
            int slotsTotal = mods.Count;
            try
            {
                using var s2 = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
                foreach (ManagementObject o in s2.Get())
                    using (o) { if (o["MemoryDevices"] is { } md) slotsTotal = Math.Max(slotsTotal, Convert.ToInt32(md)); }
            }
            catch { }
            if (mods.Count > 0) r.Memory = new MemoryInfo(total, mods.Count, slotsTotal, mods);
        }
        catch { }
    }

    private static void CollectBattery(HealthReport r)
    {
        long? design = null, full = null; int? charge = null;
        int? voltage = null, chargeRate = null, dischargeRate = null;
        string model = "", chemistry = "";
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();
            // Capacities are queried by single property, BatteryStaticData throws a "generic failure"
            // on some firmware when you ask for its identity fields, but DesignedCapacity is reliable.
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DesignedCapacity FROM BatteryStaticData")))
                foreach (ManagementObject o in s.Get()) using (o) { design = ToNullableLong(o["DesignedCapacity"]); break; }
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")))
                foreach (ManagementObject o in s.Get()) using (o) { full = ToNullableLong(o["FullChargedCapacity"]); break; }
            // Live electricals: voltage (mV) and the current charge/discharge rate (mW).
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Voltage, ChargeRate, DischargeRate FROM BatteryStatus")))
                foreach (ManagementObject o in s.Get())
                    using (o)
                    {
                        voltage = ToNullableInt(o["Voltage"]);
                        chargeRate = ToNullableInt(o["ChargeRate"]);
                        dischargeRate = ToNullableInt(o["DischargeRate"]);
                        break;
                    }
        }
        catch { /* no battery / not exposed */ }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, Name, Chemistry FROM Win32_Battery");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    if (o["EstimatedChargeRemaining"] is { } c) charge = Convert.ToInt32(c);
                    model = (o["Name"] as string ?? "").Trim();
                    chemistry = ChemistryName(o["Chemistry"] is { } ch ? Convert.ToInt32(ch) : 0);
                    break;
                }
        }
        catch { }

        // Only surface a battery section if this machine actually has one.
        if (design is null && full is null && charge is null && voltage is null) return;
        int? wear = (design is > 0 && full is > 0) ? (int)Math.Round(100.0 * (design.Value - full.Value) / design.Value) : null;
        if (wear is < 0) wear = 0;
        r.Battery = new BatteryHealth(wear, design, full, charge, voltage, chargeRate, dischargeRate, model, chemistry);
    }

    private static string ChemistryName(int c) => c switch
    {
        3 => "Lead Acid", 4 => "NiCd", 5 => "NiMH", 6 => "Li-ion", 7 => "Zinc air", 8 => "Li-polymer", _ => "",
    };

    private static void CollectUptime(HealthReport r)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT LastBootUpTime, InstallDate FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    if (o["LastBootUpTime"]?.ToString() is { Length: > 0 } lb) r.LastBoot = ManagementDateTimeConverter.ToDateTime(lb);
                    if (o["InstallDate"]?.ToString() is { Length: > 0 } id) r.InstallDate = ManagementDateTimeConverter.ToDateTime(id);
                    break;
                }
        }
        catch { }
    }

    private static string BusName(int bus) => bus switch
    {
        3 => "ATA", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 7 => "USB", 17 => "NVMe", 12 => "SD", 13 => "MMC", _ => "",
    };

    private static long ToLong(object? o)
    {
        try { return o is null ? 0 : Convert.ToInt64(o); } catch { return 0; }
    }

    private static long? ToNullableLong(object? o)
    {
        try { return o is null ? null : Convert.ToInt64(o); } catch { return null; }
    }

    private static int? ToNullableInt(object? o)
    {
        try { return o is null ? null : Convert.ToInt32(o); } catch { return null; }
    }
}
