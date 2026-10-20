using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Venquery.Models;

// ---------------------------------------------------------------
// Hardware scan results (from WMI)
// ---------------------------------------------------------------

public sealed class ScannedDevice
{
    public string Name { get; set; } = "";
    public string PnpClass { get; set; } = "";
    public string DeviceId { get; init; } = "";
    public string RawHardwareId { get; init; } = "";
    public string Bus { get; init; } = "";        // PCI | USB | HDAUDIO | ...
    public string Vendor { get; init; } = "";     // 4-hex VEN / VID
    public string Device { get; init; } = "";     // 4-hex DEV / PID
    public string InstalledVersion { get; set; } = "";
    public string InstalledDate { get; set; } = "";
}

// ---------------------------------------------------------------
// Driver definitions manifest (definitions.json)
// ---------------------------------------------------------------

public sealed class Manifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("updated")] public string Updated { get; set; } = "";
    [JsonPropertyName("manifestUrl")] public string? ManifestUrl { get; set; }
    [JsonPropertyName("devices")] public List<Definition> Devices { get; set; } = new();
    [JsonPropertyName("vendorFallbacks")] public List<VendorFallback> VendorFallbacks { get; set; } = new();
    [JsonPropertyName("systemVendors")] public List<SystemVendor> SystemVendors { get; set; } = new();
}

public sealed class Definition
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("bus")] public string Bus { get; set; } = "PCI";
    [JsonPropertyName("ven")] public string Ven { get; set; } = "";
    [JsonPropertyName("dev")] public List<string>? Dev { get; set; }          // null => any device from vendor
    [JsonPropertyName("pnpClass")] public string? PnpClass { get; set; }      // optional extra filter, e.g. "Display"
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("supportPage")] public string SupportPage { get; set; } = "";
    [JsonPropertyName("referer")] public string? Referer { get; set; }        // some vendors (AMD) require it
    [JsonPropertyName("versions")] public List<VersionEntry> Versions { get; set; } = new();
    [JsonPropertyName("optional")] public List<OptionalTool> Optional { get; set; } = new();

    // --- automatic direct-download resolution (optional) ---
    // "resolver" names a live-lookup strategy run at scan time (e.g. "nvidia-geforce")
    // that returns the current direct driver URL, so the link is never stale.
    [JsonPropertyName("resolver")] public string? Resolver { get; set; }
    // "directDownload" is a vendor-maintained "always latest" installer URL: the URL
    // itself never changes but the vendor keeps it pointed at the newest build.
    [JsonPropertyName("directDownload")] public DirectDownload? DirectDownload { get; set; }
}

/// <summary>A stable, vendor-maintained "always latest" direct installer URL.</summary>
public sealed class DirectDownload
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

public sealed class VendorFallback
{
    [JsonPropertyName("ven")] public string Ven { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("supportPage")] public string SupportPage { get; set; } = "";
}

/// <summary>
/// Matches a system/motherboard manufacturer NAME string (from Win32_ComputerSystem
/// or Win32_BaseBoard) rather than a PCI/USB hex vendor ID, a motherboard or a
/// prebuilt laptop chassis has no PnP hardware ID of its own.
/// </summary>
public sealed class SystemVendor
{
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = new();
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("supportPage")] public string SupportPage { get; set; } = "";
}

/// <summary>The identity of the whole machine, either an OEM system (laptop/prebuilt)
/// or, for a DIY desktop, its motherboard, resolved from WMI, not the PnP device list.</summary>
public sealed record SystemIdentity(string Manufacturer, string Model, string Category, bool IsLaptop = false);

