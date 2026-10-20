using System.IO;
using System.Net.Http;
using System.Text.Json;
using Venquery.Models;

namespace Venquery.Services;

/// <summary>
/// Loads the driver-definitions manifest. Order of precedence:
///   1. Fresh copy fetched from the manifest's own "manifestUrl" (if reachable)
///   2. Cached copy in %LOCALAPPDATA%\Venquery (read-only fallback: the pre-1.1 cache folder)
///   3. definitions.json bundled beside the executable
/// The bundled file always ships, so the app works fully offline.
/// </summary>
public sealed class DefinitionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public Manifest Manifest { get; private set; } = new();
    public string Source { get; private set; } = "none";

    private static string BundledPath =>
        Path.Combine(AppContext.BaseDirectory, "definitions.json");

    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Venquery");

    private static string CachePath => Path.Combine(CacheDir, "definitions.json");

    // Before 1.1 the app's internal name was used for the cache folder. Still read (never written) so an
    // existing synced copy keeps working offline until the next successful sync writes the new location.
    // Spelled in two parts so a project-wide rename can't silently rewrite it.
    private static string OldCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Open" + "DriverFetch", "definitions.json");

    public async Task LoadAsync(IProgress<string>? log = null)
    {
        // Start from the bundled manifest so we always have something valid.
        var bundled = TryLoad(BundledPath);
        if (bundled is not null) { Manifest = bundled; Source = "bundled"; }

        var cached = TryLoad(CachePath) ?? TryLoad(OldCachePath);
        if (cached is not null && IsNewer(cached, Manifest)) { Manifest = cached; Source = "cached"; }

        // Attempt an online refresh from the URL declared inside the manifest.
        var url = Manifest.ManifestUrl;
        if (!string.IsNullOrEmpty(url) && !DownloadService.IsHttps(url))
        {
            log?.Report("  definitions sync skipped: the update address isn't HTTPS");
            url = null;
        }
        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                log?.Report($"> sync driver definitions · {new Uri(url).Host}");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Venquery/0.1");
                var json = await http.GetStringAsync(url);
                var fresh = JsonSerializer.Deserialize<Manifest>(json, JsonOpts);
                if (fresh is not null && fresh.Devices.Count > 0)
                {
                    Manifest = fresh;
                    Source = "online";
                    Directory.CreateDirectory(CacheDir);
                    await Task.Run(() => File.WriteAllText(CachePath, json));   // net48-compatible async write
                    log?.Report($"  definitions updated · {fresh.Updated} · {fresh.Devices.Count} entries");
                }
            }
            catch (Exception ex)
            {
                log?.Report($"  offline or unreachable ({ex.GetType().Name}), using {Source} definitions");
            }
        }

        log?.Report($"> definitions ready · source: {Source} · updated {Manifest.Updated} · {Manifest.Devices.Count} device entries");
    }

    /// <summary>Match a scanned device to the most specific definition available.</summary>
    public (Definition? def, VendorFallback? fallback) Match(ScannedDevice hw)
    {
        Definition? venOnly = null;

        foreach (var d in Manifest.Devices)
        {
            if (!string.Equals(d.Bus, hw.Bus, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(d.Ven, hw.Vendor, StringComparison.OrdinalIgnoreCase)) continue;
            if (d.PnpClass is not null &&
                !string.Equals(d.PnpClass, hw.PnpClass, StringComparison.OrdinalIgnoreCase)) continue;

            if (d.Dev is null || d.Dev.Count == 0)
            {
                venOnly ??= d;                        // vendor-wide entry; keep as fallback
                continue;
            }
            if (d.Dev.Any(x => string.Equals(x, hw.Device, StringComparison.OrdinalIgnoreCase)))
                return (d, null);                     // exact ven+dev match wins
        }

        if (venOnly is not null) return (venOnly, null);

        var fb = Manifest.VendorFallbacks.FirstOrDefault(
            f => string.Equals(f.Ven, hw.Vendor, StringComparison.OrdinalIgnoreCase));
        return (null, fb);
    }

    /// <summary>Match a Win32_ComputerSystem/Win32_BaseBoard manufacturer string
    /// (e.g. "ASUSTeK COMPUTER INC.") against the curated system-vendor aliases.</summary>
    public SystemVendor? MatchSystemVendor(string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return null;
        var m = manufacturer.Trim();
        return Manifest.SystemVendors.FirstOrDefault(v =>
            v.Aliases.Any(a => m.StartsWith(a, StringComparison.OrdinalIgnoreCase)));
    }

    private static Manifest? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOpts);
            return (m is not null && m.Devices.Count > 0) ? m : null;
        }
        catch { return null; }
    }

    private static bool IsNewer(Manifest a, Manifest b)
        => string.CompareOrdinal(a.Updated, b.Updated) > 0;
}
