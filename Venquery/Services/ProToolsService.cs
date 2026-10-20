using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Venquery.Services;

// ---------------------------------------------------------------------------
// Pro Tools (every .NET 8 edition since 1.1.0), an admin console for servers, maintenance and troubleshooting.
//
// Posture (locked): Venquery elevates ONLY to *read*; it never changes the system silently. The deep read
// relaunches the app's own exe elevated, headless, purely to query Windows (WMI/registry/SMART) and hand
// back JSON, no window, no writes. Every "fix" is a generated, readable PowerShell script the user runs
// themselves behind confirm + UAC + a typed Y. No kernel driver is ever loaded (so no fan RPM / CPU-core
// temps, ACPI thermal zones are the safe ceiling).
//
// This whole file is compiled only into builds that define PROTOOLS (every .NET 8 edition; not Legacy).
// ---------------------------------------------------------------------------

// ---- read-result records (all serialized to JSON by the probe) ----
public sealed record SmartAttribute(int Id, string Name, int Current, int Worst, long Raw, bool Critical);

public sealed record DiskDeepRead(
    string Model, int? WearPercent, int? TemperatureC, long? PowerOnHours, string Serial = "",
    bool? PredictFailure = null, string WearSource = "", List<SmartAttribute>? Smart = null);

public sealed record NvmeHealth(
    string Model, int? CriticalWarning, int? SparePercent, int? TemperatureC,
    long? DataUnitsWritten, long? PowerOnHours, long? MediaErrors);

public sealed record ThermalZone(string Name, int TemperatureC);

public sealed record VolumeCrypto(string Drive, string Protection, int? EncryptionPercent);

public sealed record SecurityPosture(
    bool TpmPresent, bool? TpmEnabled, string TpmSpec, string SecureBoot, List<VolumeCrypto> BitLocker);

public sealed record HotfixInfo(string Id, string InstalledOn);

public sealed record ServicingInfo(
    bool RebootPending, List<string> RebootReasons, string Activation,
    string OsCaption, string OsBuild, DateTime? InstallDate, List<HotfixInfo> RecentHotfixes);

public sealed record ProblemDevice(string Name, string DeviceClass, int ErrorCode, string ErrorText);

public sealed record ServiceState(string Name, string DisplayName, string StartMode, string State);

public sealed record NetAdapter(
    string Description, string Mac, List<string> IpAddresses, string Gateway,
    List<string> DnsServers, bool DhcpEnabled);

public sealed record ProcInfo(string Name, long WorkingSetMb);

public sealed record FirewallState(bool? Domain, bool? Private, bool? Public);

public sealed record RdpState(bool Enabled, bool NlaRequired);

public sealed record VolumeSpace(string Drive, string Label, long FreeBytes, long TotalBytes, bool Low);

public sealed record DeepReadResult(
    List<DiskDeepRead> Disks, List<NvmeHealth> Nvme, List<ThermalZone> Thermals,
    SecurityPosture? Security, ServicingInfo? Servicing, List<ProblemDevice> ProblemDevices,
    List<ServiceState> BadServices, List<NetAdapter> Network,
    List<ProcInfo> TopProcesses, FirewallState? Firewall, RdpState? Rdp, List<VolumeSpace> Volumes,
    bool Elevated);

public sealed record RepairWriteResult(bool Ok, string Error, string ScriptPath);

/// <summary>A guarded maintenance/repair action. Venquery WRITES the script; the user runs it.</summary>
public sealed record FixDefinition(string Id, string Title, string Detail, bool NeedsVolume = false);

