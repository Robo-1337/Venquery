using System.Management;
using System.Text.RegularExpressions;
using Venquery.Models;

namespace Venquery.Services;

/// <summary>
/// Enumerates hardware through WMI (Win32_PnPEntity) and joins in the
/// installed driver version from Win32_PnPSignedDriver. Read-only; requires
/// no elevation.
/// </summary>
public static partial class HardwareScanner
{
    // Device classes worth enumerating at all. This list is intentionally broad, 
    // the real noise filter is that a device only ever appears in the UI if it
    // matches a curated, verified entry in definitions.json (see MainWindow's
    // scan handler). Generic USB hubs, root/host controllers, "AudioEndpoint"
    // software endpoints (the "Speakers (Realtek...)" duplicate of the real
    // audio chip already covered under "Media"), and anything else Windows
    // supplies its own in-box driver for simply won't have a curated match and
    // will be dropped automatically, without needing per-class hardcoding here.
    private static readonly HashSet<string> InterestingClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Display", "Net", "Media", "System", "USB", "HIDClass", "Mouse", "Keyboard",
        "SCSIAdapter", "HDC", "Bluetooth", "Image", "Camera",
        // Printers / scanners (their USB device) and USB-serial adapters (FTDI, Silicon Labs, Prolific, WCH).
        "Printer", "Ports",
    };

    // Generic, class-supplied device names that shouldn't win over a more specific
    // one when the same physical device (same bus+ven+dev) enumerates more than
    // once, e.g. a branded USB dongle that also exposes a generic HID collection.
    private static readonly string[] GenericNameMarkers =
    {
        "HID-compliant", "USB Composite Device", "USB Input Device",
        "Generic USB Hub", "USB Root Hub", "Unknown device",
    };

    private static bool IsGenericName(string name)
        => GenericNameMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // net8 uses the [GeneratedRegex] source generator; the Legacy (net48) build uses a compiled Regex.
