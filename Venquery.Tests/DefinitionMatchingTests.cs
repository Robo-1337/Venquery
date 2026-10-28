using System.Collections.Generic;
using System.Threading.Tasks;
using Venquery.Models;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>How a device is identified: reading hardware IDs (incl. the audio buses and the SUBSYS board/OEM ID),
/// the fixed match precedence, the never-claim list and parts routed to the PC maker.</summary>
public class DefinitionMatchingTests
{
    // ---- hardware ID parsing (real IDs from an Acer AN515-51) ----

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_1C8D&SUBSYS_118B1025&REV_A1", "PCI", "10DE", "1C8D", "1025", "118B")]   // PCI SUBSYS = device+vendor
    [InlineData(@"INTELAUDIO\FUNC_01&VEN_10EC&DEV_0255&SUBSYS_1025118A&REV_1000", "INTELAUDIO", "10EC", "0255", "1025", "118A")]   // audio SUBSYS = vendor+device
    [InlineData(@"INTELAUDIO\DSP_CTLR_DEV_A171&VEN_8086&DEV_0222&SUBSYS_00000022&REV_0001", "INTELAUDIO", "8086", "0222", "", "")]   // prefix + empty subsystem
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10DE&DEV_0082&SUBSYS_10DE118B&REV_1001", "HDAUDIO", "10DE", "0082", "10DE", "118B")]
    [InlineData(@"USB\VID_04F2&PID_B5F7&REV_9226&MI_00", "USB", "04F2", "B5F7", "", "")]
    [InlineData(@"usb\vid_8087&pid_0032&rev_0000", "USB", "8087", "0032", "", "")]
    public void Hardware_ids_are_read_with_their_subsystem(string raw, string bus, string ven, string dev, string subVen, string subDev)
    {
        var id = HardwareScanner.ParseHardwareId(raw);
        Assert.NotNull(id);
        Assert.Equal((bus, ven, dev, subVen, subDev), id!.Value);
    }

    [Theory]
    [InlineData(@"ACPI\VEN_INT&DEV_33A0")]
    [InlineData(@"HID\VID_1025&UP:000C_U:0001")]
    [InlineData(@"USB\ROOT_HUB30&VID8086&PIDA12F&REV0031")]
    [InlineData("")]
    public void Other_buses_and_root_hubs_are_ignored(string raw) => Assert.Null(HardwareScanner.ParseHardwareId(raw));

    // ---- precedence (synthetic manifest) ----

    private static Definition Def(string label, string ven = "AAAA", string bus = "PCI", string? cls = null,
        List<string>? dev = null, List<string>? subsys = null)
        => new() { Label = label, Bus = bus, Ven = ven, PnpClass = cls, Dev = dev, Subsys = subsys, Domain = "x.example" };

    private static ScannedDevice Hw(string ven = "AAAA", string dev = "0001", string cls = "Net", string bus = "PCI", string subVen = "")
        => new() { Bus = bus, Vendor = ven, Device = dev, PnpClass = cls, SubsysVendor = subVen };

    [Fact]
    public void Class_entry_beats_vendor_wide_whatever_the_file_order()
    {
        var s = new DefinitionStore(new Manifest { Devices = { Def("wide"), Def("net", cls: "Net") } });
        Assert.Equal("net", s.Match(Hw()).def!.Label);
        Assert.Equal("wide", s.Match(Hw(cls: "Media")).def!.Label);
    }

    [Fact]
    public void Device_entry_beats_subsystem_entry_beats_class_entry()
    {
        var s = new DefinitionStore(new Manifest
        {
            Devices =
            {
                Def("net", cls: "Net"),
                Def("board", subsys: new() { "1043" }),
                Def("exact", dev: new() { "0001" }),
            },
        });
        Assert.Equal("exact", s.Match(Hw(subVen: "1043")).def!.Label);
        Assert.Equal("board", s.Match(Hw(dev: "0002", subVen: "1043")).def!.Label);
        Assert.Equal("net", s.Match(Hw(dev: "0002", subVen: "1462")).def!.Label);   // subsystem mismatch: excluded
    }

