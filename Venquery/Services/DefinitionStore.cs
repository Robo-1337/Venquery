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

    public DefinitionStore() { }

    /// <summary>A store over a given manifest (tests exercise the match rules without the shipping file).</summary>
    internal DefinitionStore(Manifest manifest) { Manifest = manifest; Source = "test"; }

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

    /// <summary>Match a scanned device to the most specific definition available. Precedence is fixed, not file
    /// order: an exact device-ID entry beats a subsystem-ID entry, which beats a class-specific entry, which beats a
    /// vendor-wide one (ties go to the earlier entry). A generic, widely shared chip ID never matches.</summary>
    public (Definition? def, VendorFallback? fallback) Match(ScannedDevice hw)
    {
        if (IsGenericId(hw)) return (null, null);

        Definition? best = null;
        var bestScore = -1;
        foreach (var d in Manifest.Devices)
        {
            if (!d.OnBus(hw.Bus)) continue;
            if (!Same(d.Ven, hw.Vendor)) continue;
            if (d.PnpClass is not null && !Same(d.PnpClass, hw.PnpClass)) continue;
            var byDevice = d.Dev is { Count: > 0 };
            if (byDevice && !d.Dev!.Any(x => Same(x, hw.Device))) continue;
            var bySubsys = d.Subsys is { Count: > 0 };
            if (bySubsys && !d.Subsys!.Any(x => Same(x, hw.SubsysVendor))) continue;

            var score = (byDevice ? 8 : 0) + (bySubsys ? 4 : 0) + (d.PnpClass is not null ? 2 : 0);
            if (score > bestScore) { best = d; bestScore = score; }
        }
        if (best is not null) return (best, null);

        var fb = Manifest.VendorFallbacks.FirstOrDefault(f => Same(f.Ven, hw.Vendor));
        return (null, fb);
    }

    /// <summary>True when the device's vendor ID is on the never-claim list (shared by countless brands).</summary>
    public bool IsGenericId(ScannedDevice hw)
        => Manifest.GenericIds.Any(g => Same(g.Bus, hw.Bus) && Same(g.Ven, hw.Vendor));

    /// <summary>The chip/ODM maker entry for a part whose drivers come from the PC maker (a laptop's Chicony webcam,
    /// a codec), or null. Callers only use it on a recognised OEM system, never on a DIY motherboard.</summary>
    public OemPart? MatchOemPart(ScannedDevice hw)
        => IsGenericId(hw) ? null : Manifest.OemParts.FirstOrDefault(o => Same(o.Bus, hw.Bus) && Same(o.Ven, hw.Vendor));

    /// <summary>The board partner's own tools for a card (ASUS GPU Tweak on an ASUS-built GeForce...), found by its
    /// SUBSYS vendor. They only add to the row's optional software; the driver still comes from <see cref="Match"/>.
    /// Callers skip laptops, whose GPU carries the laptop maker's SUBSYS ID.</summary>
    public IReadOnlyList<OptionalTool> PartnerToolsFor(ScannedDevice hw)
    {
        if (hw.SubsysVendor.Length == 0 || IsGenericId(hw)) return Array.Empty<OptionalTool>();
        return Manifest.PartnerTools
            .Where(p => Same(p.Bus, hw.Bus) && p.Vens.Any(v => Same(v, hw.Vendor))
                && (p.PnpClass is null || Same(p.PnpClass, hw.PnpClass)) && p.Subsys.Any(s => Same(s, hw.SubsysVendor)))
            .SelectMany(p => p.Tools)
            .ToList();
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
