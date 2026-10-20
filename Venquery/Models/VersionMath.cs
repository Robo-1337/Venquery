namespace Venquery.Models;

/// <summary>Numeric dot-separated version comparison, shared by the driver-status logic and the
/// legacy-GPU driver cap so both agree on what "newer" means.</summary>
public static class VersionMath
{
    /// <summary>Returns &lt;0 / 0 / &gt;0, or null if either side isn't a plain numeric version
    /// (so the caller can decline to guess rather than compare nonsense).</summary>
    public static int? Compare(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return null;
        var pa = a.Split('.');
        var pb = b.Split('.');
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            if (!int.TryParse(sa, out var ai) || !int.TryParse(sb, out var bi)) return null;
            if (ai != bi) return ai.CompareTo(bi);
        }
        return 0;
    }
}