    [Fact]
    public void A_shared_controller_id_only_matches_with_its_subsystem()
    {
        // e.g. a Phison controller: only claimed for the brand whose SUBSYS is present, otherwise nothing.
        var s = new DefinitionStore(new Manifest { Devices = { Def("corsair ssd", ven: "1987", subsys: new() { "1B1C" }) } });
        Assert.Equal("corsair ssd", s.Match(Hw(ven: "1987", subVen: "1B1C")).def!.Label);
        Assert.Null(s.Match(Hw(ven: "1987")).def);
        Assert.Null(s.Match(Hw(ven: "1987", subVen: "1E4B")).def);
    }

    [Fact]
    public void Multi_bus_entry_matches_each_listed_bus_only()
    {
        var d = Def("codec", ven: "10EC"); d.Buses = new() { "HDAUDIO", "INTELAUDIO" };
        var s = new DefinitionStore(new Manifest { Devices = { d } });
        Assert.NotNull(s.Match(Hw(ven: "10EC", bus: "INTELAUDIO")).def);
        Assert.NotNull(s.Match(Hw(ven: "10EC", bus: "HDAUDIO")).def);
        Assert.Null(s.Match(Hw(ven: "10EC", bus: "PCI")).def);
    }

    [Fact]
    public void Generic_ids_are_never_claimed_even_if_an_entry_exists()
    {
        var s = new DefinitionStore(new Manifest
        {
            Devices = { Def("wrong brand", ven: "0483", bus: "USB") },
            OemParts = { new OemPart { Bus = "USB", Ven = "0483", Maker = "x" } },
            GenericIds = { new GenericId { Bus = "USB", Ven = "0483" } },
        });
        var hw = Hw(ven: "0483", bus: "USB", cls: "HIDClass");
        Assert.Equal((null, null), s.Match(hw));
        Assert.Null(s.MatchOemPart(hw));
    }

    // ---- the shipping database ----

    private static async Task<DefinitionStore> LoadedAsync()
    {
        var store = new DefinitionStore();
        await store.LoadAsync();
        return store;
    }

    [Fact]
    public async Task Intel_bluetooth_matches_on_usb_but_intel_hubs_do_not()
    {
        var s = await LoadedAsync();
        Assert.Contains("Bluetooth", s.Match(Hw(ven: "8087", dev: "0032", bus: "USB", cls: "Bluetooth")).def!.Label);
        Assert.Null(s.Match(Hw(ven: "8087", dev: "0024", bus: "USB", cls: "USB")).def);   // Intel rate-matching hub
    }

    [Theory]
    [InlineData("10DE", "NVIDIA HDMI")]
    [InlineData("1002", "AMD HDMI")]
    [InlineData("8086", "Intel display audio")]
    public async Task Hdmi_audio_points_at_the_graphics_driver(string ven, string label)
    {
        var s = await LoadedAsync();
        var def = s.Match(Hw(ven: ven, dev: "0082", bus: "HDAUDIO", cls: "Media")).def;
        Assert.NotNull(def);
        Assert.StartsWith(label, def!.Label);
        Assert.Null(def.Resolver);   // never offers a GPU driver download on the audio row
    }

    [Theory]
    [InlineData("INTELAUDIO", "10EC", "Realtek")]
    [InlineData("HDAUDIO", "10EC", "Realtek")]
    [InlineData("HDAUDIO", "14F1", "Conexant")]
    [InlineData("USB", "04F2", "Chicony")]
    [InlineData("PCI", "14C3", "MediaTek")]
    public async Task Odm_parts_have_no_brand_entry_and_route_to_the_pc_maker(string bus, string ven, string maker)
    {
        var s = await LoadedAsync();
        var hw = Hw(ven: ven, bus: bus, cls: "Media");
        Assert.Equal((null, null), s.Match(hw));
        Assert.StartsWith(maker, s.MatchOemPart(hw)!.Maker);
    }

    // ---- board-partner tools (real database) ----

    [Fact]
    public async Task An_asus_built_geforce_keeps_the_nvidia_driver_and_adds_gpu_tweak()
    {
        var s = await LoadedAsync();
        var hw = Hw(ven: "10DE", dev: "2684", cls: "Display", subVen: "1043");
        Assert.Equal("NVIDIA GeForce GPUs", s.Match(hw).def!.Label);
        Assert.Contains(s.PartnerToolsFor(hw), t => t.Url.Contains("asus.com"));
    }