/// <summary>The machine's identity as shown in Windows System Information: maker, model,
/// form factor, OS, CPU and memory. Used for the on-screen summary and the export header.
/// <paramref name="Year"/> is the machine's build year inferred from the CPU generation (see CpuYear);
/// <paramref name="Cores"/> is the CPU physical core count (0 = unknown). Both may be blank/0.</summary>
public sealed record SystemInfo(string Manufacturer, string Model, string Type, string Os, string Cpu, string Ram,
    string Year = "", int Cores = 0)
{
    public string ModelLine => string.Join(" ", new[] { Manufacturer, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string SpecLine => string.Join("  ·  ", new[] { Type, Os, Ram }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool HasAnything => !string.IsNullOrWhiteSpace(ModelLine) || !string.IsNullOrWhiteSpace(Os) || !string.IsNullOrWhiteSpace(Cpu);
    public string DisplayTitle => string.IsNullOrWhiteSpace(ModelLine) ? "This PC" : ModelLine;
    /// <summary>CPU with core count appended when known, e.g. "Intel Core i7-1260P (12 cores)".</summary>
    public string CpuDetailed => Cores > 0 && !string.IsNullOrWhiteSpace(Cpu) ? $"{Cpu} ({Cores} cores)" : Cpu;
}

public sealed class VersionEntry
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }                // direct file; null => support page only
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }          // expected checksum if published

    // True when "Version" is a real version number that can be compared to what's
    // installed (e.g. NVIDIA 610.74). False for tool installers like Intel DSA,
    // Razer Synapse, etc. whose "version" is a product name, not a number, for
    // those we must NOT claim "update available", because we can't actually tell.
    [JsonIgnore] public bool Comparable { get; set; } = true;

    public override string ToString()
        => string.IsNullOrEmpty(Notes) ? $"{Version}  ·  {Date}" : $"{Version}  ·  {Date}  ·  {Notes}";
}

public sealed class OptionalTool
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

// ---------------------------------------------------------------
// Row view-model bound to the results table
// ---------------------------------------------------------------

public sealed class DeviceRow : INotifyPropertyChanged
{
    public ScannedDevice Hardware { get; }
    public Definition? Definition { get; }
    public VendorFallback? Fallback { get; }

    // Full history (all versions) and the filtered view the ComboBox actually shows.
    // By default only the newest is visible; the "show older versions" toggle reveals
    // the rest so casual users aren't overwhelmed but tinkerers can pick any build.
    public ObservableCollection<VersionEntry> Versions { get; } = new();
    public ObservableCollection<VersionEntry> VisibleVersions { get; } = new();
    public ObservableCollection<OptionalTool> OptionalTools { get; } = new();

    public int MatchCount { get; }
    public IReadOnlyList<ScannedDevice> OtherMatches { get; }

    public DeviceRow(ScannedDevice hw, Definition? def, VendorFallback? fallback,
        IReadOnlyList<ScannedDevice>? allMatches = null, IEnumerable<VersionEntry>? resolved = null)
    {
        Hardware = hw;
        Definition = def;
        Fallback = fallback;
        MatchCount = allMatches?.Count ?? 1;
        OtherMatches = allMatches is null
            ? Array.Empty<ScannedDevice>()
            : allMatches.Where(d => !ReferenceEquals(d, hw)).ToList();

        // Live-resolved / "always latest" direct downloads lead the version list
        // (newest first) so the top one is the default selection.
        if (resolved is not null)
            foreach (var v in resolved) Versions.Add(v);
        if (def is not null)
        {
            foreach (var v in def.Versions) Versions.Add(v);
            foreach (var t in def.Optional) OptionalTools.Add(t);
        }

        RebuildVisibleVersions();
        _selectedVersion = VisibleVersions.FirstOrDefault();
        _support = GpuSupport.For(hw.Vendor, hw.Name, hw.Device);
    }

    // Legacy-GPU status (NVIDIA / AMD / Intel). Null for current hardware, so the notice bar
    // only appears when the card really is on a legacy driver branch.
    private readonly GpuSupportInfo? _support;
    public string LegacyNotice => _support?.Notice ?? "";
    public bool HasLegacyNotice => !string.IsNullOrEmpty(LegacyNotice);

    private bool _showOlderVersions;
    public bool ShowOlderVersions
    {
        get => _showOlderVersions;
        set
        {
            if (_showOlderVersions == value) return;
            _showOlderVersions = value;
            RebuildVisibleVersions();
            OnChanged(nameof(OlderVersionCount));
        }
    }

