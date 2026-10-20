using System;
using System.IO;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the winget-install script generator (offered when winget is missing, e.g. LTSC / IoT
/// Enterprise). Venquery only WRITES it; it must be guarded (typed Y) and pull only from Microsoft's own
/// official endpoints.</summary>
public class WingetInstallTests
{
    [Fact]
    public void Install_script_is_guarded_and_uses_official_sources()
    {
        var dir = Path.Combine(Path.GetTempPath(), "venq-winget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var res = WingetService.WriteInstallScript(dir);
            Assert.True(res.Ok, res.Error);
            var s = File.ReadAllText(res.ScriptPath);
            Assert.Contains("notmatch '^[Yy]'", s);                                  // typed-Y guard
            // Every download comes from Microsoft's own winget-cli release (aka.ms/getwinget as fallback).
            Assert.Contains("https://github.com/microsoft/winget-cli/releases/latest/download", s);
            Assert.Contains("DesktopAppInstaller_Dependencies.zip", s);
            Assert.Contains("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle", s);
            Assert.Contains("aka.ms/getwinget", s);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(s, @"https?://[^\s'""\)]+"))
            {
                var url = m.Value.Replace("$release", WingetService.WingetReleaseBase);
                Assert.True(url.StartsWith("https://github.com/microsoft/winget-cli/", StringComparison.OrdinalIgnoreCase)
                         || url.StartsWith("https://aka.ms/", StringComparison.OrdinalIgnoreCase), "unexpected source: " + url);
            }
            Assert.Contains("Add-AppxPackage", s);
            // The frameworks are picked per processor, and failures are shown, never swallowed.
            Assert.Contains("PROCESSOR_ARCHITEW6432", s);
            Assert.Contains("arm64", s);
            Assert.DoesNotContain("Add-AppxPackage -Path $d.FullName -ErrorAction SilentlyContinue", s);
            Assert.Contains("$ProgressPreference = 'SilentlyContinue'", s);
            Assert.DoesNotContain("__RELEASE__", s);
            Assert.DoesNotContain("__GENERATED__", s);
            // Confirmation precedes any download/install.
            Assert.True(s.IndexOf("Type Y to proceed", StringComparison.Ordinal)
                      < s.IndexOf("Invoke-WebRequest", StringComparison.Ordinal));
            Assert.True(s.IndexOf("Type Y to proceed", StringComparison.Ordinal)
                      < s.IndexOf("Add-AppxPackage", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DetectEdition_does_not_throw() => WingetService.DetectEdition();
}
