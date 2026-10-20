using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the CPU-generation → build-year mapping the Showcase uses. Every case is a real SKU with a
/// verified launch year; the goal is era-accuracy across every Intel and AMD series (owner requirement).</summary>
public class CpuYearTests
{
    [Theory]
    // ---- Intel Core i, generation from the SKU digit run ----
    [InlineData("Intel(R) Core(TM) i5-750 CPU @ 2.66GHz", "2010")]          // 1st gen (3-digit)
    [InlineData("Intel(R) Core(TM) i7-2600K CPU @ 3.40GHz", "2011")]        // 2nd
    [InlineData("Intel(R) Core(TM) i5-3570K CPU @ 3.40GHz", "2012")]        // 3rd
    [InlineData("Intel(R) Core(TM) i7-4790K CPU @ 4.00GHz", "2013")]        // 4th
    [InlineData("Intel(R) Core(TM) i7-5775C", "2015")]                       // 5th
    [InlineData("Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz", "2015")]        // 6th
    [InlineData("Intel(R) Core(TM) i5-7300HQ CPU @ 2.50GHz", "2017")]       // 7th (the test laptop)
    [InlineData("Intel(R) Core(TM) i7-8550U CPU @ 1.80GHz", "2017")]        // 8th
    [InlineData("Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz", "2018")]        // 9th
    [InlineData("Intel(R) Core(TM) i7-1065G7 CPU @ 1.30GHz", "2019")]       // 10th Ice Lake (4-digit + G)
    [InlineData("Intel(R) Core(TM) i7-10700K CPU @ 3.80GHz", "2019")]       // 10th Comet Lake (5-digit)
    [InlineData("Intel(R) Core(TM) i7-1165G7 CPU @ 2.80GHz", "2020")]       // 11th Tiger Lake
    [InlineData("Intel(R) Core(TM) i9-11900K", "2020")]                      // 11th Rocket Lake
    [InlineData("Intel(R) Core(TM) i7-1260P", "2021")]                       // 12th mobile (leads with 1)
    [InlineData("Intel(R) Core(TM) i9-12900K", "2021")]                      // 12th
    [InlineData("Intel(R) Core(TM) i7-13700H", "2022")]                      // 13th
    [InlineData("Intel(R) Core(TM) i9-14900K", "2023")]                      // 14th
    // ---- Intel Core Ultra ----
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", "2023")]                   // Series 1 (Meteor Lake)
    [InlineData("Intel(R) Core(TM) Ultra 9 285K", "2024")]                   // Series 2 (Arrow Lake)
    [InlineData("Intel(R) Core(TM) Ultra 7 258V", "2024")]                   // Series 2 (Lunar Lake)
    // ---- Intel legacy / non-Core ----
    [InlineData("Intel(R) Core(TM)2 Duo CPU E8400 @ 3.00GHz", "2007")]
    [InlineData("Intel(R) Pentium(R) Gold G6400 CPU @ 4.00GHz", "2020")]
    [InlineData("Intel(R) Celeron(R) N4020 CPU @ 1.10GHz", "2017")]
    [InlineData("Intel(R) Pentium(R) Silver N5030 CPU @ 1.10GHz", "2019")]
    [InlineData("Intel(R) Processor N100", "2023")]                          // Alder Lake-N
    [InlineData("Intel(R) Xeon(R) Gold 6130 CPU @ 2.10GHz", "2017")]         // Scalable gen1
    [InlineData("Intel(R) Xeon(R) E5-2680 v4 @ 2.40GHz", "2016")]
    [InlineData("Intel(R) Xeon(R) E3-1230 v5 @ 3.40GHz", "2015")]
    // ---- AMD Ryzen desktop ----
    [InlineData("AMD Ryzen 5 1600 Six-Core Processor", "2017")]
    [InlineData("AMD Ryzen 7 2700X Eight-Core Processor", "2018")]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", "2019")]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", "2020")]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", "2020")]
    [InlineData("AMD Ryzen 5 5600G with Radeon Graphics", "2020")]          // desktop APU (G)
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", "2022")]
    [InlineData("AMD Ryzen 9 9950X 16-Core Processor", "2024")]
    // ---- AMD Ryzen mobile (suffix U/H = mobile) ----
    [InlineData("AMD Ryzen 5 2500U with Radeon Vega Mobile Gfx", "2018")]
    [InlineData("AMD Ryzen 5 3500U with Radeon Vega Mobile Gfx", "2019")]
    [InlineData("AMD Ryzen 7 4800H with Radeon Graphics", "2020")]
    [InlineData("AMD Ryzen 7 5800U with Radeon Graphics", "2021")]
    [InlineData("AMD Ryzen 9 6900HX with Radeon Graphics", "2022")]
    [InlineData("AMD Ryzen 7 7840U w/ Radeon 780M Graphics", "2023")]
    [InlineData("AMD Ryzen 7 8840U w/ Radeon 780M Graphics", "2024")]
    // ---- AMD Threadripper / EPYC / legacy ----
    [InlineData("AMD Ryzen Threadripper 1950X 16-Core Processor", "2017")]
    [InlineData("AMD Ryzen Threadripper 3990X 64-Core Processor", "2019")]
    [InlineData("AMD EPYC 7302 16-Core Processor", "2019")]
    [InlineData("AMD EPYC 9654 96-Core Processor", "2022")]
    [InlineData("AMD FX(tm)-8350 Eight-Core Processor", "2012")]
    [InlineData("AMD A10-5800K APU with Radeon(tm) HD Graphics", "2012")]
    [InlineData("AMD A8-7600 Radeon R7", "2014")]
    [InlineData("AMD Athlon 3000G with Radeon Vega Graphics", "2019")]
    [InlineData("AMD Athlon Gold 3150U with Radeon Graphics", "2019")]
    [InlineData("AMD Phenom(tm) II X4 965 Processor", "2009")]
    public void Maps_cpu_to_expected_year(string cpu, string expected)
        => Assert.Equal(expected, CpuYear.YearOf(cpu));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some Unknown Processor")]
    [InlineData("VIA Nano X2")]
    public void Unknown_cpus_return_empty(string cpu)
        => Assert.Equal("", CpuYear.YearOf(cpu));
}