    /// <summary>How many versions are hidden while the toggle is off (0 if ≤1 exists).</summary>
    public int OlderVersionCount => Math.Max(0, Versions.Count - 1);

    private void RebuildVisibleVersions()
    {
        var keep = _selectedVersion;
        VisibleVersions.Clear();
        IEnumerable<VersionEntry> src = _showOlderVersions ? Versions : Versions.Take(1);
        foreach (var v in src) VisibleVersions.Add(v);

        // Preserve the current pick if it is still visible, else fall back to newest.
        SelectedVersion = (keep is not null && VisibleVersions.Contains(keep))
            ? keep
            : VisibleVersions.FirstOrDefault();
    }

    // --- identity / display ---
    public string Category => Hardware.PnpClass;
    public string Name => Hardware.Name;
    public string HardwareId => Hardware.RawHardwareId;
    public string InstalledVersion =>
        string.IsNullOrEmpty(Hardware.InstalledVersion) ? "unknown" : Hardware.InstalledVersion;

    // --- installed driver date / age ---
    public bool HasInstalledDate => TryParseDate(out _);
    public string InstalledDateText
    {
        get
        {
            if (!TryParseDate(out var d)) return "";
            var age = AgeText(d);
            return $"{Hardware.InstalledDate}  ·  {age}";
        }
    }
    /// <summary>Amber hint when the installed driver is quite old (a useful signal even
    /// when we can't compare version numbers, e.g. Intel tool installers).</summary>
    public bool InstalledIsOld => TryParseDate(out var d) && (DateTime.Now - d).TotalDays > 730; // > ~2 years

    private bool TryParseDate(out DateTime date)
        => DateTime.TryParse(Hardware.InstalledDate, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date) && date.Year > 1980;

    private static string AgeText(DateTime d)
    {
        var days = (DateTime.Now - d).TotalDays;
        if (days < 45) return "this month";
        var years = (int)(days / 365.25);
        if (years >= 1) return years == 1 ? "1 yr old" : $"{years} yrs old";
        var months = (int)(days / 30.44);
        return months <= 1 ? "1 mo old" : $"{months} mo old";
    }

    public bool HasMultipleMatches => MatchCount > 1;
    public string MatchNote => MatchCount > 1
        ? $"+ {MatchCount - 1} more matching component{(MatchCount - 1 == 1 ? "" : "s")} on this PC"
        : "";

    public bool HasDefinition => Definition is not null;
    public bool HasVersions => Versions.Count > 0;

    public string Domain => Definition?.Domain ?? Fallback?.Domain ?? "";
    public string SupportPage => Definition?.SupportPage ?? Fallback?.SupportPage ?? "";
    public string SupportPageDisplay => StripScheme(SupportPage);
    public bool HasSupportPage => !string.IsNullOrEmpty(SupportPage);

    // --- WinUI master/detail helpers ---
    public bool HasVerifiedSource => !string.IsNullOrEmpty(Domain);
    public string VerifiedText => "Verified: " + Domain;
    public bool HasUpdateDot => StatusKind == "update";
    public bool NoDirectDownload => !HasDirectUrl;
    public string ListSubtitle => StatusKind switch
    {
        "update" => "Update available",
        "current" => "Up to date",
        "rollback" => "Rollback selected",
        "available" => "Driver available",
        "unknown" => "Driver available",
        _ => Category,
    };
    public IEnumerable<string> AllHardwareIds
    {
        get
        {
            yield return Hardware.RawHardwareId;
            foreach (var o in OtherMatches) yield return o.RawHardwareId;
        }
    }

