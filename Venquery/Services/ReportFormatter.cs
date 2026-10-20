using System.Text;
using System.Text.Json;
using Venquery.Models;

namespace Venquery.Services;

/// <summary>The single, shared builder for Venquery's "system &amp; driver report" in text / CSV / JSON.
/// Extracted so the desktop Export and the headless CLI emit the SAME format
/// instead of each keeping its own copy. Callers map their device model (the GUI's <c>DeviceRow</c>, or a
/// raw <c>ScannedDevice</c>) onto the neutral <see cref="ReportDevice"/>, then call one of the
/// builders. Pure text assembly, no scanning, no I/O, no UI.</summary>
public static class ReportFormatter
{
    /// <summary>A format-neutral device line. Source/URLs are optional (a caller that does no online
    /// driver-source matching leaves them blank).</summary>
    public sealed record ReportDevice(
        string Name, string Category, string InstalledVersion, string InstalledDate,
        string Status, string Source, IReadOnlyList<string> HardwareIds,
        string DownloadUrl = "", string SupportUrl = "");

    private static string Bar => new('=', 60);

    /// <summary>The full text report, matching the desktop Export layout exactly.</summary>
    public static string BuildText(
        string appVersion, DateTime now, SystemInfo? sys, IReadOnlyList<ReportDevice> devices,
        HealthReport? health = null,
        StabilityReport? stability = null, ISet<string>? stabilityGroups = null,
        bool includeReliability = false, bool verboseStability = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Venquery {appVersion}, system & driver report");
        sb.AppendLine($"Generated {now:yyyy-MM-dd HH:mm}");
        sb.AppendLine(Bar);

        if (sys is not null && sys.HasAnything)
        {
            sb.AppendLine("SYSTEM");
            if (!string.IsNullOrWhiteSpace(sys.Manufacturer)) sb.AppendLine($"  Manufacturer : {sys.Manufacturer}");
            if (!string.IsNullOrWhiteSpace(sys.Model))        sb.AppendLine($"  Model        : {sys.Model}");
            if (!string.IsNullOrWhiteSpace(sys.Type))         sb.AppendLine($"  Type         : {sys.Type}");
            if (!string.IsNullOrWhiteSpace(sys.Os))           sb.AppendLine($"  OS           : {sys.Os}");
            if (!string.IsNullOrWhiteSpace(sys.Cpu))          sb.AppendLine($"  Processor    : {sys.Cpu}");
            if (!string.IsNullOrWhiteSpace(sys.Ram))          sb.AppendLine($"  Memory       : {sys.Ram}");
            sb.AppendLine(Bar);
        }

        foreach (var r in devices)
        {
            sb.AppendLine();
            sb.AppendLine($"{r.Name}  [{r.Category}]");
            sb.AppendLine($"  Installed : {r.InstalledVersion}{(string.IsNullOrWhiteSpace(r.InstalledDate) ? "" : $"  ({r.InstalledDate})")}");
            sb.AppendLine($"  Status    : {r.Status}");
            if (!string.IsNullOrWhiteSpace(r.Source)) sb.AppendLine($"  Source    : {r.Source}");
            foreach (var id in r.HardwareIds) sb.AppendLine($"  HWID      : {id}");
            if (!string.IsNullOrWhiteSpace(r.DownloadUrl)) sb.AppendLine($"  Download  : {r.DownloadUrl}");
            else if (!string.IsNullOrWhiteSpace(r.SupportUrl)) sb.AppendLine($"  Support   : {r.SupportUrl}");
        }

        if (health is not null)
        {
            sb.AppendLine();
            sb.AppendLine(Bar);
            sb.AppendLine(health.ToReportText());
        }

        if (stability is not null)
        {
            sb.AppendLine();
            sb.AppendLine(Bar);
            sb.AppendLine(stability.ToReportText(stabilityGroups, includeReliability, verboseStability));
        }

        return sb.ToString();
    }

    /// <summary>CSV of the device inventory (matches the desktop's devices.csv).</summary>
    public static string BuildCsv(IReadOnlyList<ReportDevice> devices)
    {
        static string C(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("Name,Category,Installed,Status,Source,HardwareIDs,Download");
        foreach (var r in devices)
        {
            var url = !string.IsNullOrWhiteSpace(r.DownloadUrl) ? r.DownloadUrl : r.SupportUrl;
            sb.AppendLine(string.Join(",", C(r.Name), C(r.Category), C(r.InstalledVersion),
                C(r.Status), C(r.Source), C(string.Join("; ", r.HardwareIds)), C(url)));
        }
        return sb.ToString();
    }

    /// <summary>Machine-readable JSON. Desktop passes health/stability = null; a caller can pass
    /// them for a richer report.</summary>
    public static string BuildJson(
        string appVersion, DateTime now, SystemInfo? sys, IReadOnlyList<ReportDevice> devices,
        HealthReport? health = null, StabilityReport? stability = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["tool"] = "Venquery",
            ["version"] = appVersion,
            ["generated"] = now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["system"] = sys is null ? null : new
            {
                manufacturer = sys.Manufacturer, model = sys.Model, type = sys.Type,
                os = sys.Os, cpu = sys.Cpu, memory = sys.Ram,
            },
            ["devices"] = devices.Select(r => new
            {
                name = r.Name, category = r.Category, installed = r.InstalledVersion, status = r.Status,
                source = string.IsNullOrWhiteSpace(r.Source) ? null : r.Source,
                hardwareIds = r.HardwareIds.ToList(),
                download = string.IsNullOrWhiteSpace(r.DownloadUrl) ? null : r.DownloadUrl,
                support = string.IsNullOrWhiteSpace(r.SupportUrl) ? null : r.SupportUrl,
            }).ToList(),
        };
        if (health is not null) payload["health"] = new
        {
            summary = health.SummaryLine(),
            disks = health.Disks, volumes = health.Volumes, memory = health.Memory, battery = health.Battery,
            lastBoot = health.LastBoot, installDate = health.InstallDate,
        };
        if (stability is not null) payload["stability"] = new
        {
            days = stability.Days, summary = stability.SummaryLine(), hasIssues = stability.HasIssues,
            counts = StabilityReport.GroupOrder.ToDictionary(g => g.Key, g => stability.CountIn(g.Key)),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
