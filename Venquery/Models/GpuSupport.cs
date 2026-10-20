using System.Globalization;
using System.Text.RegularExpressions;

namespace Venquery.Models;

/// <summary>What a GPU's legacy status means for driver selection.
/// <paramref name="MaxDriverVersion"/> is the newest driver the card can actually run (null = no
/// cap, i.e. current hardware). <paramref name="Notice"/> is the plain-English explanation shown
/// in the detail pane.</summary>
public sealed record GpuSupportInfo(string Tier, string? MaxDriverVersion, string Notice);

/// <summary>
/// Legacy driver-support facts for NVIDIA / AMD / Intel GPUs.
///
/// Why this exists: the NVIDIA resolver asks NVIDIA's lookup service with a fixed modern product
/// anchor, so it returns the newest Game Ready drivers regardless of which card is installed. On a
/// GTX 10-series or older that driver will not install, and the status logic would have called it
/// an "update" and pre-ticked it for download. Capping the offered list to what the card actually
/// supports keeps the app honest ("only show what's verifiable").
///
/// Facts verified 2026-07-23 against vendor announcements:
///   NVIDIA  Maxwell / Pascal / Volta (GTX 750(Ti), GTX 900, GTX 10, TITAN V), last feature
///           driver 581.80 (R580, Nov 2025); security-only updates through October 2028.
///   NVIDIA  Kepler (GTX 600, most GTX 700), last driver 472.98; Game Ready ended with 496.13.
///   NVIDIA  Fermi (GTX 400/500), last driver 391.35; security updates ended January 2019.
///   AMD     Polaris / Vega (RX 400/500/500X, RX Vega, Radeon VII), moved to a legacy driver
///           branch (Adrenalin 26.5.2); critical fixes and OS compatibility only.
///   Intel   6th–10th Gen processor graphics, legacy support model on the 31.0.101.x branch.
/// Note: GTX 750 / 750 Ti are Maxwell, not Kepler, so they get the 581.80 cap.
///
/// For NVIDIA we classify by the PCI **device id** first (exact) and only fall back to the marketing
/// name. The name is ambiguous for laptop GPUs: the same "GTX 860M" shipped as both Kepler and Maxwell
/// silicon, and mobile model numbers don't line up with desktop architecture (750M/755M are Kepler
/// though the desktop 750 is Maxwell). The device id (e.g. 0x13D8 = 970M = GM204 Maxwell) removes the
/// guesswork. Device-id ranges verified 2026-08-04 against the envytools PCI-id database and DeviceHunt.
/// </summary>
public static partial class GpuSupport
{
    private const string NvModernCap = "581.80";   // Maxwell / Pascal / Volta
    private const string NvKeplerCap = "472.98";
    private const string NvFermiCap = "391.35";

    // NVIDIA discrete-GPU PCI device-id ranges by architecture tier, newest legacy first.
    //   'L' = Maxwell / Pascal / Volta  -> 581.80
    //   'K' = Kepler                    -> 472.98
    //   'F' = Fermi                     -> 391.35
    // A device id outside every range (Turing and newer, or unrecognised) is deliberately left to the
    // name classifier, which returns null for current cards. Every range sits below Turing's 0x1E00
    // floor, so no current GPU can be captured here. Ranges: envytools pciid DB + DeviceHunt.
    private static readonly (int Lo, int Hi, char Tier)[] NvDeviceIdTiers =
    {
        // --- Maxwell / Pascal / Volta (581.80) ---
        (0x1340, 0x143F, 'L'),  // GM108 / GM107 / GM204 / GM206 (incl. 970M 0x13D8, 860M-Maxwell 0x1392)
        (0x1580, 0x15FF, 'L'),  // GP100
        (0x1617, 0x161A, 'L'),  // GM204M mobile (980M / 970M / 965M)
        (0x1667, 0x1667, 'L'),  // GM204
        (0x17C0, 0x17FF, 'L'),  // GM200 (980 Ti / TITAN X Maxwell)
        (0x1B00, 0x1DFF, 'L'),  // GP102/104/106/107/108 + GV100 (Volta)
        // --- Kepler (472.98) ---
        (0x0FC0, 0x0FFF, 'K'),  // GK107
        (0x1000, 0x103F, 'K'),  // GK110 / GK110B / GK210
        (0x1180, 0x11FF, 'K'),  // GK104 / GK106
        (0x1280, 0x12BF, 'K'),  // GK208
        // --- Fermi (391.35) ---
        (0x06C0, 0x06DF, 'F'),  // GF100
        (0x0DC0, 0x0DFF, 'F'),  // GF106 / GF108
        (0x0E20, 0x0E3F, 'F'),  // GF104
        (0x0F00, 0x0F3F, 'F'),  // GF108 (mobile)
        (0x1040, 0x107F, 'F'),  // GF119
        (0x1080, 0x109F, 'F'),  // GF110
        (0x1140, 0x117F, 'F'),  // GF117
        (0x1200, 0x121F, 'F'),  // GF114
        (0x1240, 0x125F, 'F'),  // GF116
    };

