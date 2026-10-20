using System.Linq;
using Venquery.Models;
using Xunit;

namespace Venquery.Tests;

/// <summary>The driver-status logic was the riskiest untested code (it decides whether the UI
/// says "update available", "up to date", "rollback", etc.). These lock its behaviour — including
/// the NVIDIA driver-store → marketing version normalisation that a bug could silently break.</summary>
public class DeviceRowStatusTests
{
    private static DeviceRow NvidiaRow(string installed, params string[] offered)
    {
        var def = new Definition { Label = "NVIDIA GeForce GPUs", Resolver = "nvidia-geforce" };
        var resolved = offered.Select(v => new VersionEntry { Version = v, Url = $"https://nv/{v}.exe" });
        var hw = new ScannedDevice { Name = "NVIDIA GeForce RTX", PnpClass = "Display", InstalledVersion = installed };
        return new DeviceRow(hw, def, null, null, resolved);
    }

    [Theory]
    [InlineData("32.0.15.6636", "576.02", "update")]   // installed normalises to 566.36  <  576.02
    [InlineData("32.0.15.6636", "566.36", "current")]  // exact match
    [InlineData("32.0.15.6636", "560.94", "rollback")] // 566.36  >  560.94
    public void Nvidia_status_reflects_normalized_version_compare(string installed, string offered, string expected)
        => Assert.Equal(expected, NvidiaRow(installed, offered).StatusKind);

    [Fact]
    public void Nvidia_unknown_installed_version_is_unknown()
        => Assert.Equal("unknown", NvidiaRow("", "576.02").StatusKind);

    [Fact]
    public void Non_comparable_tool_installer_is_available_not_update()
    {
        // Intel DSA-style installer: "version" is a product label, not a comparable number.
        var def = new Definition { Label = "Intel DSA" };
        var resolved = new[] { new VersionEntry { Version = "Latest", Comparable = false, Url = "https://intel/dsa" } };
        var hw = new ScannedDevice { Name = "Intel Graphics", PnpClass = "Display", InstalledVersion = "31.0.101.5000" };
        Assert.Equal("available", new DeviceRow(hw, def, null, null, resolved).StatusKind);
    }

    [Theory]
    [InlineData("10.0.19041.1", "10.0.19045.1", "update")]
    [InlineData("10.0.19045.1", "10.0.19045.1", "current")]
    [InlineData("10.0.19045.1", "10.0.19041.1", "rollback")]
    public void Plain_numeric_versions_compare_component_wise(string installed, string offered, string expected)
    {
        var def = new Definition { Label = "Realtek", Versions = { new VersionEntry { Version = offered, Url = "https://r/x" } } };
        var hw = new ScannedDevice { Name = "Realtek", PnpClass = "Net", InstalledVersion = installed };
        Assert.Equal(expected, new DeviceRow(hw, def, null, null, null).StatusKind);
    }

    [Fact]
    public void No_definition_is_none()
    {
        var hw = new ScannedDevice { Name = "x", PnpClass = "USB" };
        var row = new DeviceRow(hw, null, new VendorFallback { Name = "v", Domain = "v.com" });
        Assert.Equal("none", row.StatusKind);
    }

    [Fact]
    public void Show_older_versions_toggle_reveals_full_history()
    {
        var def = new Definition
        {
            Label = "GPU",
            Versions =
            {
                new VersionEntry { Version = "3", Url = "u3" },
                new VersionEntry { Version = "2", Url = "u2" },
                new VersionEntry { Version = "1", Url = "u1" },
            },
        };
        var row = new DeviceRow(new ScannedDevice { Name = "g", PnpClass = "Display" }, def, null);

        Assert.Single(row.VisibleVersions);       // only newest shown by default
        Assert.Equal(2, row.OlderVersionCount);   // 3 total minus the visible one
        Assert.Equal("3", row.SelectedVersion!.Version);

        row.ShowOlderVersions = true;
        Assert.Equal(3, row.VisibleVersions.Count);
        Assert.Equal("3", row.SelectedVersion!.Version); // selection preserved across toggle
    }
}
