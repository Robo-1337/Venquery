using System.Management;
using System.Text.Json;

namespace Venquery.Services;

// ---------------------------------------------------------------------------
// The machine hardware fingerprint used to hardware-LOCK a restore to the PC it
// was captured from. This is the C# equivalent of the PowerShell 4-tuple that
// DriverBackupService.RestoreScriptLines() embeds and enforces (make/model, CPU,
// motherboard, GPU) - keep the four fields and the way they are read in sync with
// that script so both agree on what "the same machine" means.
//
// Callers use this to refuse restoring onto a mismatched machine. It is deliberately fail-closed: if ANY field can't be read
// or doesn't match, Matches() returns false and the caller refuses (unless the
// user has explicitly opted into cross-machine restore). Erring toward refusal is
// safe; pushing one machine's drivers onto another is the real risk.
// ---------------------------------------------------------------------------

/// <summary>A machine's hardware identity: make/model, CPU, motherboard, and the sorted GPU list.
/// The same 4-tuple the offline driver restore locks against.</summary>
public sealed record MachineFingerprint
{
    public string Model { get; init; } = "";
    public string Cpu { get; init; } = "";
    public string Board { get; init; } = "";
    public string Gpu { get; init; } = "";

    /// <summary>Read this machine's fingerprint from WMI, computed identically to the driver-restore
    /// PowerShell so the two agree. Any field that can't be read comes back empty (which makes
    /// <see cref="Matches"/> fail-closed).</summary>
    public static MachineFingerprint Compute()
    {
        var model = QueryJoin("Win32_ComputerSystem", "Manufacturer", "Model");
        // The PS script falls back to the computer name when make/model is blank; mirror that.
        if (string.IsNullOrWhiteSpace(model)) model = Environment.MachineName;

        return new MachineFingerprint
        {
            Model = model,
            Cpu = QueryFirst("Win32_Processor", "Name"),
            Board = QueryJoin("Win32_BaseBoard", "Manufacturer", "Product"),
            Gpu = QueryGpuList(),
        };
    }

    /// <summary>True only if this fingerprint matches <paramref name="other"/> on every field. Fail-closed:
    /// any field empty/unreadable on either side, or any difference, returns false. Case-insensitive, to
    /// match PowerShell's <c>-ne</c> comparison in the restore script.</summary>
    public bool Matches(MachineFingerprint? other)
    {
        if (other is null) return false;
        return FieldMatches(Model, other.Model)
            && FieldMatches(Cpu, other.Cpu)
            && FieldMatches(Board, other.Board)
            && FieldMatches(Gpu, other.Gpu);
    }

    private static bool FieldMatches(string a, string b)
        => !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A short, human-readable one-line summary.</summary>
    public string Summary() => $"{Model} · {Cpu}".Trim(' ', '·');

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static MachineFingerprint? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<MachineFingerprint>(json, JsonOpts); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // --- WMI helpers (mirror the Get-CimInstance reads in the driver restore script) --------------------

    private static string QueryJoin(string wmiClass, string field1, string field2)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT {field1}, {field2} FROM {wmiClass}");
            foreach (ManagementObject mo in s.Get())
                return ((mo[field1] as string ?? "") + " " + (mo[field2] as string ?? "")).Trim();
        }
        catch { /* non-fatal: an empty field makes the lock fail-closed */ }
        return "";
    }

    private static string QueryFirst(string wmiClass, string field)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT {field} FROM {wmiClass}");
            foreach (ManagementObject mo in s.Get())
                return (mo[field] as string ?? "").Trim();
        }
        catch { }
        return "";
    }

    // Every video controller name, trimmed, sorted, joined with "; " - identical to the PS:
    //   (Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name.Trim() } | Sort-Object) -join '; '
    private static string QueryGpuList()
    {
        try
        {
            var names = new List<string>();
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
            {
                var n = (mo["Name"] as string ?? "").Trim();
                if (n.Length > 0) names.Add(n);
            }
            names.Sort(StringComparer.Ordinal);
            return string.Join("; ", names);
        }
        catch { }
        return "";
    }
}
