using System.IO;
using System.Text;

namespace Venquery.Services;

// ---------------------------------------------------------------------------
// Offline driver backup, a SHARED service (all .NET 8 editions; not the net48 Legacy build, whose Win7
// pnputil has no /export-driver, the UI hides it there).
//
// Safety posture (this is the risky tool, so it is deliberately conservative):
//   * BACKUP is copy-only. Venquery WRITES a guarded script; when the user runs it (confirm + UAC + typed-Y)
//     it runs `pnputil /export-driver *`, which only COPIES third-party driver packages out of the driver
//     store into a folder. It installs nothing and changes no device.
//   * The backup script also drops, INTO the backup folder, a machine fingerprint (make/model, CPU, GPU,
//     motherboard) and a hardware-LOCKED restore script.
//   * RESTORE is never automatic and never done by Venquery. The generated restore script refuses to run
//     unless the current machine matches that fingerprint EXACTLY (fail-safe: any mismatch or unreadable
//     value => refuse, no override), then still requires admin + a typed Y before `pnputil /add-driver`.
//     Pushing drivers onto a different machine is the real risk; the lock is what prevents it.
// ---------------------------------------------------------------------------

public static class DriverBackupService
{
    /// <summary>Writes the guarded driver-BACKUP script to <paramref name="outputFolder"/> (Downloads).
    /// Venquery only writes it; the user runs it. On running it exports the drivers and, alongside them,
    /// writes the fingerprint + the hardware-locked restore script.</summary>
    /// <param name="exportDestination">When set (e.g. the Prepare-for-Reset wizard's
    /// <c>&lt;parent&gt;\Drivers</c>), the script exports there. When null it computes the default
    /// <c>Downloads\Driver Backup\&lt;model&gt;\&lt;time&gt;</c>.</param>
    public static RepairScriptResult WriteBackupScript(string outputFolder, string? exportDestination = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputFolder))
                return new RepairScriptResult(false, "No output folder.", "");
            Directory.CreateDirectory(outputFolder);
            var path = UniquePath(Path.Combine(outputFolder, "venquery-driver-backup.ps1"));
            File.WriteAllText(path, BuildBackupScript(exportDestination), new UTF8Encoding(true));
            return new RepairScriptResult(true, "", path);
        }
        catch (Exception ex) { return new RepairScriptResult(false, ex.Message, ""); }
    }

    private static string BuildBackupScript(string? exportDestination)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Venquery - Offline driver backup (export only)");
        sb.AppendLine($"# Generated {DateTime.Now:yyyy-MM-dd HH:mm} - review before running.");
        sb.AppendLine("#");
        sb.AppendLine("# What this does (copy-only, safe): exports this PC's third-party driver packages out of");
        sb.AppendLine("# the Windows driver store into a folder in Downloads. It INSTALLS NOTHING and changes no");
        sb.AppendLine("# device. Alongside the drivers it writes a fingerprint of THIS machine and a matching");
        sb.AppendLine("# restore script that will ONLY reinstall these drivers on a machine with the same make/");
        sb.AppendLine("# model, CPU, GPU and motherboard - so you cannot accidentally push them onto a different PC.");
        sb.AppendLine("# Safety: (1) checks it is Administrator; (2) tells you what will run; (3) waits for a typed Y.");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'Venquery - Offline driver backup (export only)' -ForegroundColor Cyan");
        sb.AppendLine();
        sb.AppendLine("# --- Guard 1: administrator ---");
        sb.AppendLine("$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)");
        sb.AppendLine("if (-not $isAdmin) { Write-Host '  NOT RUNNING AS ADMINISTRATOR - re-run from an Administrator terminal.' -ForegroundColor Red; Read-Host 'Press Enter to close'; exit 1 }");
        sb.AppendLine();
        // Compute the fingerprint of THIS machine (used to name the folder and to lock restore).
        sb.AppendLine("# --- Machine fingerprint (make/model, CPU, GPU, motherboard) ---");
        sb.AppendLine("$cs    = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue");
        sb.AppendLine("$bb    = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue");
        sb.AppendLine("$model = (\"$($cs.Manufacturer) $($cs.Model)\").Trim()");
        sb.AppendLine("$cpu   = ((Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1).Name).Trim()");
        sb.AppendLine("$board = (\"$($bb.Manufacturer) $($bb.Product)\").Trim()");
        sb.AppendLine("$gpu   = ((Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { $_.Name.Trim() } | Sort-Object) -join '; ')");
        sb.AppendLine("if ([string]::IsNullOrWhiteSpace($model)) { $model = $env:COMPUTERNAME }");
        sb.AppendLine("$fp = [pscustomobject]@{ Model = $model; Cpu = $cpu; Board = $board; Gpu = $gpu }");
        sb.AppendLine();
        sb.AppendLine("Write-Host 'This will export the drivers for:' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host \"   Machine : $model\"");
        sb.AppendLine("Write-Host \"   CPU     : $cpu\"");
        sb.AppendLine("Write-Host \"   GPU     : $gpu\"");
        sb.AppendLine("Write-Host \"   Board   : $board\"");
        sb.AppendLine("Write-Host '   Command : pnputil /export-driver *  (copy-only; installs nothing)'");
        sb.AppendLine();
        sb.AppendLine("# --- Guard 2: confirmation ---");
        sb.AppendLine("$answer = Read-Host 'Export the drivers now? Type Y to proceed'");
        sb.AppendLine("if ($answer -notmatch '^[Yy]') { Write-Host 'Cancelled - nothing was exported.'; Read-Host 'Press Enter'; exit 0 }");
        sb.AppendLine();
        // Destination folder. Either a fixed folder (the wizard's <parent>\Drivers) or the default
        // Downloads\Driver Backup\<model>\<timestamp>.
        if (!string.IsNullOrWhiteSpace(exportDestination))
        {
            sb.AppendLine($"$dest = '{PsLit(exportDestination!)}'");
        }
        else
        {
            sb.AppendLine("$safeModel = $model");
            sb.AppendLine("foreach ($ch in [System.IO.Path]::GetInvalidFileNameChars()) { $safeModel = $safeModel.Replace($ch,'_') }");
            sb.AppendLine("$dl   = Join-Path $env:USERPROFILE 'Downloads'");
            sb.AppendLine("$dest = Join-Path $dl ('Driver Backup\\' + $safeModel + '\\' + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))");
        }
        sb.AppendLine("New-Item -ItemType Directory -Force $dest | Out-Null");
        sb.AppendLine();
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host \"Exporting drivers to: $dest\" -ForegroundColor Cyan");
        sb.AppendLine("try { pnputil /export-driver * \"$dest\" } catch { Write-Host ('  [error] ' + $_.Exception.Message) -ForegroundColor Red }");
        sb.AppendLine();
        sb.AppendLine("# Save the fingerprint next to the drivers so the restore script can verify the machine.");
        sb.AppendLine("$fp | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dest 'machine-fingerprint.json') -Encoding UTF8");
        sb.AppendLine();
        // The hardware-locked restore script, emitted verbatim (single-quoted here-string = no expansion).
        sb.AppendLine("# --- Write the hardware-locked restore script into the backup folder ---");
        sb.AppendLine("$restore = @'");
        foreach (var line in RestoreScriptLines()) sb.AppendLine(line);
        sb.AppendLine("'@");
        sb.AppendLine("Set-Content -LiteralPath (Join-Path $dest 'restore-drivers.ps1') -Value $restore -Encoding UTF8");
        sb.AppendLine();
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'Done. Your drivers, a machine fingerprint, and a hardware-locked restore script are in:' -ForegroundColor Green");
        sb.AppendLine("Write-Host \"   $dest\" -ForegroundColor Green");
        sb.AppendLine("Write-Host 'Restore ONLY reinstalls on a machine that matches this one. Keep the whole folder together.' -ForegroundColor Yellow");
        sb.AppendLine("Read-Host 'Press Enter to close'");
        return sb.ToString();
    }

    // The restore script (static). It reads the fingerprint sitting next to it, recomputes the current
    // machine's fingerprint, and REFUSES unless every field matches. Only then, after admin + typed Y, does
    // it run pnputil /add-driver /install. No override exists, a mismatch always refuses.
    private static string[] RestoreScriptLines() => new[]
    {
        "# Venquery - Driver RESTORE (hardware-locked)",
        "# This reinstalls the drivers in this folder, but ONLY on a machine matching the one they came from.",
        "$ErrorActionPreference = 'Continue'",
        "Write-Host ''",
        "Write-Host 'Venquery - Driver restore (hardware-locked)' -ForegroundColor Cyan",
        "",
        "# Guard 1: administrator",
        "$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)",
        "if (-not $isAdmin) { Write-Host '  NOT ADMINISTRATOR - re-run from an Administrator terminal.' -ForegroundColor Red; Read-Host 'Press Enter'; exit 1 }",
        "",
        "# Guard 2: hardware lock - the fingerprint must match this machine EXACTLY",
        "$fpPath = Join-Path $PSScriptRoot 'machine-fingerprint.json'",
        "if (-not (Test-Path $fpPath)) { Write-Host '  Missing machine-fingerprint.json - cannot verify the machine. Refusing.' -ForegroundColor Red; Read-Host 'Press Enter'; exit 1 }",
        "$fp = Get-Content $fpPath -Raw | ConvertFrom-Json",
        "$cs   = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue",
        "$bb   = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue",
        "$model = (\"$($cs.Manufacturer) $($cs.Model)\").Trim()",
        "$cpu   = ((Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1).Name).Trim()",
        "$board = (\"$($bb.Manufacturer) $($bb.Product)\").Trim()",
        "$gpu   = ((Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { $_.Name.Trim() } | Sort-Object) -join '; ')",
        "# Fail-closed: an UNREADABLE field (empty on either side) must REFUSE, not vacuously pass ('' -ne '' is",
        "# false). This keeps the lock honest when WMI can't read a value on the backup or the restore machine.",
        "$fields = @(@('make/model',$model,$fp.Model), @('CPU',$cpu,$fp.Cpu), @('motherboard',$board,$fp.Board), @('GPU',$gpu,$fp.Gpu))",
        "foreach ($f in $fields) { if ([string]::IsNullOrWhiteSpace($f[1]) -or [string]::IsNullOrWhiteSpace($f[2])) {",
        "    Write-Host \"  REFUSING: the $($f[0]) fingerprint is unreadable (backup: '$($f[2])'  this PC: '$($f[1])'), so the machine cannot be verified.\" -ForegroundColor Red",
        "    Write-Host '  Installing another machine''s drivers can break Windows, so restore is blocked.' -ForegroundColor Red",
        "    Read-Host 'Press Enter'; exit 1",
        "} }",
        "$mismatch = @()",
        "if ($model -ne $fp.Model) { $mismatch += \"make/model (backup: '$($fp.Model)'  this PC: '$model')\" }",
        "if ($cpu   -ne $fp.Cpu)   { $mismatch += \"CPU (backup: '$($fp.Cpu)'  this PC: '$cpu')\" }",
        "if ($board -ne $fp.Board) { $mismatch += \"motherboard (backup: '$($fp.Board)'  this PC: '$board')\" }",
        "if ($gpu   -ne $fp.Gpu)   { $mismatch += \"GPU (backup: '$($fp.Gpu)'  this PC: '$gpu')\" }",
        "if ($mismatch.Count -gt 0) {",
        "    Write-Host '  REFUSING: this PC does not match the machine these drivers were backed up from:' -ForegroundColor Red",
        "    foreach ($m in $mismatch) { Write-Host \"     - $m\" -ForegroundColor Red }",
        "    Write-Host '  Installing another machine''s drivers can break Windows, so restore is blocked.' -ForegroundColor Red",
        "    Read-Host 'Press Enter'; exit 1",
        "}",
        "Write-Host \"  Machine matches: $model\" -ForegroundColor Green",
        "",
        "# Guard 3: confirmation",
        "Write-Host 'This will run: pnputil /add-driver \\\"<this folder>\\*.inf\\\" /subdirs /install' -ForegroundColor Cyan",
        "$answer = Read-Host 'Reinstall these drivers now? Type Y to proceed'",
        "if ($answer -notmatch '^[Yy]') { Write-Host 'Cancelled - nothing was installed.'; Read-Host 'Press Enter'; exit 0 }",
        "try { pnputil /add-driver \"$PSScriptRoot\\*.inf\" /subdirs /install } catch { Write-Host ('  [error] ' + $_.Exception.Message) -ForegroundColor Red }",
        "Write-Host 'Done. A reboot is recommended.' -ForegroundColor Green",
        "Read-Host 'Press Enter to close'",
    };

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
}