    // Trailing mobile suffix is captured (group 3) but intentionally NOT bounded by \b: a name like
    // "GTX 970M" has no word boundary between "970" and "M", which is exactly what made the old
    // name-only rule miss every laptop GPU. The (M) group lets the fallback treat mobile parts
    // conservatively when there is no device id to go on.
    // The .NET 8 build uses the compile-time [GeneratedRegex] source generator; the Legacy (net48) build
    // can't (it's .NET 7+), so it uses equivalent compiled Regex instances. Same patterns either way.
#if LEGACY
    private static readonly Regex _nvidiaModel = new(@"\b(RTX|GTX|GT|MX)\s?(\d{3,4})(M)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex NvidiaModel() => _nvidiaModel;

    private static readonly Regex _radeonModel = new(@"\bRX\s?(\d{3,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex RadeonModel() => _radeonModel;

    private static readonly Regex _radeonRSeries = new(@"\bR([3579])\s?M?\s?(\d{3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex RadeonRSeries() => _radeonRSeries;

    private static readonly Regex _radeonHd = new(@"\bHD\s?(\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex RadeonHd() => _radeonHd;

    private static readonly Regex _intelModel = new(@"\b(?:UHD|HD|IRIS)\b[^0-9]{0,20}(\d{3,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex IntelModel() => _intelModel;
#else
    [GeneratedRegex(@"\b(RTX|GTX|GT|MX)\s?(\d{3,4})(M)?", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaModel();

    [GeneratedRegex(@"\bRX\s?(\d{3,4})", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonModel();

    // R9 / R7 / R5 / R3 (200 & 300 series, incl. mobile "M" parts), older GCN.
    [GeneratedRegex(@"\bR([3579])\s?M?\s?(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonRSeries();

    // Radeon HD four-digit parts (HD 7000/8000 = GCN; lower = pre-GCN TeraScale).
    [GeneratedRegex(@"\bHD\s?(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonHd();

    [GeneratedRegex(@"\b(?:UHD|HD|IRIS)\b[^0-9]{0,20}(\d{3,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelModel();
#endif

    /// <summary>Classify a GPU from its PCI vendor id, WMI name, and (for NVIDIA) PCI device id.
    /// Null = current hardware with no restriction (also the safe default when nothing is recognised).
    /// For NVIDIA the device id is authoritative; the name is only a fallback when the id is unknown.</summary>
    public static GpuSupportInfo? For(string? vendorId, string? name, string? deviceId = null)
    {
        switch ((vendorId ?? "").ToUpperInvariant())
        {
            case "10DE":
                return NvidiaByDeviceId(deviceId)
                       ?? (string.IsNullOrWhiteSpace(name) ? null : Nvidia(name));
            case "1002":
                return string.IsNullOrWhiteSpace(name) ? null : Amd(name);
            case "8086":
                return string.IsNullOrWhiteSpace(name) ? null : Intel(name);
            default:
                return null;
        }
    }

    /// <summary>Classify an NVIDIA GPU by its 4-hex PCI device id (e.g. "13D8"). Returns null for a
    /// blank/unparseable id or one outside the known legacy ranges (Turing and newer), so the caller
    /// falls back to the name.</summary>
    private static GpuSupportInfo? NvidiaByDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        if (!int.TryParse(deviceId.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            return null;
        foreach (var (lo, hi, tier) in NvDeviceIdTiers)
            if (id >= lo && id <= hi)
                return tier switch { 'L' => NvLegacy(), 'K' => NvKepler(), 'F' => NvFermi(), _ => null };
        return null;   // Turing+ / unrecognised, let the name classifier decide (it returns null for current cards)
    }

    /// <summary>Drop any offered driver newer than the card's last supported version. With no cap
    /// the list is returned unchanged.</summary>
    public static List<VersionEntry> CapToSupported(IEnumerable<VersionEntry> versions, string? maxVersion)
    {
        var all = versions.ToList();
        if (string.IsNullOrEmpty(maxVersion)) return all;
        return all.Where(v => VersionMath.Compare(v.Version, maxVersion) is int c && c <= 0).ToList();
    }

    // --- vendor classifiers -------------------------------------------------

    private static GpuSupportInfo? Nvidia(string name)
    {
        var n = name.ToUpperInvariant();

        // TITAN cards don't follow the numeric naming pattern.
        if (n.Contains("TITAN"))
        {
            if (n.Contains("RTX")) return null;                        // TITAN RTX (Turing), current
            if (n.Contains("TITAN V")) return NvLegacy();               // Volta
            if (n.Contains("TITAN X")) return NvLegacy();               // TITAN X / Xp (Maxwell, Pascal)
            return NvKepler();                                          // TITAN / Black / Z (Kepler)
        }

        var m = NvidiaModel().Match(n);
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var num)) return null;
        var mobile = m.Groups[3].Success;   // trailing "M", a laptop part (600M–980M era)

        return m.Groups[1].Value.ToUpperInvariant() switch
        {
            "RTX" => null,                                              // RTX 20/30/40/50, current
            "MX" => num >= 400 ? null : NvLegacy(),                     // MX450+ Turing; MX150–350 Pascal
            "GTX" => num >= 1600 ? null                                 // GTX 16xx, Turing
                   : num >= 900 ? NvLegacy()                            // GTX 10xx Pascal, GTX 9xx/9xxM Maxwell
                   // Desktop 750/750 Ti are Maxwell; the 750M/755M laptop parts are Kepler. When we
                   // only have the name (no device id), fall to the *lower* cap for anything mobile so
                   // we never offer a driver the card can't run.
                   : num is >= 750 and <= 759 ? (mobile ? NvKepler() : NvLegacy())
                   : num >= 600 ? NvKepler()                            // other GTX 6xx / 7xx (+ 8xxM), Kepler-or-lower
                   : NvFermi(),                                         // GTX 4xx / 5xx, Fermi
            "GT" => num >= 1000 ? NvLegacy()                            // GT 1030, Pascal
                  : num >= 600 ? NvKepler()
                  : NvFermi(),
            _ => null,
        };
    }

    private static GpuSupportInfo? Amd(string name)
    {
        var n = name.ToUpperInvariant();

        // Older GCN that AMD moved to full legacy in 2021 (final driver Adrenalin 21.5.2):
        // Fury / Nano (Fiji), the R5/R7/R9 200 & 300 series, and Radeon HD 7000/8000.
        if (n.Contains("FURY") || n.Contains("NANO")) return AmdOlderLegacy();
        var rs = RadeonRSeries().Match(n);
        if (rs.Success && int.TryParse(rs.Groups[2].Value, out var rnum) && rnum is >= 200 and <= 399)
            return AmdOlderLegacy();
        var hd = RadeonHd().Match(n);
        if (hd.Success && int.TryParse(hd.Groups[1].Value, out var hnum) && hnum >= 7000)
            return AmdOlderLegacy();

        // Polaris / Vega, still getting critical/security updates on a separate legacy branch.
        if (n.Contains("VEGA") || n.Contains("RADEON VII")) return AmdLegacy();
        var m = RadeonModel().Match(n);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var num) && num is >= 400 and <= 599)
            return AmdLegacy();

        return null;
    }

    private static GpuSupportInfo? Intel(string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Contains("ARC") || n.Contains(" XE")) return null;        // Arc / Iris Xe, current
        var m = IntelModel().Match(n);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var num)) return null;
        // 6th–10th Gen graphics are three-digit 5xx–6xx (HD 530, UHD 630, Iris 655); the 2nd–5th Gen
        // parts are four-digit (HD 2000–6000, Iris 5100–6200). 11th Gen+ (UHD 7xx) stays current.
        if (num is >= 500 and <= 699) return IntelLegacy();            // 6th–10th Gen, 31.0.101.x branch
        if (num >= 2000) return IntelOlderLegacy();                    // 2nd–5th Gen, older 15.x branch
        return null;
    }

    // --- the notices --------------------------------------------------------

    private static GpuSupportInfo NvLegacy() => new(
        "nvidia-maxwell-pascal-volta", NvModernCap,
        $"Legacy GPU (Maxwell / Pascal / Volta). The last driver that supports this card is {NvModernCap}, " +
        "newer Game Ready drivers dropped it. NVIDIA ships security-only updates for it until October 2028. " +
        "Only drivers this card can actually run are listed below.");

    private static GpuSupportInfo NvKepler() => new(
        "nvidia-kepler", NvKeplerCap,
        $"Legacy GPU (Kepler). Game Ready support ended in 2021; the last driver that supports this card is " +
        $"{NvKeplerCap}. Newer drivers will not install, so no current download is offered, use the official " +
        "support page to get the final driver for your model.");

    private static GpuSupportInfo NvFermi() => new(
        "nvidia-fermi", NvFermiCap,
        $"Unsupported GPU (Fermi). The last driver for this card is {NvFermiCap} and security updates ended in " +
        "January 2019. No current download is offered, use the official support page for the archived driver.");

    private static GpuSupportInfo AmdLegacy() => new(
        "amd-polaris-vega", null,
        "Legacy GPU (Polaris / Vega). AMD moved these cards to a separate legacy driver branch, they get " +
        "critical fixes and OS compatibility only, with no new game optimizations. Make sure you pick the " +
        "legacy Adrenalin package for Polaris/Vega on AMD's page rather than the mainline driver.");

    private static GpuSupportInfo AmdOlderLegacy() => new(
        "amd-gcn-legacy", null,
        "End-of-life GPU (older GCN, Radeon HD 7000/8000, R5/R7/R9 200 & 300 series, Fury / Nano). AMD ended " +
        "driver development for these in 2021; the final release is Radeon Software Adrenalin 21.5.2 and no " +
        "further updates are planned. Use AMD's support page to get that last archived driver.");

    private static GpuSupportInfo IntelLegacy() => new(
        "intel-gen6-gen10", null,
        "Legacy graphics (6th–10th Gen Intel). Intel moved these to a legacy support model on the 31.0.101.x " +
        "driver branch, maintenance updates only. The Intel Driver & Support Assistant below will pick the " +
        "correct legacy driver; newer Intel graphics branches don't apply to this hardware.");

    private static GpuSupportInfo IntelOlderLegacy() => new(
        "intel-gen2-gen5", null,
        "End-of-life graphics (2nd–5th Gen Intel, HD 2000–6000 / Iris 5100–6200, Sandy Bridge through " +
        "Broadwell). These are on Intel's oldest graphics driver branch (15.40 for Haswell/Broadwell, earlier " +
        "for Sandy/Ivy Bridge) and are no longer updated. Install the last archived driver from Intel's support " +
        "page, newer Intel graphics branches won't apply to this hardware.");
}