    // --- selection state ---
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnChanged(); }
    }

    private VersionEntry? _selectedVersion;
    public VersionEntry? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            _selectedVersion = value;
            OnChanged();
            OnChanged(nameof(StatusText));
            OnChanged(nameof(StatusKind));
            OnChanged(nameof(DirectUrl));
            OnChanged(nameof(DirectUrlDisplay));
            OnChanged(nameof(HasDirectUrl));
            OnChanged(nameof(NoDirectDownload));
            OnChanged(nameof(HasUpdateDot));
            OnChanged(nameof(ListSubtitle));
        }
    }

    public bool HasDirectUrl => !string.IsNullOrEmpty(SelectedVersion?.Url);
    public string DirectUrl => SelectedVersion?.Url ?? "";
    public string DirectUrlDisplay => StripScheme(DirectUrl);

    // --- status ---

    /// <summary>
    /// The installed version in a form comparable to the offered versions. NVIDIA
    /// reports a Windows driver-store version (e.g. 32.0.15.6636); convert it to the
    /// marketing number (566.36). Other vendors already report a comparable string.
    /// </summary>
    private string ComparableInstalled
    {
        get
        {
            var v = Hardware.InstalledVersion;
            if (string.IsNullOrEmpty(v)) return "";
            if (string.Equals(Definition?.Resolver, "nvidia-geforce", StringComparison.OrdinalIgnoreCase))
            {
                var digits = new string(v.Where(char.IsDigit).ToArray());
                if (digits.Length >= 5)
                {
                    var last5 = digits[^5..];
                    return last5[..3] + "." + last5[3..];   // 32.0.15.6636 -> 566.36
                }
            }
            return v;
        }
    }

    public string StatusKind => KindFor(SelectedVersion);

    /// <summary>True when the NEWEST offered version is a real, comparable version newer than what's installed,
    /// independent of which version is picked in the dropdown (an older pick is a rollback choice, not "no update").
    /// Tool installers (Comparable = false) and unknown installed versions never count. Drives the summary tile.</summary>
    public bool NewestIsUpdate => KindFor(Versions.FirstOrDefault()) == "update";

    private string KindFor(VersionEntry? sel)
    {
        if (!HasDefinition) return "none";
        if (!HasVersions) return "page";
        if (sel is null) return "page";

        // Tool installers (Intel DSA, Razer Synapse, …) have no comparable version
        // number, so we can't honestly know if an update is needed. Present neutrally
        // rather than falsely claiming "update available".
        if (!sel.Comparable) return "available";

        var inst = ComparableInstalled;
        if (string.IsNullOrEmpty(inst)) return "unknown";

        // Numeric compare (works even if the installed build isn't in the offered
        // list, e.g. an old NVIDIA driver older than the most recent 30).
        var cmp = VersionMath.Compare(sel.Version, inst);
        if (cmp is null) return "unknown";      // couldn't parse either side
        if (cmp == 0) return "current";
        return cmp > 0 ? "update" : "rollback"; // selected newer => update; older => rollback
    }

    public string StatusText => StatusKind switch
    {
        "none" => "No definition, use vendor support page",
        "page" => "Browse official page for downloads",
        "available" => "◆ Latest driver available to download",
        "current" => "● You have the latest, matches installed",
        "rollback" => "↩ Older version selected (rollback)",
        "unknown" => "◆ Latest available, can't confirm your installed version",
        _ => "▲ Update available",
    };

    // --- download progress ---
    private double _progress;
    public double Progress { get => _progress; set { _progress = value; OnChanged(); } }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        set { _progressText = value; OnChanged(); OnChanged(nameof(HasProgress)); OnChanged(nameof(ProgressKind)); }
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set { _isDownloading = value; OnChanged(); OnChanged(nameof(HasProgress)); }
    }

    // The progress card stays visible after a download so the saved-file + signature
    // result isn't lost the instant IsDownloading flips off.
    public bool HasProgress => IsDownloading || !string.IsNullOrEmpty(ProgressText);
    public string ProgressKind =>
        ProgressText.StartsWith('✓') ? "ok" :
        (ProgressText.StartsWith('✕') || ProgressText.StartsWith('⚠')) ? "fail" : "busy";

    // --- helpers ---
    private static string StripScheme(string url)
        => url.Replace("https://", "").Replace("http://", "");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
