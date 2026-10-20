using System.Collections.Generic;
using Venquery.Models;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the legacy-GPU rules. If these break, the app can offer a driver a card
/// cannot install — the exact bug this logic exists to prevent.</summary>
public class GpuSupportTests
{
    private const string Nvidia = "10DE", Amd = "1002", Intel = "8086";

    // ---- NVIDIA: current hardware must stay uncapped -----------------------

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090")]
    [InlineData("NVIDIA GeForce RTX 4070 Laptop GPU")]
    [InlineData("NVIDIA GeForce RTX 3080")]
    [InlineData("NVIDIA GeForce RTX 2060")]
    [InlineData("NVIDIA GeForce GTX 1660 Ti")]
    [InlineData("NVIDIA GeForce GTX 1650")]
    [InlineData("NVIDIA TITAN RTX")]
    [InlineData("NVIDIA GeForce MX550")]
    public void Modern_nvidia_gpus_are_not_capped(string name)
        => Assert.Null(GpuSupport.For(Nvidia, name));

    // ---- NVIDIA: Maxwell / Pascal / Volta -> 581.80 ------------------------

    [Theory]
    [InlineData("NVIDIA GeForce GTX 1060 6GB")]
    [InlineData("NVIDIA GeForce GTX 1080 Ti")]
    [InlineData("NVIDIA GeForce GTX 1050")]
    [InlineData("NVIDIA GeForce GT 1030")]
    [InlineData("NVIDIA GeForce GTX 980")]
    [InlineData("NVIDIA GeForce GTX 750 Ti")]   // Maxwell, NOT Kepler
    [InlineData("NVIDIA TITAN V")]
    [InlineData("NVIDIA GeForce MX150")]
    public void Maxwell_pascal_volta_capped_to_581(string name)
    {
        var info = GpuSupport.For(Nvidia, name);
        Assert.NotNull(info);
        Assert.Equal("581.80", info!.MaxDriverVersion);
        Assert.Contains("581.80", info.Notice);
    }

    // ---- NVIDIA: Kepler -> 472.98, Fermi -> 391.35 -------------------------

    [Theory]
    [InlineData("NVIDIA GeForce GTX 780 Ti")]
    [InlineData("NVIDIA GeForce GTX 760")]
    [InlineData("NVIDIA GeForce GTX 660")]
    [InlineData("NVIDIA GeForce GT 730")]
    public void Kepler_capped_to_472(string name)
        => Assert.Equal("472.98", GpuSupport.For(Nvidia, name)!.MaxDriverVersion);

    [Theory]
    [InlineData("NVIDIA GeForce GTX 580")]
    [InlineData("NVIDIA GeForce GTX 480")]
    public void Fermi_capped_to_391(string name)
        => Assert.Equal("391.35", GpuSupport.For(Nvidia, name)!.MaxDriverVersion);

    // ---- NVIDIA by PCI device id (authoritative, disambiguates laptop parts) ----

    // Maxwell / Pascal / Volta dies -> 581.80. IDs verified against envytools + DeviceHunt.
    [Theory]
    [InlineData("13D8")]   // GM204 — GTX 970M (the reported bug)
    [InlineData("13C2")]   // GM204 — GTX 970
    [InlineData("1392")]   // GM107 — GTX 860M (Maxwell variant of the dual-arch name)
    [InlineData("1401")]   // GM206 — GTX 960
    [InlineData("1617")]   // GM204M — GTX 980M (mobile)
    [InlineData("17C8")]   // GM200 — GTX 980 Ti
    [InlineData("1B81")]   // GP104 — GTX 1070
    [InlineData("1C03")]   // GP106 — GTX 1060
    [InlineData("1D81")]   // GV100 — TITAN V (Volta)
    public void Nvidia_maxwell_pascal_volta_dies_capped_to_581(string dev)
    {
        var info = GpuSupport.For(Nvidia, "ignored name", dev);
        Assert.NotNull(info);
        Assert.Equal("581.80", info!.MaxDriverVersion);
    }

    // Kepler dies -> 472.98.
    [Theory]
    [InlineData("1184")]   // GK104 — GTX 770
    [InlineData("0FC6")]   // GK107 — GT 640 / 650M (Kepler laptop)
    [InlineData("1004")]   // GK110 — GTX 780
    [InlineData("1280")]   // GK208 — GT 730
    public void Nvidia_kepler_dies_capped_to_472(string dev)
        => Assert.Equal("472.98", GpuSupport.For(Nvidia, "x", dev)!.MaxDriverVersion);

