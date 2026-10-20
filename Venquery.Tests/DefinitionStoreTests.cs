using System.Threading.Tasks;
using Venquery.Models;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Integration tests over the real shipping definitions.json (copied into the test
/// output). They double as a schema/integrity check: a malformed edit or a broken match rule
/// fails here. definitions.json has a null manifestUrl, so LoadAsync stays fully offline.</summary>
public class DefinitionStoreTests
{
    private static async Task<DefinitionStore> LoadedAsync()
    {
        var store = new DefinitionStore();
        await store.LoadAsync();
        return store;
    }

    [Fact]
    public async Task Bundled_definitions_load_non_empty()
    {
        var s = await LoadedAsync();
        Assert.NotEqual("none", s.Source);
        Assert.True(s.Manifest.Devices.Count > 0);
        Assert.True(s.Manifest.SystemVendors.Count > 0);
    }

    [Fact]
    public async Task Nvidia_display_device_matches_geforce_definition()
    {
        var s = await LoadedAsync();
        var (def, _) = s.Match(new ScannedDevice { Bus = "PCI", Vendor = "10DE", Device = "2482", PnpClass = "Display" });
        Assert.NotNull(def);
        Assert.Contains("NVIDIA", def!.Label);
    }

    [Fact]
    public async Task Kingston_pci_vendor_matches_by_vendor_id()
    {
        var s = await LoadedAsync();
        var (def, _) = s.Match(new ScannedDevice { Bus = "PCI", Vendor = "2646", Device = "5013", PnpClass = "" });
        Assert.NotNull(def);
        Assert.Equal("kingston.com", def!.Domain);
    }

    [Fact]
    public async Task Unknown_vendor_matches_nothing()
    {
        var s = await LoadedAsync();
        var (def, fb) = s.Match(new ScannedDevice { Bus = "PCI", Vendor = "FFFF", Device = "0001", PnpClass = "Net" });
        Assert.Null(def);
        Assert.Null(fb);
    }

    [Theory]
    [InlineData("CLEVO CO.", "clevo.com.tw")]
    [InlineData("Notebook", "clevo.com.tw")]                 // generic barebook string routes to Clevo
    [InlineData("SAMSUNG ELECTRONICS CO., LTD.", "samsung.com")]
    [InlineData("MECHREVO", "mechrevo.com")]
    [InlineData("Dynabook Inc.", "dynabook.com")]
    [InlineData("VAIO Corporation", "vaio.com")]
    [InlineData("Sony Corporation", "vaio.com")]        // old Sony VAIO routes to VAIO Corp support
    [InlineData("Panasonic Corporation", "panasonic.com")]
    [InlineData("Getac Technology", "getac.com")]
    public async Task System_vendor_aliases_resolve_to_expected_domain(string manufacturer, string domain)
    {
        var s = await LoadedAsync();
        var v = s.MatchSystemVendor(manufacturer);
        Assert.NotNull(v);
        Assert.Equal(domain, v!.Domain);
    }
}
