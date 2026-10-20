using System.IO;
using System.Text;

namespace Venquery.Services;

// ---------------------------------------------------------------------------
// System file repair (SFC + DISM), a SHARED, ungated service compiled into EVERY edition (including the
// net48 Legacy build, which links this file). It exists so the one repair every Windows user eventually
// needs, Windows' own corrupt-system-file check, is available from the System Health page in all editions,
// not just Pro Tools.
//
// Posture (unchanged Safe Mode promise): Venquery only ever WRITES the script here. Running it is the user's
// action, gated by the app's confirm dialog, the Windows UAC prompt, and the script's OWN admin check + a
// typed Y before anything runs. It uses only Windows' built-in DISM + sfc; it loads no driver, changes
// nothing on its own, and sends nothing anywhere. This is the same guarded pattern as
// Pro Tools, just scoped to the single SFC/DISM repair and shared across editions.
// ---------------------------------------------------------------------------

/// <summary>Result of writing the SFC/DISM repair script. Plain class (not a record) so the shared file
/// compiles cleanly on net48 as well as net8.</summary>
public sealed class RepairScriptResult
{
    public bool Ok { get; }
    public string Error { get; }
    public string ScriptPath { get; }
    public RepairScriptResult(bool ok, string error, string scriptPath)
    { Ok = ok; Error = error; ScriptPath = scriptPath; }
}

public static class SystemRepairService
{
    /// <summary>Writes ONE guarded PowerShell script that runs <c>DISM /RestoreHealth</c> then
    /// <c>sfc /scannow</c>, Windows' own repair for corrupt system files. Venquery only writes it; the user
    /// runs it (confirm + UAC + typed Y).</summary>
    public static RepairScriptResult WriteSfcDismScript(string outputFolder)
        => WriteScript(outputFolder, "venquery-sfc-dism.ps1", BuildSfcDismScript());

    /// <summary>Writes a guarded script that enables System Protection on C: and creates a System Restore
    /// point, the safe thing to do before any repair or driver change. Venquery only writes it; the user
    /// runs it (confirm + UAC + typed Y).</summary>
    public static RepairScriptResult WriteRestorePointScript(string outputFolder)
        => WriteScript(outputFolder, "venquery-restore-point.ps1", BuildRestorePointScript());

    private static RepairScriptResult WriteScript(string outputFolder, string fileName, string content)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputFolder))
                return new RepairScriptResult(false, "No output folder.", "");
            Directory.CreateDirectory(outputFolder);
            var path = UniquePath(Path.Combine(outputFolder, fileName));
            // UTF-8 with BOM so PowerShell reads any non-ASCII characters correctly.
            File.WriteAllText(path, content, new UTF8Encoding(true));
            return new RepairScriptResult(true, "", path);
        }
        catch (Exception ex) { return new RepairScriptResult(false, ex.Message, ""); }
    }

    // Shared header/guards used by every script this service writes: admin check, then a "what will run"
    // listing, then a typed-Y gate. Nothing runs before the Y.
    private static void AppendGuardedHeader(StringBuilder sb, string title, string[] whatRuns)
    {
        sb.AppendLine($"# Venquery - {title}");
        sb.AppendLine($"# Generated {DateTime.Now:yyyy-MM-dd HH:mm} - review before running.");
        sb.AppendLine("#");
        sb.AppendLine("# You can run this yourself, or press \"Run\" in Venquery - it behaves the same, and nothing");
        sb.AppendLine("# runs until you confirm below. It uses only Windows' built-in tools.");
        sb.AppendLine("# Safety: (1) checks it is Administrator; (2) tells you what will run; (3) waits for a typed Y.");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine($"Write-Host 'Venquery - {title}' -ForegroundColor Cyan");
        sb.AppendLine();
        sb.AppendLine("# --- Guard 1: administrator ---");
        sb.AppendLine("$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)");
        sb.AppendLine("if (-not $isAdmin) { Write-Host '  NOT RUNNING AS ADMINISTRATOR - re-run from an Administrator terminal.' -ForegroundColor Red; Read-Host 'Press Enter to close'; exit 1 }");
        sb.AppendLine();
        sb.AppendLine("# --- What will run ---");
        foreach (var line in whatRuns) sb.AppendLine($"Write-Host '{PsLit(line)}'");
        sb.AppendLine();
        sb.AppendLine("# --- Guard 2: confirmation ---");
        sb.AppendLine("$answer = Read-Host 'Run this now? Type Y to proceed'");
        sb.AppendLine("if ($answer -notmatch '^[Yy]') { Write-Host 'Cancelled - nothing was run.'; Read-Host 'Press Enter'; exit 0 }");
        sb.AppendLine();
    }

    private static string BuildRestorePointScript()
    {
        var sb = new StringBuilder();
        AppendGuardedHeader(sb, "Create a restore point", new[]
        {
            "This will:",
            "   1. Enable System Protection on C: (if it is off)",
            "   2. Create a System Restore point named 'Venquery manual restore point'",
        });
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'Enabling System Protection on C: ...' -ForegroundColor Cyan");
        sb.AppendLine("try { Enable-ComputerRestore -Drive 'C:\\' } catch { Write-Host ('  [note] ' + $_.Exception.Message) -ForegroundColor Yellow }");
        sb.AppendLine("Write-Host 'Creating restore point ...' -ForegroundColor Cyan");
        sb.AppendLine("try { Checkpoint-Computer -Description 'Venquery manual restore point' -RestorePointType 'MODIFY_SETTINGS'; Write-Host '  Restore point created.' -ForegroundColor Green }");
        sb.AppendLine("catch { Write-Host ('  [error] ' + $_.Exception.Message) -ForegroundColor Red; Write-Host '  (Windows limits restore points to one per ~24h by default.)' -ForegroundColor Yellow }");
        sb.AppendLine("Read-Host 'Press Enter to close'");
        return sb.ToString();
    }

    private static string PsLit(string s) => PsText.Lit(s);

    private static string BuildSfcDismScript()
    {
        var sb = new StringBuilder();
        AppendGuardedHeader(sb, "System file repair (SFC + DISM)", new[]
        {
            "These 2 steps will run, in order:",
            "   1. DISM /Online /Cleanup-Image /RestoreHealth",
            "        - repairs the Windows component store (may take several minutes; needs internet)",
            "   2. sfc /scannow",
            "        - scans and repairs protected system files",
        });
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'Running: DISM /Online /Cleanup-Image /RestoreHealth' -ForegroundColor Cyan");
        sb.AppendLine("try { DISM.exe /Online /Cleanup-Image /RestoreHealth } catch { Write-Host ('  [error] ' + $_.Exception.Message) -ForegroundColor Red }");
        sb.AppendLine();
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'Running: sfc /scannow' -ForegroundColor Cyan");
        sb.AppendLine("try { sfc.exe /scannow } catch { Write-Host ('  [error] ' + $_.Exception.Message) -ForegroundColor Red }");
        sb.AppendLine();
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'Finished. Review the output above - a reboot is recommended if repairs were made.' -ForegroundColor Green");
        sb.AppendLine("Read-Host 'Press Enter to close'");
        return sb.ToString();
    }

    // If a script of this name already exists, write "venquery-sfc-dism (2).ps1" etc. rather than overwrite.
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
