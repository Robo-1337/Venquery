using System.Collections.Generic;
using System.Linq;

namespace Venquery.Services;

/// <summary>Filters a raw <c>Win32_VideoController</c> name list down to REAL graphics adapters (discrete or
/// integrated), dropping virtual/software display adapters (Microsoft Basic/Remote Display, Parsec, Citrix,
/// DisplayLink, VM adapters, etc.), and orders discrete GPUs before integrated so the "headline" GPU is the
/// impressive one. Used by the Showcase only; the hardware-lock fingerprint (`MachineFingerprint`) stays RAW.</summary>
public static class GpuFilter
{
    // A real GPU always carries one of these vendor/brand tokens; virtual/software display adapters do not
    // (they read "... Display Adapter" / "... Render Driver" with no GPU vendor in the name).
    private static readonly string[] RealTokens =
        { "NVIDIA", "GEFORCE", "QUADRO", "TESLA", "TITAN", "RTX", "GTX", "RADEON", "FIREPRO", "INSTINCT", "INTEL", "MATROX" };

    public static bool IsRealGpu(string? name)
    {
        var s = (name ?? "").ToUpperInvariant();
        return s.Length > 0 && RealTokens.Any(t => s.Contains(t));
    }

    /// <summary>True for an INTEGRATED GPU (a CPU's iGPU), false for a discrete card. Used only for ordering.</summary>
    public static bool IsIntegrated(string? name)
    {
        var s = (name ?? "").ToUpperInvariant();
        if (s.Contains("NVIDIA") || s.Contains("GEFORCE") || s.Contains("QUADRO") || s.Contains("RTX") || s.Contains("GTX")) return false;
        if (s.Contains("ARC")) return false;                              // Intel Arc = discrete
        if (s.Contains("INTEL") && s.Contains("GRAPHICS")) return true;   // Intel HD / UHD / Iris / Xe
        if (s.Contains("RADEON"))
        {
            if (s.Contains(" RX") || s.Contains("FIREPRO") || s.Contains("INSTINCT") || s.Contains(" PRO ")) return false;   // discrete Radeon
            if (s.Contains("GRAPHICS") || s.Contains("VEGA")) return true;                                                    // Ryzen APU iGPU
        }
        return false;                                                    // unknown → treat as discrete (shown first)
    }

    /// <summary>Keep real GPUs only, trim, de-dupe (case-insensitive), discrete first (stable within a group).</summary>
    public static List<string> Clean(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var real = new List<string>();
        foreach (var raw in names)
        {
            var n = (raw ?? "").Trim();
            if (n.Length == 0 || !IsRealGpu(n) || !seen.Add(n)) continue;
            real.Add(n);
        }
        return real.OrderBy(n => IsIntegrated(n) ? 1 : 0).ToList();
    }
}
