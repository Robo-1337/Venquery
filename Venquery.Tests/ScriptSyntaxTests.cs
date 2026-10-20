using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Every PowerShell script Venquery generates is parsed by Windows PowerShell's own parser. A typo or a
/// bad escape in a generator used to surface only when a user ran the script; now it fails here. (Parsing never
/// runs anything.)</summary>
public class ScriptSyntaxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "venq-ps-" + Guid.NewGuid().ToString("N"));
    public ScriptSyntaxTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private List<string> AllGeneratedScripts()
    {
        var scripts = new List<string>();
        void Add(string? p) { Assert.False(string.IsNullOrEmpty(p)); scripts.Add(p!); }

        // Repair Kit / Pro Tools: every action on its own, and all of them combined (with self-delete).
        foreach (var f in ProToolsService.Fixes)
            Add(ProToolsService.WriteFixScript(new[] { f.Id }, Path.Combine(_dir, "fix-" + f.Id), "D:").ScriptPath);
        Add(ProToolsService.WriteFixScript(ProToolsService.Fixes.Select(f => f.Id).ToList(), Path.Combine(_dir, "fix-all"), "C:", autoDelete: true).ScriptPath);

        Add(SystemRepairService.WriteSfcDismScript(Path.Combine(_dir, "sfc")).ScriptPath);
        Add(SystemRepairService.WriteRestorePointScript(Path.Combine(_dir, "rp")).ScriptPath);
        Add(WingetService.WriteInstallScript(Path.Combine(_dir, "winget")).ScriptPath);
        // A destination with typographic + ASCII quotes, the case PsText.Lit exists for.
        Add(DriverBackupService.WriteBackupScript(Path.Combine(_dir, "drv"), Path.Combine(_dir, "Mom\u2019s 'Backup'", "Drivers")).ScriptPath);

        const string exportJson = "{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":\"7zip.7zip\"},{\"PackageIdentifier\":\"Git.Git\"}]," +
                                  "\"SourceDetails\":{\"Name\":\"winget\",\"Identifier\":\"Microsoft.Winget.Source_8wekyb3d8bbwe\",\"Argument\":\"https://cdn.winget.microsoft.com/cache\",\"Type\":\"Microsoft.PreIndexed.Package\"}}]}";
        var w = new WingetService().WriteBackupFiles(exportJson, new HashSet<string> { "7zip.7zip", "Git.Git" },
            Array.Empty<Venquery.Models.AppBackupEntry>(), Path.Combine(_dir, "apps"), "Contoso \u2018Laptop\u2019 Pro", allowCrossMachine: false);
        Assert.True(w.Ok, w.Error);
        Add(w.ScriptPath);

        return scripts;
    }

    [Fact]
    public void Every_generated_script_parses_without_errors()
    {
        var scripts = AllGeneratedScripts();
        var list = Path.Combine(_dir, "scripts.txt");
        File.WriteAllLines(list, scripts);
        var checker = Path.Combine(_dir, "check.ps1");
        File.WriteAllText(checker,
            "$bad = 0\n" +
            "foreach ($p in Get-Content -LiteralPath $args[0]) {\n" +
            "  $errs = $null; $tokens = $null\n" +
            "  [void][System.Management.Automation.Language.Parser]::ParseFile($p, [ref]$tokens, [ref]$errs)\n" +
            "  foreach ($e in $errs) { $bad++; Write-Output (\"{0}:{1}: {2}\" -f (Split-Path $p -Leaf), $e.Extent.StartLineNumber, $e.Message) }\n" +
            "}\n" +
            "Write-Output \"PARSED $bad\"\n");
        var psi = new ProcessStartInfo(SystemTools.Exe("powershell.exe"), $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{checker}\" \"{list}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        Assert.Contains("PARSED 0", output);
        Assert.True(scripts.Count >= ProToolsService.Fixes.Count + 6, $"only {scripts.Count} scripts checked");
    }

    [Fact]
    public void Disk_cleanup_never_ticks_the_recycle_bin_downloads_or_previous_windows()
    {
        var path = ProToolsService.WriteFixScript(new[] { "diskcleanup" }, Path.Combine(_dir, "dc"), "C:").ScriptPath;
        var s = File.ReadAllText(path);
        Assert.Contains("cleanmgr.exe", s);
        Assert.Contains("/sagerun:777", s);
        Assert.Contains("Remove-ItemProperty", s);                 // the StateFlags are removed again afterwards
        Assert.DoesNotContain("'Recycle Bin'", s);
        Assert.DoesNotContain("'DownloadsFolder'", s);
        Assert.DoesNotContain("'Previous Installations'", s);
        Assert.True(s.IndexOf("Type Y", StringComparison.OrdinalIgnoreCase) < s.IndexOf("Start-Process -FilePath (Join-Path $env:SystemRoot", StringComparison.OrdinalIgnoreCase));
    }
}
