using System.Text;
using System.Text.Json;
using Venquery.Models;

namespace Venquery.Services;

/// <summary>Builds the headless <c>--health</c> / <c>--report</c> output (text or JSON) from the same
/// UI-free services the GUI uses. Compiled behind the <c>CLI</c> constant (every .NET 8 edition). Read-only;
/// <c>--report</c> makes the same GET-only curated network calls the GUI scan already does, and
/// <c>--health</c> touches no network.</summary>
public static class HeadlessReport
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<string> RunAsync(string mode, bool json, bool admin = false)
    {
        if (mode == "diag") return BuildDiag(json);
        var body = mode == "report" ? await BuildReportAsync(json) : await BuildHealthAsync(json);
        if (!admin || json) return body;   // (JSON report already carries structured sections; --admin text only)
        return body + Environment.NewLine + new string('=', 60) + Environment.NewLine + BuildDiag(false);
    }

    // The administrator deep read (SMART, NVMe, TPM/BitLocker, servicing, problem devices, services, network).
    // Reads directly, if the CLI is already running elevated (typical on a server admin shell) it gets the
    // full detail; otherwise it returns what's visible plus a note.
    private static string BuildDiag(bool json)
    {
        var read = ProToolsService.ReadDeep();
        if (json)
            return JsonSerializer.Serialize(new
            {
                version = MainWindow.AppVersion,
                edition = Edition.Current.DisplayName,
                generatedUtc = DateTime.UtcNow.ToString("o"),
                elevated = read.Elevated,
                admin = read,
            }, JsonOpts);
        var text = ProToolsService.ToText(read);
        if (!read.Elevated)
            text += Environment.NewLine + "(Run from an elevated shell, 'Run as administrator', for full SMART/BitLocker/TPM detail.)";
        return text;
    }

    // ---- --health (local WMI only) ----------------------------------------

    private static async Task<string> BuildHealthAsync(bool json)
    {
        var health = await HealthScanner.ScanAsync(null);
        if (!json) return health.ToReportText();
        return JsonSerializer.Serialize(new
        {
            version = MainWindow.AppVersion,
            edition = Edition.Current.DisplayName,
            generatedUtc = DateTime.UtcNow.ToString("o"),
            summary = health.SummaryLine(),
            lastBoot = health.LastBoot,
            installDate = health.InstallDate,
            disks = health.Disks,
            volumes = health.Volumes,
            memory = health.Memory,
            battery = health.Battery,
        }, JsonOpts);
    }

    // ---- --report (full scan + resolve) -----------------------------------

    private static async Task<string> BuildReportAsync(bool json)
    {
        var defs = new DefinitionStore();
        await defs.LoadAsync(null);

        var sys = await HardwareScanner.ScanSystemInfoAsync(null);
        var devices = await HardwareScanner.ScanAsync(null);
        var resolver = new DriverResolver();

        var rows = new List<DeviceRow>();
        var unmatched = new List<ScannedDevice>();

        // Whole-machine (OEM/motherboard) row, resolved by name, same as MainWindow.RunScan.
        var identity = await HardwareScanner.ScanSystemIdentityAsync(null);
        if (identity is not null)
        {
            var sv = defs.MatchSystemVendor(identity.Manufacturer);
            if (sv is not null)
            {
                var name = $"{identity.Manufacturer} {identity.Model}".Trim();
                var fallback = new VendorFallback { Name = name, Domain = sv.Domain, SupportPage = sv.SupportPage };
                var hw = new ScannedDevice
                {
                    Name = name,
                    PnpClass = identity.Category,
                    Bus = "SYS",
                    RawHardwareId = identity.Category.ToUpperInvariant(),
                };
                rows.Add(new DeviceRow(hw, null, fallback));
            }
        }

        // Group by matched definition/fallback, then resolve each group's driver source.
        var groups = new List<(Definition? def, VendorFallback? fb, List<ScannedDevice> hw)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var hw in devices)
        {
            var (def, fb) = defs.Match(hw);
            if (def is null && fb is null) { unmatched.Add(hw); continue; }
            var key = def is not null ? $"D:{def.Label}" : $"F:{fb!.Name}";
            if (index.TryGetValue(key, out var i)) groups[i].hw.Add(hw);
            else { index[key] = groups.Count; groups.Add((def, fb, new List<ScannedDevice> { hw })); }
        }
        foreach (var (def, fb, hwList) in groups)
        {
            var rep = PickRepresentative(hwList);
            var resolved = def is not null ? await resolver.ResolveAsync(def, rep, null) : null;
            rows.Add(new DeviceRow(rep, def, fb, hwList, resolved));
        }

        var health = await HealthScanner.ScanAsync(null);
        var stability = await StabilityScanner.ScanAsync(30, null);

        return json
            ? BuildReportJson(sys, rows, unmatched, health, stability, defs)
            : BuildReportText(sys, rows, unmatched, health, stability);
    }

    private static string BuildReportText(SystemInfo? sys, List<DeviceRow> rows, List<ScannedDevice> unmatched,
        HealthReport health, StabilityReport stability)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Edition.Current.DisplayName} {MainWindow.AppVersion}, system & driver report");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
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

        sb.AppendLine();
        sb.AppendLine(new string('=', 60));
        sb.AppendLine(health.ToReportText());

        sb.AppendLine();
        sb.AppendLine(new string('=', 60));
        sb.AppendLine(stability.ToReportText());

        sb.AppendLine();
        sb.AppendLine(new string('=', 60));
        sb.AppendLine("UNRECOGNIZED HARDWARE");
        sb.AppendLine("Devices that didn't match Venquery's driver database, most use a built-in Windows");
        sb.AppendLine("driver and need nothing; listed for troubleshooting / hardware-ID lookup.");
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
        return sb.ToString();
    }

    private static string BuildReportJson(SystemInfo? sys, List<DeviceRow> rows, List<ScannedDevice> unmatched,
        HealthReport health, StabilityReport stability, DefinitionStore defs)
    {
        return JsonSerializer.Serialize(new
        {
            version = MainWindow.AppVersion,
            edition = Edition.Current.DisplayName,
            generatedUtc = DateTime.UtcNow.ToString("o"),
            definitions = new { source = defs.Source, updated = defs.Manifest.Updated },
            system = sys is null ? null : new
            {
                sys.Manufacturer, sys.Model, sys.Type, sys.Os, sys.Cpu, sys.Ram,
            },
            devices = rows.Select(r => new
            {
                name = r.Name,
                category = r.Category,
                installed = r.InstalledVersion,
                installedDate = r.HasInstalledDate ? r.InstalledDateText : null,
                status = r.StatusText,
                statusKind = r.StatusKind,
                source = r.HasVerifiedSource ? r.Domain : null,
                hardwareIds = r.AllHardwareIds,
                downloadUrl = r.HasDirectUrl ? r.DirectUrl : null,
                supportPage = !r.HasDirectUrl && r.HasSupportPage ? r.SupportPage : null,
            }).ToList(),
            health = new
            {
                summary = health.SummaryLine(),
                lastBoot = health.LastBoot,
                installDate = health.InstallDate,
                disks = health.Disks,
                volumes = health.Volumes,
                memory = health.Memory,
                battery = health.Battery,
            },
            stability = new
            {
                days = stability.Days,
                summary = stability.SummaryLine(),
                hasIssues = stability.HasIssues,
                counts = StabilityReport.GroupOrder.ToDictionary(g => g.Key, g => stability.CountIn(g.Key)),
            },
            unrecognized = unmatched
                .OrderBy(d => d.PnpClass, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new { name = d.Name, pnpClass = d.PnpClass, hardwareId = d.RawHardwareId })
                .ToList(),
        }, JsonOpts);
    }

    // Same ranking as MainWindow.PickRepresentative, the most "driver-relevant" device in a group.
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
}