#if LEGACY
    private static readonly Regex _hardwareIdPattern = new(@"^(PCI|USB|HDAUDIO|INTELAUDIO)\\(?:[A-Za-z0-9_]+&)*?(?:VEN|VID)_([0-9A-Fa-f]{4})&(?:DEV|PID)_([0-9A-Fa-f]{4})(?:&SUBSYS_([0-9A-Fa-f]{8}))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex HardwareIdPattern() => _hardwareIdPattern;
#else
    [GeneratedRegex(@"^(PCI|USB|HDAUDIO|INTELAUDIO)\\(?:[A-Za-z0-9_]+&)*?(?:VEN|VID)_([0-9A-Fa-f]{4})&(?:DEV|PID)_([0-9A-Fa-f]{4})(?:&SUBSYS_([0-9A-Fa-f]{8}))?", RegexOptions.IgnoreCase)]
    private static partial Regex HardwareIdPattern();
#endif

    /// <summary>Read bus, vendor, device and the subsystem (board/OEM) vendor + device out of a hardware ID such as
    /// <c>PCI\VEN_10DE&amp;DEV_1C8D&amp;SUBSYS_118B1025</c>, <c>INTELAUDIO\FUNC_01&amp;VEN_10EC&amp;DEV_0255&amp;SUBSYS_1025118A</c>
    /// or <c>INTELAUDIO\DSP_CTLR_DEV_A171&amp;VEN_8086&amp;DEV_0222</c>. Null when it isn't a PCI/USB/audio ID.
    /// PCI writes SUBSYS as device+vendor; the HD audio buses write it as vendor+device.</summary>
    public static (string Bus, string Ven, string Dev, string SubVen, string SubDev)? ParseHardwareId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var m = HardwareIdPattern().Match(raw);
        if (!m.Success) return null;
        var bus = m.Groups[1].Value.ToUpperInvariant();
        string subVen = "", subDev = "";
        if (m.Groups[4].Success)
        {
            var sub = m.Groups[4].Value.ToUpperInvariant();
            if (bus == "PCI") { subVen = sub.Substring(4, 4); subDev = sub.Substring(0, 4); }
            else { subVen = sub.Substring(0, 4); subDev = sub.Substring(4, 4); }
            if (subVen == "0000") { subVen = ""; subDev = ""; }
        }
        return (bus, m.Groups[2].Value.ToUpperInvariant(), m.Groups[3].Value.ToUpperInvariant(), subVen, subDev);
    }

    public static Task<List<ScannedDevice>> ScanAsync(IProgress<string>? log = null)
        => Task.Run(() => Scan(log));

    // Values Windows/OEMs commonly leave in Win32_ComputerSystem/Win32_BaseBoard
    // on hand-built desktops, not a real manufacturer or model name.
    private static readonly HashSet<string> PlaceholderIdentityValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "System manufacturer", "System Manufacturer", "System Product Name", "System Version",
        "To Be Filled By O.E.M.", "Default string", "Not Applicable", "O.E.M", "None", "",
    };

    private static bool IsPlaceholderIdentity(string s)
        => string.IsNullOrWhiteSpace(s) || PlaceholderIdentityValues.Contains(s.Trim());

    /// <summary>
    /// A motherboard or laptop chassis isn't a PnP device, it has no hardware ID, 
    /// so it's identified separately via Win32_ComputerSystem (OEM laptops/prebuilts)
    /// falling back to Win32_BaseBoard (DIY desktop motherboards).
    /// </summary>
    public static Task<SystemIdentity?> ScanSystemIdentityAsync(IProgress<string>? log = null)
        => Task.Run(() => ScanSystemIdentity(log));

    private static SystemIdentity? ScanSystemIdentity(IProgress<string>? log)
    {
        var (csManufacturer, csModel) = QueryOne("Win32_ComputerSystem", "Manufacturer", "Model");
        if (!IsPlaceholderIdentity(csManufacturer) && !IsPlaceholderIdentity(csModel))
        {
            var laptop = IsLaptop();
            log?.Report($"> system identity · {csManufacturer} {csModel}{(laptop ? " · laptop" : "")}");
            return new SystemIdentity(csManufacturer, csModel, "System", laptop);
        }

        var (bbManufacturer, bbProduct) = QueryOne("Win32_BaseBoard", "Manufacturer", "Product");
        if (!IsPlaceholderIdentity(bbManufacturer) && !IsPlaceholderIdentity(bbProduct))
        {
            log?.Report($"> motherboard identity · {bbManufacturer} {bbProduct}");
            return new SystemIdentity(bbManufacturer, bbProduct, "Motherboard");
        }

        log?.Report("  no recognizable system/motherboard identity (placeholder BIOS strings)");
        return null;

        static (string, string) QueryOne(string wmiClass, string field1, string field2)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT {field1}, {field2} FROM {wmiClass}");
                foreach (ManagementObject mo in searcher.Get())
                    return (mo[field1] as string ?? "", mo[field2] as string ?? "");
            }
            catch { /* non-fatal: identity is best-effort */ }
            return ("", "");
        }

        // Win32_ComputerSystem.PCSystemType: 2 = Mobile (laptop/notebook).
        static bool IsLaptop()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT PCSystemType FROM Win32_ComputerSystem");
                foreach (ManagementObject mo in searcher.Get())
                    return Convert.ToInt32(mo["PCSystemType"] ?? 0) == 2;
            }
            catch { /* non-fatal */ }
            return false;
        }
    }

    /// <summary>
    /// The machine's raw "Manufacturer Model" string straight from Win32_ComputerSystem,
    /// with no placeholder filtering, computed identically to the restore script's
    /// <c>(Get-CimInstance Win32_ComputerSystem)</c> check, so the model-lock compares
    /// apples to apples on the same PC. Empty string if it can't be read.
    /// </summary>
    public static string RawMachineIdentity()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in s.Get())
                return ((mo["Manufacturer"] as string ?? "") + " " + (mo["Model"] as string ?? "")).Trim();
        }
        catch { /* non-fatal: model-lock simply won't be applied */ }
        return "";
    }

    /// <summary>Full system identity (maker/model/type/OS/CPU/RAM), mirroring the values
    /// Windows System Information shows. Best-effort; any field may be blank.</summary>
    public static Task<SystemInfo> ScanSystemInfoAsync(IProgress<string>? log = null)
        => Task.Run(() => ScanSystemInfo(log));

    private static SystemInfo ScanSystemInfo(IProgress<string>? log)
    {
        string manu = "", model = "", type = "PC", os = "", cpu = "", ram = "";

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model, PCSystemType, TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in s.Get())
            {
                manu = mo["Manufacturer"] as string ?? "";
                model = mo["Model"] as string ?? "";
                type = Convert.ToInt32(mo["PCSystemType"] ?? 0) switch
                {
                    1 => "Desktop", 2 => "Laptop", 3 => "Workstation", 4 => "Server", _ => "PC",
                };
                if (mo["TotalPhysicalMemory"] is { } tpm && ulong.TryParse(tpm.ToString(), out var bytes))
                    ram = $"{Math.Round(bytes / 1073741824.0)} GB RAM";
                break;
            }
        }
        catch { /* non-fatal */ }

        // DIY desktops report placeholder maker/model, fall back to the motherboard.
        if (IsPlaceholderIdentity(manu) || IsPlaceholderIdentity(model))
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
                foreach (ManagementObject mo in s.Get())
                {
                    var bm = mo["Manufacturer"] as string ?? "";
                    var bp = mo["Product"] as string ?? "";
                    if (IsPlaceholderIdentity(manu) && !IsPlaceholderIdentity(bm)) manu = bm;
                    if (IsPlaceholderIdentity(model) && !IsPlaceholderIdentity(bp)) model = bp;
                    break;
                }
            }
            catch { }
        }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in s.Get())
            {
                os = (mo["Caption"] as string ?? "").Replace("Microsoft ", "").Trim();
                var build = mo["BuildNumber"] as string ?? "";
                if (!string.IsNullOrEmpty(build)) os += $" (build {build})";
                break;
            }
        }
        catch { }

        int cores = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor");
            foreach (ManagementObject mo in s.Get())
            {
                cpu = (mo["Name"] as string ?? "").Trim();
                if (mo["NumberOfCores"] is { } nc && int.TryParse(nc.ToString(), out var c)) cores = c;
                break;
            }
        }
        catch { }

        // "Year the machine was made": derived from the CPU generation (the best ON-BOX signal). The BIOS
        // release date reports the LATEST-flashed BIOS, so it overstates newness on a machine that later got a
        // BIOS update (e.g. a 2017 laptop flashed to a 2019 BIOS), CpuYear reads the era from the CPU itself.
        var year = CpuYear.YearOf(cpu);

        if (IsPlaceholderIdentity(manu)) manu = "";
        if (IsPlaceholderIdentity(model)) model = "";
        log?.Report($"> system model · {(manu + " " + model).Trim()}");
        return new SystemInfo(manu, model, type, os, cpu, ram, year, cores);
    }

    private static List<ScannedDevice> Scan(IProgress<string>? log)
    {
        log?.Report("> init WMI session · root\\CIMV2");

        // 1) Installed driver versions, keyed by PNP DeviceID.
        var driverByDeviceId = new Dictionary<string, (string Version, string Date)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            log?.Report("> SELECT DeviceID, DriverVersion, DriverDate FROM Win32_PnPSignedDriver");
            using var drvSearcher = new ManagementObjectSearcher(
                "SELECT DeviceID, DriverVersion, DriverDate FROM Win32_PnPSignedDriver");
            foreach (ManagementObject mo in drvSearcher.Get())
            {
                var id = mo["DeviceID"] as string;
                if (string.IsNullOrEmpty(id)) continue;
                var ver = mo["DriverVersion"] as string ?? "";
                var date = FormatWmiDate(mo["DriverDate"] as string);
                driverByDeviceId[id] = (ver, date);
            }
        }
        catch (Exception ex)
        {
            log?.Report($"  warning: driver-version query failed ({ex.Message}), continuing without versions");
        }

        // 2) Hardware enumeration.
        log?.Report("> SELECT Name, DeviceID, HardwareID, PNPClass FROM Win32_PnPEntity");
        var results = new List<ScannedDevice>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // bus+ven+dev -> index in results

        // WMI can throw (damaged CIM repository / provider load failure) on the searcher itself or mid-
        // iteration. Guard it like the driver-version query above so the scan degrades to whatever was
        // collected rather than faulting the whole ScanAsync (HealthScanner/StabilityScanner do the same).
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, HardwareID, PNPClass FROM Win32_PnPEntity");

            foreach (ManagementObject mo in searcher.Get())
            {
                var pnpClass = mo["PNPClass"] as string ?? "";
                // Canonical spelling (Intel's audio stack reports "MEDIA"), so case-sensitive checks downstream agree.
                if (!InterestingClasses.TryGetValue(pnpClass, out var canonicalClass)) continue;
                pnpClass = canonicalClass;

                var hwids = mo["HardwareID"] as string[];
                if (hwids is null || hwids.Length == 0) continue;

                // The first hardware ID is the most specific.
                var raw = hwids[0];
                if (ParseHardwareId(raw) is not { } id) continue;
                var (bus, ven, dev, subVen, subDev) = id;

                var deviceId = mo["DeviceID"] as string ?? "";
                var (ver, date) = driverByDeviceId.TryGetValue(deviceId, out var d) ? d : ("", "");
                var name = mo["Name"] as string ?? raw;

                var key = $"{bus}|{ven}|{dev}";
                if (indexByKey.TryGetValue(key, out var existingIdx))
                {
                    // Same physical device enumerated again (e.g. a composite USB
                    // dongle also exposing a generic HID collection). Prefer whichever
                    // name is more specific/branded rather than a generic OS-supplied one.
                    var existing = results[existingIdx];
                    if (IsGenericName(existing.Name) && !IsGenericName(name))
                    {
                        existing.Name = name;
                        existing.PnpClass = pnpClass;
                    }
                    continue;
                }

                var device = new ScannedDevice
                {
                    Name = name,
                    PnpClass = pnpClass,
                    DeviceId = deviceId,
                    RawHardwareId = raw,
                    Bus = bus,
                    Vendor = ven,
                    Device = dev,
                    SubsysVendor = subVen,
                    SubsysDevice = subDev,
                    InstalledVersion = ver,
                    InstalledDate = date,
                };
                indexByKey[key] = results.Count;
                results.Add(device);
                log?.Report($"  found {bus}\\VEN_{ven}&DEV_{dev} · {device.Name}");
            }
        }
        catch (Exception ex)
        {
            log?.Report($"  warning: device enumeration failed ({ex.Message}), returning what was collected");
        }

        // Stable, readable ordering: GPUs first, then by class, then name.
        results.Sort((a, b) =>
        {
            static int Rank(string c) => c switch
            {
                "Display" => 0, "System" => 1, "Media" => 2,
                "Net" => 3, "Bluetooth" => 3,
                "USB" => 4, "HIDClass" => 4, "Mouse" => 4, "Keyboard" => 4,
                "HDC" => 5, "SCSIAdapter" => 5, _ => 9,
            };
            var r = Rank(a.PnpClass).CompareTo(Rank(b.PnpClass));
            return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        log?.Report($"> scan complete · {results.Count} devices enumerated");
        return results;
    }

    /// <summary>WMI dates arrive as "20260114000000.000000-000"; render as yyyy-MM-dd.</summary>
    private static string FormatWmiDate(string? wmi)
    {
        if (string.IsNullOrEmpty(wmi) || wmi.Length < 8) return "";
        return $"{wmi[..4]}-{wmi[4..6]}-{wmi[6..8]}";
    }
}