/// <summary>A tickable row wrapping a <see cref="FixDefinition"/> for the multi-select maintenance list.</summary>
public sealed class FixRow : System.ComponentModel.INotifyPropertyChanged
{
    public required FixDefinition Fix { get; init; }
    public string Id => Fix.Id;
    public string Title => Fix.Title;
    public string Detail => Fix.Detail;
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public static class ProToolsService
{
    private const string ProbeFlag = "--health-probe";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // ==================================================================
    //  Elevated deep read, main-app side (self-relaunch, one UAC prompt)
    // ==================================================================

    public static async Task<DeepReadResult?> RunElevatedDeepReadAsync()
    {
        var outFile = Path.Combine(Path.GetTempPath(), "venq-deepread-" + Guid.NewGuid().ToString("N") + ".json");
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;
        try
        {
            var psi = new ProcessStartInfo(exe, $"{ProbeFlag} \"{outFile}\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            if (p is null) return null;
            await p.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // UAC declined
        catch { return null; }

        try
        {
            if (!File.Exists(outFile)) return null;
            return JsonSerializer.Deserialize<DeepReadResult>(File.ReadAllText(outFile), JsonOpts);
        }
        catch { return null; }
        finally { try { File.Delete(outFile); } catch { } }
    }

    // ==================================================================
    //  Headless probe, elevated-instance side
    // ==================================================================

    public static bool TryRunProbe(string[] args)
    {
        if (args is null || args.Length < 2 || !string.Equals(args[0], ProbeFlag, StringComparison.OrdinalIgnoreCase))
            return false;
        try { File.WriteAllText(args[1], JsonSerializer.Serialize(ReadDeep(), JsonOpts)); }
        catch
        {
            try { File.WriteAllText(args[1], JsonSerializer.Serialize(Empty(IsAdmin()), JsonOpts)); } catch { }
        }
        return true;
    }

    private static DeepReadResult Empty(bool elevated) =>
        new(new(), new(), new(), null, null, new(), new(), new(), new(), null, null, new(), elevated);

    /// <summary>Collect every read-only admin signal. Also callable directly (e.g. an already-elevated CLI
    /// process), each collector is independently guarded so one failure never sinks the rest.</summary>
    public static DeepReadResult ReadDeep()
    {
        var disks = Safe(ReadDisks, new List<DiskDeepRead>());
        var nvme = Safe(ReadNvme, new List<NvmeHealth>());
        var thermals = Safe(ReadThermals, new List<ThermalZone>());
        var security = Safe<SecurityPosture?>(ReadSecurity, null);
        var servicing = Safe<ServicingInfo?>(ReadServicing, null);
        var problems = Safe(ReadProblemDevices, new List<ProblemDevice>());
        var badsvc = Safe(ReadBadServices, new List<ServiceState>());
        var net = Safe(ReadNetwork, new List<NetAdapter>());
        var procs = Safe(ReadTopProcesses, new List<ProcInfo>());
        var fw = Safe<FirewallState?>(ReadFirewall, null);
        var rdp = Safe<RdpState?>(ReadRdp, null);
        var vols = Safe(ReadVolumes, new List<VolumeSpace>());
        return new(disks, nvme, thermals, security, servicing, problems, badsvc, net, procs, fw, rdp, vols, IsAdmin());
    }

    private static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

    // ---- storage: reliability counter + raw ATA SMART ----------------

    private static readonly Dictionary<int, string> SmartNames = new()
    {
        [0x01] = "Read error rate", [0x05] = "Reallocated sectors", [0x09] = "Power-on hours",
        [0x0A] = "Spin retry count", [0x0C] = "Power cycle count", [0xA9] = "Wear range delta",
        [0xAD] = "Wear leveling count", [0xAF] = "Program fail count", [0xB1] = "Wear leveling",
        [0xB4] = "Unused reserved blocks", [0xB7] = "SATA downshift", [0xBB] = "Uncorrectable errors",
        [0xBE] = "Airflow temperature", [0xC2] = "Temperature", [0xC5] = "Pending sectors",
        [0xC6] = "Uncorrectable sectors", [0xC7] = "CRC error count", [0xE7] = "SSD life left",
        [0xE8] = "Available reserved space", [0xE9] = "Media wearout / NAND writes",
        [0xF1] = "Host writes (LBAs)", [0xF2] = "Host reads (LBAs)",
    };
    private static readonly int[] CriticalIds = { 0x05, 0xC5, 0xC6, 0xBB };
    private static readonly int[] WearIds = { 0xE7, 0xE9, 0xB4, 0xB1, 0xAD, 0xE8 };

    private static List<DiskDeepRead> ReadDisks()
    {
        var result = new List<DiskDeepRead>();

        // SMART payloads + predict-failure keyed by PNP device id.
        var smartData = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var predict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var wmi = new ManagementScope(@"\\.\root\WMI"); wmi.Connect();
            using (var s = new ManagementObjectSearcher(wmi, new ObjectQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData")))
                foreach (ManagementObject o in s.Get()) using (o)
                {
                    var key = NormKey(o["InstanceName"] as string);
                    if (o["VendorSpecific"] is byte[] vs && key.Length > 0) smartData[key] = vs;
                }
            using (var s = new ManagementObjectSearcher(wmi, new ObjectQuery("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus")))
                foreach (ManagementObject o in s.Get()) using (o)
                {
                    var key = NormKey(o["InstanceName"] as string);
                    if (key.Length > 0 && o["PredictFailure"] is { } pf) predict[key] = Convert.ToBoolean(pf);
                }
        }
        catch { /* SMART not exposed */ }

        // Reliability counters (wear/temp/hours) keyed by serial, from the Storage provider.
        var reliBySerial = new Dictionary<string, (int? wear, int? temp, long? hours)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var st = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage"); st.Connect();
            using var s = new ManagementObjectSearcher(st, new ObjectQuery("SELECT SerialNumber, FriendlyName FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject pd in s.Get()) using (pd)
            {
                var serial = (pd["SerialNumber"] as string ?? "").Trim();
                try
                {
                    using var rc = new ManagementObjectSearcher(st, new ObjectQuery(
                        $"ASSOCIATORS OF {{{pd.Path.RelativePath}}} WHERE ResultClass=MSFT_StorageReliabilityCounter"));
                    foreach (ManagementObject c in rc.Get()) using (c)
                    {
                        int? wear = c["Wear"] is { } w ? Convert.ToInt32(w) : null;
                        int? temp = c["Temperature"] is { } t && Convert.ToInt32(t) > 0 ? Convert.ToInt32(t) : null;
                        long? hours = c["PowerOnHours"] is { } h && Convert.ToInt64(h) > 0 ? Convert.ToInt64(h) : null;
                        if (serial.Length > 0) reliBySerial[serial] = (wear, temp, hours);
                        break;
                    }
                }
                catch { }
            }
        }
        catch { }

        // Anchor on Win32_DiskDrive (present on every Windows incl. Server/Win7).
        using var dd = new ManagementObjectSearcher("SELECT Model, SerialNumber, PNPDeviceID FROM Win32_DiskDrive");
        foreach (ManagementObject d in dd.Get()) using (d)
        {
            var model = (d["Model"] as string ?? "Disk").Trim();
            var serial = (d["SerialNumber"] as string ?? "").Trim();
            var pnp = NormKey(d["PNPDeviceID"] as string);

            List<SmartAttribute>? attrs = null;
            int? wear = null, temp = null; long? hours = null; string wearSrc = "";
            bool? predictFail = predict.TryGetValue(pnp, out var pf) ? pf : null;

            if (smartData.TryGetValue(pnp, out var raw))
            {
                attrs = ParseSmart(raw);
                foreach (var a in attrs)
                {
                    if (a.Id == 0x09 && a.Raw > 0) hours = a.Raw;
                    else if (a.Id == 0xC2 && (a.Raw & 0xFF) is > 0 and < 120) temp = (int)(a.Raw & 0xFF);
                }
                // Wear = 100 - normalized current of a recognized wear attribute (best-effort).
                foreach (var id in WearIds)
                {
                    var wa = attrs.FirstOrDefault(a => a.Id == id);
                    if (wa is not null && wa.Current is > 0 and <= 100)
                    { wear = 100 - wa.Current; wearSrc = $"SMART attr 0x{id:X2}"; break; }
                }
            }

            // Prefer the reliability counter where it has a value.
            if (reliBySerial.TryGetValue(serial, out var reli))
            {
                if (reli.wear is not null) { wear = reli.wear; wearSrc = "reliability counter"; }
                temp ??= reli.temp;
                hours ??= reli.hours;
            }

            result.Add(new DiskDeepRead(model, wear, temp, hours, serial, predictFail, wearSrc, attrs));
        }
        return result;
    }

    // ATA SMART READ DATA: 30 attributes of 12 bytes from offset 2. Entry: id, flags(2), current, worst,
    // raw(6 LE), reserved. Some drives prepend a 2-byte revision so we scan for the first plausible id.
    private static List<SmartAttribute> ParseSmart(byte[] data)
    {
        var list = new List<SmartAttribute>();
        if (data is null || data.Length < 362) return list;
        for (int i = 2; i + 12 <= 362; i += 12)
        {
            int id = data[i];
            if (id == 0) continue;
            int current = data[i + 3];
            int worst = data[i + 4];
            long rawv = 0;
            for (int b = 0; b < 6; b++) rawv |= (long)data[i + 5 + b] << (8 * b);
            var name = SmartNames.TryGetValue(id, out var n) ? n : $"Attribute 0x{id:X2}";
            bool crit = CriticalIds.Contains(id) && rawv > 0;
            list.Add(new SmartAttribute(id, name, current, worst, rawv, crit));
        }
        return list;
    }

    private static string NormKey(string? s) => (s ?? "").Trim().ToUpperInvariant();

    // ---- NVMe health log (best-effort DeviceIoControl) ---------------

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY_HDR { public int PropertyId; public int QueryType; public int ProtocolType; public int DataType; public int ProtocolDataRequestValue; public int ProtocolDataRequestSubValue; public int ProtocolDataOffset; public int ProtocolDataLength; public int FixedProtocolReturnData; public int Reserved0; public int Reserved1; public int Reserved2; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

    private static List<NvmeHealth> ReadNvme()
    {
        var list = new List<NvmeHealth>();
        // Map PhysicalDriveN → model, and only probe NVMe buses.
        var models = new Dictionary<int, string>();
        try
        {
            using var dd = new ManagementObjectSearcher("SELECT Index, Model, PNPDeviceID FROM Win32_DiskDrive");
            foreach (ManagementObject d in dd.Get()) using (d)
            {
                var pnp = (d["PNPDeviceID"] as string ?? "").ToUpperInvariant();
                if (!pnp.Contains("NVME")) continue;   // only NVMe drives expose the NVMe log
                if (d["Index"] is { } idx) models[Convert.ToInt32(idx)] = (d["Model"] as string ?? "NVMe").Trim();
            }
        }
        catch { return list; }

        foreach (var kv in models)
        {
            try
            {
                var h = CreateFile($@"\\.\PhysicalDrive{kv.Key}", 0x80000000 | 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                using (h)
                {
                    if (h.IsInvalid) continue;
                    const int hdr = 48, log = 512;
                    var buf = new byte[hdr + log];
                    var q = new STORAGE_PROPERTY_QUERY_HDR
                    {
                        PropertyId = 50 /*StorageDeviceProtocolSpecificProperty*/, QueryType = 0 /*Standard*/,
                        ProtocolType = 3 /*NVMe*/, DataType = 2 /*LogPage*/, ProtocolDataRequestValue = 2 /*SMART/health*/,
                        ProtocolDataOffset = hdr, ProtocolDataLength = log,
                    };
                    var inBuf = new byte[hdr + log];
                    var gch = GCHandle.Alloc(inBuf, GCHandleType.Pinned);
                    try { Marshal.StructureToPtr(q, gch.AddrOfPinnedObject(), false); } finally { gch.Free(); }
                    if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, inBuf.Length, buf, buf.Length, out _, IntPtr.Zero))
                        continue;
                    var d0 = hdr; // NVMe SMART/health log starts at the data offset
                    int crit = buf[d0 + 0];
                    int compTempK = buf[d0 + 1] | (buf[d0 + 2] << 8);
                    int? tempC = compTempK > 0 ? compTempK - 273 : null;
                    int spare = buf[d0 + 3];
                    long duw = BitConverter.ToInt64(buf, d0 + 48);         // data units written (low 64 of 128)
                    long poh = BitConverter.ToInt64(buf, d0 + 128);        // power on hours (low 64)
                    long mediaErr = BitConverter.ToInt64(buf, d0 + 160);   // media errors (low 64)
                    list.Add(new NvmeHealth(kv.Value, crit, spare, tempC, duw, poh, mediaErr));
                }
            }
            catch { /* drive didn't answer, skip */ }
        }
        return list;
    }

    // ---- thermal zones ----------------------------------------------

    private static List<ThermalZone> ReadThermals()
    {
        var list = new List<ThermalZone>();
        var scope = new ManagementScope(@"\\.\root\WMI"); scope.Connect();
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));
        foreach (ManagementObject o in s.Get()) using (o)
        {
            if (o["CurrentTemperature"] is not { } raw) continue;
            var c = (int)Math.Round(Convert.ToDouble(raw) / 10.0 - 273.15);
            if (c is <= 0 or > 150) continue;
            var name = (o["InstanceName"] as string ?? "Thermal zone").Trim();
            var cut = name.LastIndexOf('\\');
            if (cut >= 0 && cut < name.Length - 1) name = name.Substring(cut + 1);
            list.Add(new ThermalZone(name, c));
        }
        return list;
    }

    // ---- security posture -------------------------------------------

    private static SecurityPosture ReadSecurity()
    {
        bool tpmPresent = false; bool? tpmEnabled = null; string tpmSpec = "";
        try
        {
            var sc = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm"); sc.Connect();
            using var s = new ManagementObjectSearcher(sc, new ObjectQuery("SELECT * FROM Win32_Tpm"));
            foreach (ManagementObject o in s.Get()) using (o)
            {
                tpmPresent = true;
                tpmSpec = (o["SpecVersion"] as string ?? "").Split(',')[0].Trim();
                if (o["IsEnabled_InitialValue"] is { } en) tpmEnabled = Convert.ToBoolean(en);
                break;
            }
        }
        catch { }

        string secureBoot = "Unknown";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (k?.GetValue("UEFISecureBootEnabled") is int v) secureBoot = v == 1 ? "On" : "Off";
            else secureBoot = "Off / legacy BIOS";
        }
        catch { }

        var bl = new List<VolumeCrypto>();
        try
        {
            var sc = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption"); sc.Connect();
            using var s = new ManagementObjectSearcher(sc, new ObjectQuery("SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume"));
            foreach (ManagementObject o in s.Get()) using (o)
            {
                var drive = o["DriveLetter"] as string ?? "";
                var ps = o["ProtectionStatus"] is { } p ? Convert.ToInt32(p) : 2;
                var prot = ps switch { 1 => "Protected", 0 => "Off", _ => "Unknown" };
                int? pct = null;
                try
                {
                    var args = new object[] { 0, 0 };
                    o.InvokeMethod("GetConversionStatus", args);
                    if (args[1] is { } ep) pct = Convert.ToInt32(ep);
                }
                catch { }
                bl.Add(new VolumeCrypto(drive, prot, pct));
            }
        }
        catch { }

        return new SecurityPosture(tpmPresent, tpmEnabled, tpmSpec, secureBoot, bl);
    }

    // ---- servicing: reboot-pending, activation, OS, hotfixes --------

    private static ServicingInfo ReadServicing()
    {
        var reasons = new List<string>();
        bool KeyExists(string path) { try { using var k = Registry.LocalMachine.OpenSubKey(path); return k is not null; } catch { return false; } }
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) reasons.Add("Component servicing");
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) reasons.Add("Windows Update");
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (k?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) reasons.Add("Pending file rename");
        }
        catch { }

