using System.Text.RegularExpressions;

namespace Venquery.Services;

/// <summary>Best-effort "what year was this machine made" from the CPU name. The CPU generation is the most
/// reliable ON-BOX signal of a machine's era, far better than the BIOS date (which reports the latest-flashed
/// BIOS, not the original). Returns a 4-digit year, or "" when the CPU can't be dated. Series-launch-year
/// accuracy, no internet, easily hand-corrected. Covers Intel Core i (1st-14th) + Core Ultra + legacy, and AMD
/// Ryzen (desktop/mobile) + Threadripper + EPYC + legacy. Unit-tested in CpuYearTests.</summary>
public static class CpuYear
{
    public static string YearOf(string? cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName)) return "";
        var s = Normalize(cpuName);
        var y = Intel(s) ?? Amd(s);
        return y is > 1990 and < 2100 ? y.Value.ToString() : "";
    }

    private static string Normalize(string n)
    {
        n = n.ToUpperInvariant().Replace("(R)", " ").Replace("(TM)", " ").Replace("(C)", " ");
        n = Regex.Replace(n, @"@.*$", " ");            // drop "@ 2.50GHz"
        n = Regex.Replace(n, @"\s*-\s*", "-");          // join hyphens split by the (tm) removal, e.g. "FX -8350" -> "FX-8350"
        n = Regex.Replace(n, @"\d+-CORE", " ");         // "6-Core"
        n = n.Replace(" CPU", " ").Replace(" PROCESSOR", " ");
        n = Regex.Replace(n, @"\s+", " ").Trim();
        return " " + n + " ";
    }

    // ---- Intel -----------------------------------------------------------
    private static int? Intel(string s)
    {
        if (!(s.Contains("INTEL") || s.Contains(" CORE ") || s.Contains("XEON") || s.Contains("PENTIUM")
              || s.Contains("CELERON") || s.Contains("ATOM") || Regex.IsMatch(s, @"\bI[3579][\s-]")))
            return null;

        var mu = Regex.Match(s, @"ULTRA\s+\d?\s*(\d)\d\d");        // Core Ultra 7 155H / 9 285K
        if (mu.Success) return mu.Groups[1].Value[0] switch { '1' => 2023, '2' => 2024, '3' => 2025, _ => (int?)null };

        var mi = Regex.Match(s, @"\bI[3579][\s-]?(\d{3,5})");      // Core i5-7300HQ / i7-1260P / i9-14900K
        if (mi.Success) return CoreYear(CoreGen(mi.Groups[1].Value));

        if (s.Contains("CORE 2")) return 2007;
        if (s.Contains("CORE DUO") || s.Contains("CORE SOLO")) return 2006;

        var xe = Xeon(s); if (xe is not null) return xe;
        return PentiumCeleronAtom(s);
    }

    /// <summary>Intel Core generation from the SKU digit run: 5 digits use the first two (10510-&gt;10); 4 digits
    /// that lead with 1 use the first two (1260-&gt;12, 1065-&gt;10) else the first digit (7300-&gt;7); 3 digits are 1st gen.</summary>
    private static int CoreGen(string d)
    {
        if (d.Length >= 5) return int.Parse(d.Substring(0, 2));
        if (d.Length == 4) return d[0] == '1' ? int.Parse(d.Substring(0, 2)) : d[0] - '0';
        return 1;
    }

    private static int? CoreYear(int gen) => gen switch
    {
        1 => 2010, 2 => 2011, 3 => 2012, 4 => 2013, 5 => 2015, 6 => 2015, 7 => 2017,
        8 => 2017, 9 => 2018, 10 => 2019, 11 => 2020, 12 => 2021, 13 => 2022, 14 => 2023,
        _ => (int?)null,
    };

    private static int? Xeon(string s)
    {
        if (!s.Contains("XEON")) return null;
        var sc = Regex.Match(s, @"(?:PLATINUM|GOLD|SILVER|BRONZE)\s+\d(\d)\d\d");   // Scalable: 2nd digit = gen
        if (sc.Success) return sc.Groups[1].Value[0] switch { '1' => 2017, '2' => 2019, '3' => 2021, '4' => 2023, '5' => 2023, '6' => 2024, _ => 2017 };
        var vm = Regex.Match(s, @"E[357]-\d{4}\s*V(\d)");                            // E3/E5/E7 vN
        if (vm.Success) return vm.Groups[1].Value switch { "2" => 2013, "3" => 2014, "4" => 2016, "5" => 2015, "6" => 2017, _ => 2012 };
        if (Regex.IsMatch(s, @"E[357]-\d{3,4}")) return 2012;                        // v1
        if (s.Contains("XEON W")) return 2018;
        return 2016;
    }

    private static int? PentiumCeleronAtom(string s)
    {
        if (Regex.IsMatch(s, @"\bN[12]00\b")) return 2023;                           // Alder Lake-N N100/N200
        var n = Regex.Match(s, @"\b[NJ](\d)\d{3}\b");                                // Atom/Pentium Silver/Celeron N-J
        if (n.Success)
            return n.Groups[1].Value[0] switch { '2' => 2013, '3' => 2015, '4' => 2017, '5' => 2019, '6' => 2021, _ => (int?)null };
        var g = Regex.Match(s, @"\bG(\d)\d{3}\b");                                   // desktop Pentium Gold / Celeron G
        if (g.Success)
            return g.Groups[1].Value[0] switch { '1' => 2011, '2' => 2011, '3' => 2013, '4' => 2017, '5' => 2018, '6' => 2020, '7' => 2021, _ => (int?)null };
        var pg = Regex.Match(s, @"(?:PENTIUM|CELERON)\s+(?:GOLD\s+)?(\d{4,5})");     // mobile Pentium Gold 7505 etc.
        if (pg.Success) { var y = CoreYear(CoreGen(pg.Groups[1].Value)); if (y is not null) return y; }
        if (s.Contains("PENTIUM 4")) return 2003;
        if (s.Contains("PENTIUM D")) return 2005;
        if (s.Contains("PENTIUM")) return 2010;
        if (s.Contains("CELERON")) return 2013;
        if (s.Contains("ATOM")) return 2013;
        return null;
    }

    // ---- AMD -------------------------------------------------------------
    private static int? Amd(string s)
    {
        var isAmd = s.Contains("AMD") || s.Contains("RYZEN") || s.Contains("EPYC") || s.Contains("ATHLON")
                    || s.Contains("THREADRIPPER") || s.Contains("FX-") || s.Contains("PHENOM")
                    || s.Contains("SEMPRON") || Regex.IsMatch(s, @"\bA(4|6|8|10|12)-");
        if (!isAmd) return null;

        if (s.Contains("THREADRIPPER"))
        {
            var m = Regex.Match(s, @"(\d)\d{3}\s*(?:WX|X)?\b");
            if (m.Success) return m.Groups[1].Value[0] switch { '1' => 2017, '2' => 2018, '3' => 2019, '5' => 2022, '7' => 2023, _ => (int?)null };
        }
        if (s.Contains("EPYC"))
        {
            var m = Regex.Match(s, @"\b\d{3}(\d)\b");   // 7001/7002/7003/9004: last digit = generation
            if (m.Success) return m.Groups[1].Value switch { "1" => 2017, "2" => 2019, "3" => 2021, "4" => 2022, "5" => 2024, _ => 2017 };
            return 2017;
        }
        if (s.Contains("RYZEN"))
        {
            if (s.Contains("RYZEN AI")) return 2024;   // "Ryzen AI 9 HX 370" (new 2024 naming)
            var m = Regex.Match(s, @"RYZEN\s+(?:\d\s+)?(?:PRO\s+)?(\d)\d{3}([A-Z]*)");
            if (m.Success)
            {
                var first = m.Groups[1].Value[0];
                var suffix = m.Groups[2].Value;
                var mobile = (suffix.Contains("U") || suffix.Contains("H") || suffix.Contains("E") || suffix.Contains("C"))
                             && !suffix.Contains("G");
                return mobile ? RyzenMobile(first) : RyzenDesktop(first);
            }
        }
        var a = Regex.Match(s, @"\bA(?:4|6|8|10|12)-(\d)\d{3}");   // A-series APU
        if (a.Success) return a.Groups[1].Value[0] switch { '3' => 2011, '4' => 2012, '5' => 2012, '6' => 2013, '7' => 2014, '8' => 2015, '9' => 2016, _ => (int?)null };
        if (s.Contains("FX-")) return 2012;
        if (s.Contains("ATHLON"))
        {
            var m = Regex.Match(s, @"ATHLON\s+(?:GOLD\s+|SILVER\s+)?(\d)\d{3}");
            if (m.Success) return m.Groups[1].Value[0] switch { '3' => 2019, '7' => 2023, _ => 2019 };
            if (s.Contains("ATHLON II")) return 2009;
            if (s.Contains("ATHLON 64")) return 2004;
            return 2010;
        }
        if (s.Contains("PHENOM")) return 2009;
        if (s.Contains("SEMPRON")) return 2008;
        return null;
    }

    private static int? RyzenDesktop(char first) => first switch
    { '1' => 2017, '2' => 2018, '3' => 2019, '4' => 2020, '5' => 2020, '6' => 2022, '7' => 2022, '8' => 2024, '9' => 2024, _ => (int?)null };

    private static int? RyzenMobile(char first) => first switch
    { '2' => 2018, '3' => 2019, '4' => 2020, '5' => 2021, '6' => 2022, '7' => 2023, '8' => 2024, '9' => 2025, _ => (int?)null };
}
