using System;
using System.IO;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the shared SFC/DISM repair-script generator (present in every edition, driven from the
/// System Health page). Venquery must only ever WRITE a guarded script: it must check for administrator,
/// run only Windows' own DISM + sfc, and make no change before the typed-Y confirmation.</summary>
public class SystemRepairServiceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "venquery-sfc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Writes_a_real_file()
    {
        var dir = TempDir();
        try
        {
            var res = SystemRepairService.WriteSfcDismScript(dir);
            Assert.True(res.Ok, res.Error);
            Assert.True(File.Exists(res.ScriptPath));
            Assert.EndsWith(".ps1", res.ScriptPath);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Script_runs_only_dism_and_sfc()
    {
        var dir = TempDir();
        try
        {
            var script = File.ReadAllText(SystemRepairService.WriteSfcDismScript(dir).ScriptPath);
            Assert.Contains("DISM.exe /Online /Cleanup-Image /RestoreHealth", script);
            Assert.Contains("sfc.exe /scannow", script);
            // No other system-changing tools sneak in.
            Assert.DoesNotContain("Remove-Item", script);
            Assert.DoesNotContain("netsh", script);
            Assert.DoesNotContain("reg ", script);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Script_has_the_admin_and_typed_Y_guards()
    {
        var dir = TempDir();
        try
        {
            var script = File.ReadAllText(SystemRepairService.WriteSfcDismScript(dir).ScriptPath);
            Assert.Contains("IsInRole", script);              // administrator check
            Assert.Contains("notmatch '^[Yy]'", script);      // typed-Y confirmation
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Nothing_runs_before_the_typed_confirmation()
    {
        var dir = TempDir();
        try
        {
            var script = File.ReadAllText(SystemRepairService.WriteSfcDismScript(dir).ScriptPath);
            var confirmAt = script.IndexOf("Type Y to proceed", StringComparison.Ordinal);
            var dismAt = script.IndexOf("try { DISM.exe", StringComparison.Ordinal);
            var sfcAt = script.IndexOf("try { sfc.exe", StringComparison.Ordinal);
            Assert.True(confirmAt >= 0 && dismAt >= 0 && sfcAt >= 0);
            Assert.True(confirmAt < dismAt, "confirmation must precede DISM");
            Assert.True(confirmAt < sfcAt, "confirmation must precede sfc");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Second_write_does_not_overwrite_the_first()
    {
        var dir = TempDir();
        try
        {
            var a = SystemRepairService.WriteSfcDismScript(dir);
            var b = SystemRepairService.WriteSfcDismScript(dir);
            Assert.True(a.Ok && b.Ok);
            Assert.NotEqual(a.ScriptPath, b.ScriptPath);
            Assert.True(File.Exists(a.ScriptPath) && File.Exists(b.ScriptPath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Empty_folder_argument_fails_cleanly()
    {
        var res = SystemRepairService.WriteSfcDismScript("");
        Assert.False(res.Ok);
        Assert.Equal("", res.ScriptPath);
    }

    [Fact]
    public void Restore_point_script_is_guarded_and_only_creates_a_checkpoint()
    {
        var dir = TempDir();
        try
        {
            var res = SystemRepairService.WriteRestorePointScript(dir);
            Assert.True(res.Ok, res.Error);
            var script = File.ReadAllText(res.ScriptPath);
            Assert.Contains("IsInRole", script);                 // admin guard
            Assert.Contains("notmatch '^[Yy]'", script);          // typed-Y guard
            Assert.Contains("Checkpoint-Computer", script);       // creates the restore point
            Assert.Contains("Enable-ComputerRestore", script);
            // Confirmation precedes the checkpoint.
            Assert.True(script.IndexOf("Type Y to proceed", StringComparison.Ordinal)
                      < script.IndexOf("Checkpoint-Computer", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, true); }
    }
}