        string activation = "Unknown", caption = "", build = ""; DateTime? install = null;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, BuildNumber, Version, InstallDate FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                caption = (o["Caption"] as string ?? "").Trim();
                build = $"{o["Version"]} (build {o["BuildNumber"]})";
                if (o["InstallDate"]?.ToString() is { Length: > 0 } id) install = ManagementDateTimeConverter.ToDateTime(id);
                break;
            }
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                activation = (o["LicenseStatus"] is { } ls ? Convert.ToInt32(ls) : -1) switch
                { 1 => "Activated", 0 => "Unlicensed", 2 => "OOB grace", 3 => "OOT grace", 5 => "Notification", _ => "Other" };
                break;
            }
        }
        catch { }

        var hotfixes = new List<HotfixInfo>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering");
            foreach (ManagementObject o in s.Get()) using (o)
                hotfixes.Add(new HotfixInfo((o["HotFixID"] as string ?? "").Trim(), (o["InstalledOn"] as string ?? "").Trim()));
        }
        catch { }
        hotfixes = hotfixes.OrderByDescending(h => DateTime.TryParse(h.InstalledOn, out var dt) ? dt : DateTime.MinValue).Take(5).ToList();

        return new ServicingInfo(reasons.Count > 0, reasons, activation, caption, build, install, hotfixes);
    }

    // ---- diagnostics: problem devices, bad services, network --------

    private static string DeviceError(int code) => code switch
    {
        1 => "not configured", 3 => "driver corrupt / low memory", 10 => "cannot start", 12 => "no free resources",
        14 => "needs restart", 18 => "reinstall drivers", 19 => "registry corrupt", 22 => "disabled",
        28 => "no driver installed", 31 => "driver failed", 37 => "driver returned failure", 39 => "driver missing/corrupt",
        43 => "stopped (reported a problem)", 45 => "not connected", _ => $"code {code}",
    };

    private static List<ProblemDevice> ReadProblemDevices()
    {
        var list = new List<ProblemDevice>();
        using var s = new ManagementObjectSearcher("SELECT Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        foreach (ManagementObject o in s.Get()) using (o)
        {
            var code = o["ConfigManagerErrorCode"] is { } c ? Convert.ToInt32(c) : 0;
            if (code == 0) continue;
            list.Add(new ProblemDevice((o["Name"] as string ?? "(unnamed)").Trim(),
                (o["PNPClass"] as string ?? "").Trim(), code, DeviceError(code)));
        }
        return list;
    }

    private static List<ServiceState> ReadBadServices()
    {
        var list = new List<ServiceState>();
        using var s = new ManagementObjectSearcher("SELECT Name, DisplayName, StartMode, State FROM Win32_Service WHERE StartMode='Auto' AND State<>'Running'");
        foreach (ManagementObject o in s.Get()) using (o)
            list.Add(new ServiceState((o["Name"] as string ?? "").Trim(), (o["DisplayName"] as string ?? "").Trim(),
                (o["StartMode"] as string ?? "").Trim(), (o["State"] as string ?? "").Trim()));
        return list;
    }

    private static List<NetAdapter> ReadNetwork()
    {
        var list = new List<NetAdapter>();
        using var s = new ManagementObjectSearcher("SELECT Description, MACAddress, IPAddress, DefaultIPGateway, DNSServerSearchOrder, DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=true");
        foreach (ManagementObject o in s.Get()) using (o)
        {
            var ips = (o["IPAddress"] as string[])?.ToList() ?? new List<string>();
            var gw = (o["DefaultIPGateway"] as string[])?.FirstOrDefault() ?? "";
            var dns = (o["DNSServerSearchOrder"] as string[])?.ToList() ?? new List<string>();
            list.Add(new NetAdapter((o["Description"] as string ?? "").Trim(), (o["MACAddress"] as string ?? "").Trim(),
                ips, gw, dns, o["DHCPEnabled"] is { } dh && Convert.ToBoolean(dh)));
        }
        return list;
    }

    private static List<ProcInfo> ReadTopProcesses()
    {
        return Process.GetProcesses()
            .Select(p => { try { return new ProcInfo(p.ProcessName, p.WorkingSet64 / (1024 * 1024)); } catch { return null; } })
            .Where(p => p is not null).Select(p => p!)
            .OrderByDescending(p => p.WorkingSetMb).Take(5).ToList();
    }

    private static bool? FwOn(string profile)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}");
            return k?.GetValue("EnableFirewall") is int v ? v == 1 : (bool?)null;
        }
        catch { return null; }
    }

    private static FirewallState ReadFirewall() =>
        new(FwOn("DomainProfile"), FwOn("StandardProfile"), FwOn("PublicProfile"));

    private static RdpState ReadRdp()
    {
        bool enabled = false, nla = false;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
            if (k?.GetValue("fDenyTSConnections") is int deny) enabled = deny == 0;
        }
        catch { }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
            if (k?.GetValue("UserAuthentication") is int ua) nla = ua == 1;
        }
        catch { }
        return new RdpState(enabled, nla);
    }

    private static List<VolumeSpace> ReadVolumes()
    {
        var list = new List<VolumeSpace>();
        using var s = new ManagementObjectSearcher("SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
        foreach (ManagementObject o in s.Get()) using (o)
        {
            long total = o["Size"] is { } sz ? Convert.ToInt64(sz) : 0;
            if (total <= 0) continue;
            long free = o["FreeSpace"] is { } fs ? Convert.ToInt64(fs) : 0;
            list.Add(new VolumeSpace(o["DeviceID"] as string ?? "", o["VolumeName"] as string ?? "",
                free, total, free < 0.10 * total));
        }
        return list.OrderBy(v => v.Drive, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ==================================================================
    //  Maintenance / repair, guarded scripts (written, never auto-run)
    // ==================================================================

    /// <summary>The maintenance actions offered on the Pro Tools page. Each writes a readable, guarded
    /// script; the user runs it (confirm + UAC + typed Y).</summary>
    public static readonly IReadOnlyList<FixDefinition> Fixes = new[]
    {
        new FixDefinition("collect", "Collect diagnostic bundle",
            "Gathers systeminfo, msinfo32, event logs, gpresult, ipconfig and driver list into Downloads\\Venquery Diagnostics\\<PC model>\\<time>, a support bundle. Read-only."),
        new FixDefinition("restorepoint", "Create a restore point",
            "Checkpoint-Computer, a System Restore point. Tick this first, before any change."),
        new FixDefinition("sfc", "System file check (SFC + DISM)",
            "Runs DISM /RestoreHealth then sfc /scannow, Windows' own repair for corrupt system files."),
        new FixDefinition("diskcleanup", "Disk Cleanup (Windows' own)",
            "Runs Windows Disk Cleanup (cleanmgr) on safe items only: temporary files, thumbnail cache, Delivery Optimization and Windows Update leftovers, error reports and shader caches. Never touches the Recycle Bin, your Downloads folder or previous Windows installations. Shows the space freed."),
        new FixDefinition("cleanup", "Component store cleanup (DISM)",
            "DISM /StartComponentCleanup, reclaims space in the WinSxS component store. Safe, may take a while."),
        new FixDefinition("chkdsk", "Check disk (CHKDSK)",
            "chkdsk /scan on the chosen volume (online, read-only scan). Reports errors without locking the disk.",
            NeedsVolume: true),
        new FixDefinition("wureset", "Reset Windows Update",
            "Stops the update services, renames SoftwareDistribution + catroot2, and restarts them, the standard fix for a stuck Windows Update."),
        new FixDefinition("netreset", "Reset network stack",
            "netsh winsock reset, netsh int ip reset, ipconfig /flushdns, clears a corrupt TCP/IP / Winsock stack. Needs a reboot."),
        new FixDefinition("flushdns", "Flush DNS cache",
            "ipconfig /flushdns, clears the DNS resolver cache. Quick, harmless."),
        new FixDefinition("gpupdate", "Update Group Policy",
            "gpupdate /force, re-applies domain and local Group Policy immediately."),
        new FixDefinition("powerreport", "Power & battery reports",
            "powercfg /batteryreport, /energy and /sleepstudy, writes HTML diagnostics to your Downloads folder (read-only)."),
    };

    /// <summary>The Disk Cleanup handlers ticked for a run. Deliberately NOT included: "Recycle Bin" (the user's
    /// deleted files), "DownloadsFolder" (their Downloads), "Previous Installations" (Windows.old, needed to roll
    /// back an upgrade) and "Language Pack"/"Setup Log Files" style items that remove recovery options.</summary>
    internal static readonly string[] DiskCleanupHandlers =
    {
        "Temporary Files", "Temporary Setup Files", "Thumbnail Cache", "Delivery Optimization Files",
        "Update Cleanup", "Windows Error Reporting Files", "D3D Shader Cache", "Old ChkDsk Files",
        "Downloaded Program Files", "Internet Cache Files", "System error memory dump files",
        "System error minidump files", "Windows Upgrade Log Files",
    };

    /// <summary>Windows' documented way to automate Disk Cleanup: tick handlers by writing a StateFlags value under
    /// VolumeCaches, run "cleanmgr /sagerun:N", then remove the StateFlags again so nothing is left behind. Handlers
    /// that don't exist on this Windows build are skipped.</summary>
    private static string[] DiskCleanupCommands()
    {
        var list = string.Join(",", DiskCleanupHandlers.Select(h => "'" + PsLit(h) + "'"));
        return new[]
        {
            "$vc = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VolumeCaches'",
            "$flag = 'StateFlags0777'",
            "$before = (Get-PSDrive C).Free",
            "foreach ($h in @(" + list + ")) { if (Test-Path (Join-Path $vc $h)) { New-ItemProperty -Path (Join-Path $vc $h) -Name $flag -Value 2 -PropertyType DWord -Force | Out-Null } }",
            "Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\cleanmgr.exe') -ArgumentList '/sagerun:777' -Wait",
            "Get-ChildItem $vc | ForEach-Object { Remove-ItemProperty -Path $_.PSPath -Name $flag -ErrorAction SilentlyContinue }",
            "$freed = ((Get-PSDrive C).Free - $before) / 1MB",
            "Write-Host ('    Disk Cleanup freed about {0:N0} MB on C:' -f [Math]::Max(0, $freed))",
        };
    }

    // Execution order within a combined run: restore-point first (safety), report/collect last.
    private static int Rank(string id) => id switch
    {
        "restorepoint" => 0, "collect" => 8, "powerreport" => 9, "netreset" => 7, _ => 5,
    };

    /// <summary>Writes ONE guarded script running all the chosen actions in order. <paramref name="autoDelete"/>
    /// makes the script remove itself once finished. Venquery only writes it, the user runs it.</summary>
    public static RepairWriteResult WriteFixScript(IReadOnlyList<string> ids, string outputFolder, string volume = "C:", bool autoDelete = false)
    {
        try
        {
            var chosen = ids.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => Fixes.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)))
                .Where(f => f is not null).Select(f => f!)
                .OrderBy(f => Rank(f.Id)).ToList();
            if (chosen.Count == 0) return new RepairWriteResult(false, "No valid actions selected.", "");
            // The volume is typed by the user and lands on a command line in a script that runs as Administrator,
            // so only a plain drive letter is accepted (anything else could smuggle in a second command).
            var vol = NormalizeVolume(volume);
            if (vol is null && chosen.Any(f => f.NeedsVolume))
                return new RepairWriteResult(false, "The check-disk volume must be a drive letter, for example C:", "");
            volume = vol ?? "C:";
            Directory.CreateDirectory(outputFolder);
            var stem = chosen.Count == 1 ? $"venquery-{chosen[0].Id}" : "venquery-maintenance";
            var path = UniquePath(Path.Combine(outputFolder, stem + ".ps1"));
            File.WriteAllText(path, BuildCombinedScript(chosen, volume, autoDelete), new UTF8Encoding(true));
            return new RepairWriteResult(true, "", path);
        }
        catch (Exception ex) { return new RepairWriteResult(false, ex.Message, ""); }
    }

    /// <summary>"c", "C:", "c:\" → "C:"; anything that isn't a single drive letter → null.</summary>
    internal static string? NormalizeVolume(string? volume)
    {
        var v = (volume ?? "").Trim().TrimEnd('\\', '/');
        if (v.Length == 1) v += ":";
        return v.Length == 2 && v[1] == ':' && char.IsLetter(v[0]) && v[0] < 128 ? char.ToUpperInvariant(v[0]) + ":" : null;
    }

    private static string BuildCombinedScript(List<FixDefinition> fixes, string volume, bool autoDelete)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Edition.Current.DisplayName} - Maintenance run");
        sb.AppendLine($"# Generated {DateTime.Now:yyyy-MM-dd HH:mm} - review before running.");
        sb.AppendLine("# Actions: " + string.Join(", ", fixes.Select(f => f.Title)));
        sb.AppendLine("# Self-clean: " + (autoDelete ? "ON (this script deletes itself when finished)" : "off (script is kept)"));
        sb.AppendLine("#");
        sb.AppendLine("# You can run this yourself, or press \"Run\" in Venquery - it behaves the same, and nothing");
        sb.AppendLine("# runs until you confirm below. It uses Windows' own built-in tools.");
        sb.AppendLine("# Safety: (1) checks it is Administrator; (2) lists every action; (3) waits for a typed Y.");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine($"Write-Host '{Edition.Current.DisplayName} - Maintenance run' -ForegroundColor Cyan");
        sb.AppendLine();
        sb.AppendLine("# --- Guard 1: administrator ---");
        sb.AppendLine("$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)");
        sb.AppendLine("if (-not $isAdmin) { Write-Host '  NOT RUNNING AS ADMINISTRATOR - re-run from an Administrator terminal.' -ForegroundColor Red; Read-Host 'Press Enter to close'; exit 1 }");
        sb.AppendLine();
        sb.AppendLine("# --- What will run ---");
        sb.AppendLine($"Write-Host 'These {fixes.Count} action(s) will run, in order:' -ForegroundColor Cyan");
        int i = 1;
        foreach (var f in fixes)
        {
            sb.AppendLine($"Write-Host '   {i}. {PsLit(f.Title)}'");
            foreach (var line in FixCommands(f, volume).comment) sb.AppendLine($"Write-Host '        - {PsLit(line)}'");
            i++;
        }
        sb.AppendLine();
        sb.AppendLine("# --- Guard 2: confirmation ---");
        sb.AppendLine("$answer = Read-Host 'Run these now? Type Y to proceed'");
        sb.AppendLine("if ($answer -notmatch '^[Yy]') { Write-Host 'Cancelled - nothing was run.'; Read-Host 'Press Enter'; exit 0 }");
        sb.AppendLine();
        foreach (var f in fixes)
        {
            sb.AppendLine("Write-Host ''");
            sb.AppendLine($"Write-Host 'Running: {PsLit(f.Title)}' -ForegroundColor Cyan");
            sb.AppendLine("try {");
            foreach (var cmd in FixCommands(f, volume).commands) sb.AppendLine("    " + cmd);
            sb.AppendLine("} catch { Write-Host ('  [error] ' + $_.Exception.Message) -ForegroundColor Red }");
        }
        sb.AppendLine();
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'All selected actions finished. Review the output above.' -ForegroundColor Green");
        sb.AppendLine("Read-Host 'Press Enter to close'");
        if (autoDelete)
        {
            sb.AppendLine();
            sb.AppendLine("# Self-clean: remove this script now that it has finished (only ever its own file).");
            sb.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");
        }
        return sb.ToString();
    }

    private static (string[] comment, string[] commands) FixCommands(FixDefinition fix, string volume)
    {
        var vol = (volume ?? "C:").TrimEnd('\\');
        return fix.Id switch
        {
            "sfc" => (new[] { "DISM /Online /Cleanup-Image /RestoreHealth, then sfc /scannow" },
                      new[] { "DISM.exe /Online /Cleanup-Image /RestoreHealth", "sfc.exe /scannow" }),
            "cleanup" => (new[] { "DISM /Online /Cleanup-Image /StartComponentCleanup" },
                          new[] { "DISM.exe /Online /Cleanup-Image /StartComponentCleanup" }),
            "chkdsk" => (new[] { $"chkdsk {vol} /scan  (online read-only scan)" },
                         new[] { $"chkdsk.exe {vol} /scan" }),
            "wureset" => (new[] { "Stop wuauserv/bits/cryptsvc, rename SoftwareDistribution + catroot2, restart" },
                          new[] {
                              "Stop-Service wuauserv,bits,cryptsvc -Force -ErrorAction SilentlyContinue",
                              "Rename-Item $env:SystemRoot\\SoftwareDistribution SoftwareDistribution.old -ErrorAction SilentlyContinue",
                              "Rename-Item $env:SystemRoot\\System32\\catroot2 catroot2.old -ErrorAction SilentlyContinue",
                              "Start-Service wuauserv,bits,cryptsvc -ErrorAction SilentlyContinue" }),
            "netreset" => (new[] { "netsh winsock reset; netsh int ip reset; ipconfig /flushdns (reboot after)" },
                           new[] { "netsh winsock reset", "netsh int ip reset", "ipconfig /flushdns" }),
            "flushdns" => (new[] { "ipconfig /flushdns" }, new[] { "ipconfig /flushdns" }),
            "diskcleanup" => (new[] { "cleanmgr /sagerun on: " + string.Join(", ", DiskCleanupHandlers) + "; free space shown before and after" },
                              DiskCleanupCommands()),
            "gpupdate" => (new[] { "gpupdate /force" }, new[] { "gpupdate /force" }),
            "restorepoint" => (new[] { "Enable protection on C: and create a restore point" },
                               new[] {
                                   "Enable-ComputerRestore -Drive 'C:\\' -ErrorAction SilentlyContinue",
                                   "Checkpoint-Computer -Description 'Venquery manual restore point' -RestorePointType 'MODIFY_SETTINGS'" }),
            "powerreport" => (new[] { "powercfg /batteryreport, /energy, /sleepstudy -> Downloads" },
                              new[] {
                                  "$dl = Join-Path $env:USERPROFILE 'Downloads'",
                                  "powercfg /batteryreport /output \"$dl\\venquery-batteryreport.html\"",
                                  "powercfg /sleepstudy /output \"$dl\\venquery-sleepstudy.html\"",
                                  "powercfg /energy /output \"$dl\\venquery-energy.html\" /duration 30",
                                  "Write-Host \"Reports written to $dl\"" }),
            "collect" => (new[] { "systeminfo, msinfo32, event logs, gpresult, ipconfig, drivers -> Downloads\\Venquery Diagnostics\\<PC model>\\<time>" },
                          new[] {
                              "$cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue",
                              "$model = (\"$($cs.Manufacturer) $($cs.Model)\").Trim()",
                              "if ([string]::IsNullOrWhiteSpace($model)) { $model = $env:COMPUTERNAME }",
                              "foreach ($ch in [System.IO.Path]::GetInvalidFileNameChars()) { $model = $model.Replace($ch,'_') }",
                              "$dl = Join-Path $env:USERPROFILE 'Downloads'",
                              "$dest = Join-Path $dl ('Venquery Diagnostics\\' + $model + '\\' + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))",
                              "New-Item -ItemType Directory -Force $dest | Out-Null",
                              "systeminfo | Out-File (Join-Path $dest 'systeminfo.txt')",
                              "ipconfig /all | Out-File (Join-Path $dest 'ipconfig.txt')",
                              "driverquery /v | Out-File (Join-Path $dest 'drivers.txt')",
                              "cmd /c gpresult /h \"$dest\\gpresult.html\" /f",
                              "cmd /c wevtutil epl System \"$dest\\System.evtx\" /ow:true",
                              "cmd /c wevtutil epl Application \"$dest\\Application.evtx\" /ow:true",
                              "cmd /c msinfo32 /report \"$dest\\msinfo32.txt\"",
                              "Write-Host \"Diagnostic bundle written to: $dest\" -ForegroundColor Green" }),
            _ => (new[] { fix.Title }, Array.Empty<string>()),
        };
    }

    private static string PsLit(string s) => PsText.Lit(s);

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var c = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(c)) return c;
        }
    }

    // ==================================================================
    //  Text rendering (shared by the CLI --admin report)
    // ==================================================================

    public static string ToText(DeepReadResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ADMIN DEEP READ" + (r.Elevated ? "" : "  (NOT elevated, values may be incomplete)"));
        sb.AppendLine(new string('-', 60));

        sb.AppendLine("Storage & SMART");
        foreach (var d in r.Disks)
        {
            sb.AppendLine($"  {d.Model}{(d.PredictFailure == true ? "   *** SMART PREDICTS FAILURE ***" : "")}");
            var bits = new List<string> {
                d.WearPercent is { } w ? $"{w}% life used{(string.IsNullOrEmpty(d.WearSource) ? "" : $" ({d.WearSource})")}" : "wear: n/a",
                d.TemperatureC is { } t ? $"{t} °C" : "temp: n/a",
                d.PowerOnHours is { } h ? $"{h:n0} power-on hrs" : "hours: n/a" };
            sb.AppendLine("    " + string.Join("  ·  ", bits));
            if (d.Smart is { Count: > 0 })
                foreach (var a in d.Smart.Where(a => a.Critical))
                    sb.AppendLine($"    ! {a.Name} (0x{a.Id:X2}) raw={a.Raw}");
        }
        if (r.Nvme.Count > 0)
        {
            sb.AppendLine("NVMe health");
            foreach (var n in r.Nvme)
                sb.AppendLine($"  {n.Model}: warn=0x{n.CriticalWarning:X2}  spare={n.SparePercent}%  {n.TemperatureC} °C  poh={n.PowerOnHours}  mediaErr={n.MediaErrors}");
        }
        if (r.Thermals.Count > 0)
            sb.AppendLine("Thermal: " + string.Join("  ·  ", r.Thermals.Select(z => $"{z.Name} {z.TemperatureC}°C")));

        if (r.Security is { } s)
        {
            sb.AppendLine("Security posture");
            sb.AppendLine($"  TPM: {(s.TpmPresent ? $"present{(s.TpmEnabled == true ? ", enabled" : "")} (spec {s.TpmSpec})" : "not present")}  ·  Secure Boot: {s.SecureBoot}");
            foreach (var v in s.BitLocker)
                sb.AppendLine($"  BitLocker {v.Drive} {v.Protection}{(v.EncryptionPercent is { } p ? $" ({p}%)" : "")}");
        }
        if (r.Servicing is { } sv)
        {
            sb.AppendLine("Servicing");
            sb.AppendLine($"  OS: {sv.OsCaption} {sv.OsBuild}  ·  activation: {sv.Activation}");
            sb.AppendLine($"  Reboot pending: {(sv.RebootPending ? "YES, " + string.Join(", ", sv.RebootReasons) : "no")}");
            if (sv.RecentHotfixes.Count > 0)
                sb.AppendLine("  Recent hotfixes: " + string.Join(", ", sv.RecentHotfixes.Select(h => h.Id)));
        }
        if (r.ProblemDevices.Count > 0)
        {
            sb.AppendLine("Problem devices");
            foreach (var p in r.ProblemDevices) sb.AppendLine($"  {p.Name} [{p.DeviceClass}], {p.ErrorText}");
        }
        if (r.BadServices.Count > 0)
        {
            sb.AppendLine("Auto services not running");
            foreach (var b in r.BadServices) sb.AppendLine($"  {b.DisplayName} ({b.Name}), {b.State}");
        }
        if (r.Network.Count > 0)
        {
            sb.AppendLine("Network");
            foreach (var n in r.Network)
                sb.AppendLine($"  {n.Description}: {string.Join(", ", n.IpAddresses)}  gw={n.Gateway}  dns={string.Join(",", n.DnsServers)}  dhcp={(n.DhcpEnabled ? "on" : "off")}");
        }
        if (r.Volumes.Count > 0)
        {
            sb.AppendLine("Volumes");
            foreach (var v in r.Volumes)
                sb.AppendLine($"  {v.Drive} {(v.TotalBytes > 0 ? $"{v.FreeBytes / 1073741824.0:0.0} GB free of {v.TotalBytes / 1073741824.0:0.0} GB" : "")}{(v.Low ? "  ← low" : "")}");
        }
        if (r.Firewall is { } fw)
            sb.AppendLine($"Firewall: Domain={FwTxt(fw.Domain)}  Private={FwTxt(fw.Private)}  Public={FwTxt(fw.Public)}");
        if (r.Rdp is { } rd)
            sb.AppendLine($"RDP: {(rd.Enabled ? "enabled" : "disabled")}{(rd.Enabled ? $" (NLA {(rd.NlaRequired ? "required" : "off")})" : "")}");
        if (r.TopProcesses.Count > 0)
            sb.AppendLine("Top processes (RAM): " + string.Join("  ·  ", r.TopProcesses.Select(p => $"{p.Name} {p.WorkingSetMb} MB")));
        return sb.ToString();
    }

    private static string FwTxt(bool? b) => b is null ? "?" : b.Value ? "on" : "off";
}
