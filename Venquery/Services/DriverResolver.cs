using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Venquery.Models;

namespace Venquery.Services;

/// <summary>
/// Turns a matched <see cref="Definition"/> into concrete, current, direct
/// download links at scan time. Two strategies keep links from ever going stale:
///
///   1. A live "resolver" (e.g. NVIDIA's official driver-lookup service) queried
///      on every scan. It returns the full recent version history, newest first, 
///      each with the exact WHQL installer URL, so nothing is ever hardcoded.
///   2. A vendor-maintained "always latest" installer URL that the vendor keeps
///      pointed at the current build (Intel DSA, SteelSeries GG, Razer Synapse…).
///
/// When neither applies, this returns an empty list and the UI falls back to the
/// official support page, we never fabricate a link we can't stand behind.
/// </summary>
public sealed class DriverResolver
{
    private readonly HttpClient _http;

    public DriverResolver()
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Venquery/0.4 (+driver-link-resolver)");
    }

    /// <summary>
    /// Returns the direct downloads for a definition, newest first. May be empty.
    /// </summary>
    public async Task<List<VersionEntry>> ResolveAsync(Definition def, ScannedDevice? device = null,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        // What can this specific card actually run? NVIDIA's lookup is queried with a fixed modern
        // product anchor, so without this an old card would be offered a driver it can't install.
        var support = GpuSupport.For(device?.Vendor, device?.Name, device?.Device);

        // Live strategies first, they yield the freshest possible links.
        try
        {
            if (string.Equals(def.Resolver, "nvidia-geforce", StringComparison.OrdinalIgnoreCase))
            {
                var list = await ResolveNvidiaGeForceAsync(log, ct);

                if (support?.MaxDriverVersion is { } cap)
                {
                    var before = list.Count;
                    list = GpuSupport.CapToSupported(list, cap);
                    log?.Report(list.Count > 0
                        ? $"  legacy GPU · {device?.Name}, limited to drivers up to {cap} ({before} → {list.Count})"
                        : $"  legacy GPU · {device?.Name}, last supported driver is {cap}, older than anything " +
                          "NVIDIA currently lists; falling back to the official support page");
                    // Never fall through to a generic installer for a capped card, if nothing it can
                    // run is on offer, the honest answer is the support page (empty list).
                    return list;
                }

                if (list.Count > 0) return list;
            }
        }
        catch (Exception ex)
        {
            log?.Report($"  live lookup failed for {def.Label} ({ex.GetType().Name}), using stable link/page");
        }

        // Vendor "always latest" installer URL (no network needed, stable by design).
        if (def.DirectDownload is { } dd && !string.IsNullOrWhiteSpace(dd.Url))
        {
            return new List<VersionEntry>
            {
                new()
                {
                    Version = string.IsNullOrEmpty(dd.Label) ? "Latest" : dd.Label,
                    Date = "",
                    Notes = dd.Note,
                    Url = dd.Url,
                    Comparable = false,   // a tool installer, not a comparable version number
                },
            };
        }

        return new List<VersionEntry>();
    }

    /// <summary>
    /// Queries NVIDIA's public driver-lookup service for the recent Game Ready
    /// driver history. The desktop GeForce Game Ready package is a single unified
    /// installer across the whole modern GeForce line, so a representative product
    /// id returns the versions that apply to any GeForce card. Each entry carries
    /// the exact WHQL .exe URL on NVIDIA's own CDN.
    /// </summary>
    private async Task<List<VersionEntry>> ResolveNvidiaGeForceAsync(IProgress<string>? log, CancellationToken ct)
    {
        // osID: 135 = Windows 11 (build >= 22000), 57 = Windows 10 64-bit.
        int osId = Environment.OSVersion.Version.Build >= 22000 ? 135 : 57;

        // psid=120 (GeForce RTX 30 series), pfid=933, a valid desktop GeForce anchor.
        var api =
            "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
            $"?func=DriverManualLookup&psid=120&pfid=933&osID={osId}" +
            "&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&sort1=0&numberOfResults=30";

        log?.Report("  resolving NVIDIA GeForce driver history · gfwsl.geforce.com");
        var json = await (await _http.GetAsync(api, ct)).Content.ReadAsStringAsync();   // ct-aware, net48-safe

        var results = new List<VersionEntry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in ids.EnumerateArray())
        {
            if (!item.TryGetProperty("downloadInfo", out var di)) continue;
            var url = GetStr(di, "DownloadURL");
            var version = GetStr(di, "Version");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(version)) continue;

            results.Add(new VersionEntry
            {
                Version = version,
                Date = NormalizeDate(GetStr(di, "ReleaseDateTime")),
                Notes = GetStr(di, "IsWHQL") == "1" ? "Game Ready · WHQL" : "Game Ready",
                Url = url,
            });
        }

        if (results.Count > 0)
            log?.Report($"  NVIDIA GeForce · {results.Count} versions ({results[0].Version} latest)");
        return results;
    }

    private static string GetStr(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>NVIDIA sends dates like "Tue Jul 07, 2026", render as yyyy-MM-dd.</summary>
    private static string NormalizeDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : raw.Trim();
    }
}