    // Fermi dies -> 391.35.
    [Theory]
    [InlineData("06C0")]   // GF100 — GTX 480
    [InlineData("1140")]   // GF117 — laptop Fermi
    [InlineData("1200")]   // GF114 — GTX 560 Ti
    public void Nvidia_fermi_dies_capped_to_391(string dev)
        => Assert.Equal("391.35", GpuSupport.For(Nvidia, "x", dev)!.MaxDriverVersion);

    // Turing and newer device ids are NOT capped (fall through to the name, which is also current).
    [Theory]
    [InlineData("2184", "NVIDIA GeForce GTX 1660")]        // TU116 — Turing
    [InlineData("1F82", "NVIDIA GeForce GTX 1650")]        // TU117 — Turing
    [InlineData("2504", "NVIDIA GeForce RTX 3060")]        // GA106 — Ampere
    [InlineData("2782", "NVIDIA GeForce RTX 4070")]        // AD104 — Ada
    public void Nvidia_turing_and_newer_dies_are_not_capped(string dev, string name)
        => Assert.Null(GpuSupport.For(Nvidia, name, dev));

    // The device id wins over the marketing name — the whole point of using it. A Maxwell die keeps
    // 581.80 even when the name reads Kepler-era, and a Kepler die keeps 472.98 even under a Maxwell
    // laptop name. This is how the same "GTX 860M" (which shipped as both Kepler and Maxwell) is
    // capped to its actual silicon, which the name alone can never tell apart.
    [Fact]
    public void Device_id_wins_over_the_marketing_name()
    {
        Assert.Equal("581.80", GpuSupport.For(Nvidia, "NVIDIA GeForce GTX 760", "1392")!.MaxDriverVersion);  // GM107 Maxwell die
        Assert.Equal("472.98", GpuSupport.For(Nvidia, "NVIDIA GeForce GTX 970M", "1184")!.MaxDriverVersion); // GK104 Kepler die
    }

    // ---- NVIDIA name fallback (no device id) — the old regex missed every laptop "M" part ----

    [Theory]
    [InlineData("NVIDIA GeForce GTX 970M", "581.80")]   // 900M is Maxwell — matches now (was missed)
    [InlineData("NVIDIA GeForce GTX 980M", "581.80")]
    [InlineData("NVIDIA GeForce GTX 860M", "472.98")]   // 8xxM ambiguous -> conservative lower cap
    [InlineData("NVIDIA GeForce GT 750M",  "472.98")]   // mobile 750M is Kepler, not the Maxwell desktop 750
    [InlineData("NVIDIA GeForce GTX 680MX","472.98")]   // Kepler
    [InlineData("NVIDIA GeForce GTX 750 Ti","581.80")]  // desktop 750 Ti stays Maxwell
    public void Nvidia_name_fallback_handles_mobile_suffix(string name, string expectedCap)
        => Assert.Equal(expectedCap, GpuSupport.For(Nvidia, name)!.MaxDriverVersion);

    // ---- AMD / Intel: notice only, no version cap --------------------------