    [Theory]
    [InlineData("10DE", "10DE")]   // NVIDIA Founders Edition: no partner
    [InlineData("10DE", "")]       // no SUBSYS at all
    [InlineData("8086", "1043")]   // an ASUS board's Intel iGPU is not an ASUS graphics card
    [InlineData("1002", "1043")]   // nor is an AMD APU on an ASUS board
    public async Task No_partner_tool_without_a_partner_built_card(string ven, string subVen)
    {
        var s = await LoadedAsync();
        Assert.Empty(s.PartnerToolsFor(Hw(ven: ven, cls: "Display", subVen: subVen)));
    }

    [Fact]
    public async Task A_sapphire_radeon_gets_trixx()
    {
        var s = await LoadedAsync();
        var hw = Hw(ven: "1002", dev: "744C", cls: "Display", subVen: "1DA2");
        Assert.Equal("AMD Radeon GPUs", s.Match(hw).def!.Label);
        Assert.Contains(s.PartnerToolsFor(hw), t => t.Url.Contains("sapphiretech.com"));
    }

    // ---- entries added in the 1.5.0 batches (real database) ----

    [Theory]
    [InlineData("USB", "0B95", "1790", "", "Net", "ASIX USB Ethernet adapters")]                       // this dev PC's adapter
    [InlineData("PCI", "10EC", "3000", "", "Net", "Killer Ethernet (Realtek-based E2500B / E2600 / E3000 / E5000)")]
    [InlineData("PCI", "10EC", "8168", "", "Net", "Realtek PCIe devices (audio / LAN)")]              // plain Realtek stays Realtek
    [InlineData("PCI", "8086", "2725", "1A56", "Net", "Intel Killer Wi-Fi")]
    [InlineData("PCI", "8086", "2725", "8086", "Net", "Intel Wi-Fi / Ethernet networking")]
    [InlineData("PCI", "8086", "F1A8", "", "SCSIAdapter", "Intel SSDs (now supported by Solidigm)")]
    [InlineData("PCI", "C0A9", "5415", "", "SCSIAdapter", "Crucial NVMe SSDs (P / T series)")]
    [InlineData("USB", "0951", "16DF", "", "Media", "HyperX headsets / keyboards / mice (now HP)")]
    [InlineData("USB", "0951", "1666", "", "USB", "Kingston USB drives")]
    [InlineData("USB", "054C", "0CE6", "", "HIDClass", "Sony DualSense controllers")]
    public async Task New_entries_match_the_right_brand(string bus, string ven, string dev, string subVen, string cls, string label)
    {
        var s = await LoadedAsync();
        Assert.Equal(label, s.Match(Hw(ven: ven, dev: dev, cls: cls, bus: bus, subVen: subVen)).def!.Label);
    }

    [Theory]
    [InlineData("USB", "054C", "09CC")]   // DualShock 4: Sony's updater is DualSense-only
    [InlineData("USB", "04E8", "6860")]   // a Samsung phone is not a Portable SSD
    [InlineData("PCI", "1179", "0603")]   // Toshiba CardBus bridge is not a KIOXIA SSD
    [InlineData("PCI", "1D97", "1160")]   // FORESEE drive on the Longsys ID is not Lexar
    [InlineData("USB", "0E6F", "0139")]   // 0E6F is shared (PDP / Logic3 / others)
    public async Task Shared_vendor_ids_only_match_the_listed_models(string bus, string ven, string dev)
    {
        var s = await LoadedAsync();
        Assert.Null(s.Match(Hw(ven: ven, dev: dev, bus: bus, cls: "USB")).def);
    }

    [Fact]
    public async Task A_generic_microcontroller_stays_unmatched()
    {
        var s = await LoadedAsync();
        var hw = Hw(ven: "0483", dev: "5740", bus: "USB", cls: "Ports");
        Assert.True(s.IsGenericId(hw));
        Assert.Equal((null, null), s.Match(hw));
        Assert.Null(s.MatchOemPart(hw));
    }
}
