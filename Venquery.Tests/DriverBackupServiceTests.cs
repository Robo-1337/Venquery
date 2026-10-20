using System;
using System.IO;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the offline driver-backup generator — the app's highest-risk tool, so safety is asserted
/// hard. The BACKUP must be copy-only (export, never install); the RESTORE script it emits must be
/// hardware-locked (refuse on any mismatch) and must never install before that lock and a typed-Y.</summary>
public class DriverBackupServiceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "venquery-drv-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Script()
    {
        var dir = TempDir();
        try { return File.ReadAllText(DriverBackupService.WriteBackupScript(dir).ScriptPath); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Writes_a_real_file()
    {
        var dir = TempDir();
        try
        {
            var res = DriverBackupService.WriteBackupScript(dir);
            Assert.True(res.Ok, res.Error);
            Assert.True(File.Exists(res.ScriptPath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Backup_is_copy_only_never_installs()
    {
        var s = Script();
        Assert.Contains("pnputil /export-driver", s);
        // The backup itself must never install a driver — the ONLY /add-driver in the file is inside the
        // embedded restore script, which is separately gated. The backup's own commands don't install.
        var exportAt = s.IndexOf("pnputil /export-driver", StringComparison.Ordinal);
        var restoreStart = s.IndexOf("Driver restore (hardware-locked)", StringComparison.Ordinal);
        Assert.True(restoreStart > 0, "restore script must be embedded");
        Assert.DoesNotContain("/add-driver", s.Substring(0, restoreStart));  // nothing installs before the restore block
        Assert.True(exportAt < restoreStart);
    }

    [Fact]
    public void Backup_has_admin_and_typed_Y_guards()
    {
        var s = Script();
        Assert.Contains("IsInRole", s);
        Assert.Contains("notmatch '^[Yy]'", s);
    }

    [Fact]
    public void Embedded_restore_script_is_hardware_locked()
    {
        var s = Script();
        // It must compare make/model, CPU, motherboard and GPU, and REFUSE on any mismatch.
        Assert.Contains("machine-fingerprint.json", s);
        Assert.Contains("$fp.Model", s);
        Assert.Contains("$fp.Cpu", s);
        Assert.Contains("$fp.Board", s);
        Assert.Contains("$fp.Gpu", s);
        Assert.Contains("REFUSING", s);
        // The lock + confirmation must come BEFORE the only install command.
        var refuseAt = s.IndexOf("REFUSING", StringComparison.Ordinal);
        var addDriverAt = s.IndexOf("/add-driver", StringComparison.Ordinal);
        Assert.True(refuseAt >= 0 && addDriverAt >= 0);
        Assert.True(refuseAt < addDriverAt, "the hardware-lock check must precede any install");
    }

    [Fact]
    public void Empty_folder_argument_fails_cleanly()
    {
        var res = DriverBackupService.WriteBackupScript("");
        Assert.False(res.Ok);
        Assert.Equal("", res.ScriptPath);
    }

    [Fact]
    public void Export_destination_override_targets_the_given_folder()
    {
        // The Prepare-for-Reset wizard passes <parent>\Drivers so the export lands in the unified backup.
        var dir = TempDir();
        try
        {
            var target = @"D:\Venquery Backup - Test PC\Drivers";
            var script = File.ReadAllText(DriverBackupService.WriteBackupScript(dir, target).ScriptPath);
            Assert.Contains($"$dest = '{target}'", script);
            Assert.DoesNotContain("Join-Path $dl ('Driver Backup", script);   // default path not used
        }
        finally { Directory.Delete(dir, true); }
    }
}
