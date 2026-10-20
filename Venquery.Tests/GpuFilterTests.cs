using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the Showcase GPU filter: only real discrete/integrated GPUs are shown, virtual and software
/// display adapters are dropped, and discrete cards are ordered before integrated.</summary>
public class GpuFilterTests
{
    [Theory]
    // real GPUs
    [InlineData("NVIDIA GeForce GTX 1050", true)]
    [InlineData("NVIDIA GeForce RTX 4070 Laptop GPU", true)]
    [InlineData("NVIDIA Quadro P2000", true)]
    [InlineData("Intel(R) HD Graphics 630", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", true)]
    [InlineData("AMD Radeon RX 6800 XT", true)]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    // virtual / software display adapters, NOT GPUs
    [InlineData("Microsoft Basic Display Adapter", false)]
    [InlineData("Microsoft Basic Render Driver", false)]
    [InlineData("Microsoft Remote Display Adapter", false)]
    [InlineData("Microsoft Hyper-V Video", false)]
    [InlineData("Parsec Virtual Display Adapter", false)]
    [InlineData("Citrix Indirect Display Adapter", false)]
    [InlineData("DisplayLink USB Device", false)]
    [InlineData("VMware SVGA 3D", false)]
    [InlineData("Oracle VirtualBox Graphics Adapter", false)]
    [InlineData("", false)]
    public void IsRealGpu_detects_only_real(string name, bool expected)
        => Assert.Equal(expected, GpuFilter.IsRealGpu(name));

    [Theory]
    [InlineData("Intel(R) HD Graphics 630", true)]
    [InlineData("AMD Radeon(TM) Graphics", true)]        // Ryzen APU iGPU
    [InlineData("NVIDIA GeForce GTX 1050", false)]       // discrete
    [InlineData("AMD Radeon RX 6800", false)]            // discrete
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false)] // discrete
    public void IsIntegrated_classifies_igpu_vs_dgpu(string name, bool integrated)
        => Assert.Equal(integrated, GpuFilter.IsIntegrated(name));

    [Fact]
    public void Clean_filters_virtual_and_puts_discrete_first()
    {
        var input = new[]
        {
            "Intel(R) HD Graphics 630",
            "Microsoft Basic Display Adapter",
            "NVIDIA GeForce GTX 1050",
            "Parsec Virtual Display Adapter",
        };
        Assert.Equal(new[] { "NVIDIA GeForce GTX 1050", "Intel(R) HD Graphics 630" }, GpuFilter.Clean(input));
    }

    [Fact]
    public void Clean_dedupes_case_insensitively()
        => Assert.Single(GpuFilter.Clean(new[] { "NVIDIA GeForce RTX 3080", "nvidia geforce rtx 3080" }));
}