    [Theory]
    [InlineData("AMD Radeon RX 580")]
    [InlineData("AMD Radeon RX 570")]
    [InlineData("Radeon RX Vega 64")]
    [InlineData("AMD Radeon VII")]
    public void Amd_polaris_vega_gets_notice_without_cap(string name)
    {
        var info = GpuSupport.For(Amd, name);
        Assert.NotNull(info);
        Assert.Null(info!.MaxDriverVersion);          // AMD offers no versioned direct download
        Assert.Contains("legacy", info.Notice, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("AMD Radeon RX 6600")]
    [InlineData("AMD Radeon RX 5700 XT")]
    public void Modern_amd_gpus_get_no_notice(string name)
        => Assert.Null(GpuSupport.For(Amd, name));

    // Older GCN (HD 7000/8000, R-200/300, Fury/Nano) — full legacy since 2021 (Adrenalin 21.5.2).
    [Theory]
    [InlineData("AMD Radeon R9 390")]
    [InlineData("AMD Radeon R9 290X")]
    [InlineData("AMD Radeon R7 370")]
    [InlineData("AMD Radeon R9 M370X")]      // mobile "M" part
    [InlineData("AMD Radeon R9 Fury X")]
    [InlineData("AMD Radeon R9 Nano")]
    [InlineData("AMD Radeon HD 7970")]
    [InlineData("AMD Radeon HD 8670")]
    public void Amd_older_gcn_gets_eol_notice_without_cap(string name)
    {
        var info = GpuSupport.For(Amd, name);
        Assert.NotNull(info);
        Assert.Null(info!.MaxDriverVersion);
        Assert.Contains("21.5.2", info.Notice);
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 630")]
    [InlineData("Intel(R) HD Graphics 530")]
    [InlineData("Intel(R) Iris(R) Plus Graphics 655")]
    public void Intel_gen6_to_gen10_gets_notice_without_cap(string name)
    {
        var info = GpuSupport.For(Intel, name);
        Assert.NotNull(info);
        Assert.Null(info!.MaxDriverVersion);
        Assert.Contains("31.0.101", info.Notice);
    }

    [Theory]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    [InlineData("Intel(R) Iris(R) Xe Graphics")]
    [InlineData("Intel(R) UHD Graphics 770")]     // 11th Gen+ — not the legacy branch
    public void Modern_intel_graphics_get_no_notice(string name)
        => Assert.Null(GpuSupport.For(Intel, name));

    // 2nd–5th Gen (four-digit HD 2000–6000 / Iris 5100–6200) — older 15.x branch, EOL.
    [Theory]
    [InlineData("Intel(R) HD Graphics 4600")]     // Haswell (4th Gen)
    [InlineData("Intel(R) HD Graphics 4000")]     // Ivy Bridge (3rd Gen)
    [InlineData("Intel(R) HD Graphics 3000")]     // Sandy Bridge (2nd Gen)
    [InlineData("Intel(R) HD Graphics 5500")]     // Broadwell (5th Gen)
    [InlineData("Intel(R) Iris(TM) Pro Graphics 5200")]
    [InlineData("Intel(R) Iris(TM) Graphics 6100")]
    public void Intel_gen2_to_gen5_gets_eol_notice_without_cap(string name)
    {
        var info = GpuSupport.For(Intel, name);
        Assert.NotNull(info);
        Assert.Null(info!.MaxDriverVersion);
        Assert.Contains("15.40", info.Notice);
    }

    // ---- unknown / other vendors are left alone ----------------------------

    [Theory]
    [InlineData("10DE", "")]
    [InlineData("10DE", "NVIDIA Something Unrecognised")]
    [InlineData("1234", "Some Other Vendor GPU")]
    public void Unknown_input_is_safe_default_no_restriction(string ven, string name)
        => Assert.Null(GpuSupport.For(ven, name));

    // ---- the cap filter itself ---------------------------------------------

    private static List<VersionEntry> Versions(params string[] v)
        => v.Select(x => new VersionEntry { Version = x, Url = $"https://nv/{x}.exe" }).ToList();

    [Fact]
    public void Cap_removes_drivers_newer_than_the_last_supported()
    {
        var kept = GpuSupport.CapToSupported(Versions("590.10", "585.00", "581.80", "576.02"), "581.80");
        Assert.Equal(new[] { "581.80", "576.02" }, kept.Select(v => v.Version));
    }

    [Fact]
    public void Cap_can_remove_everything_when_card_is_far_out_of_date()
        => Assert.Empty(GpuSupport.CapToSupported(Versions("590.10", "581.80"), "472.98"));

    [Fact]
    public void No_cap_keeps_the_whole_list()
        => Assert.Equal(3, GpuSupport.CapToSupported(Versions("590.10", "585.00", "581.80"), null).Count);
}

public class VersionMathTests
{
    [Theory]
    [InlineData("581.80", "581.80", 0)]
    [InlineData("590.10", "581.80", 1)]
    [InlineData("472.98", "581.80", -1)]
    [InlineData("10.0.19045.1", "10.0.19041.1", 1)]
    public void Compares_component_wise(string a, string b, int expectedSign)
        => Assert.Equal(expectedSign, Math.Sign(VersionMath.Compare(a, b)!.Value));

    [Theory]
    [InlineData("Latest", "581.80")]
    [InlineData("581.80", "")]
    [InlineData(null, "581.80")]
    public void Non_numeric_returns_null_rather_than_guessing(string? a, string? b)
        => Assert.Null(VersionMath.Compare(a, b));
}
