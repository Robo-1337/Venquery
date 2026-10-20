using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Regression guards for how Venquery starts other programs and opens things. A Windows tool launched
/// by bare name ("powershell.exe") is searched for in the app's own folder and the current folder first, so a
/// look-alike planted next to a portable Venquery (e.g. in Downloads) would run instead, elevated when the
/// call uses "runas". Every launch must therefore use an absolute path (SystemTools.Exe in the app,
/// NodePaths.SystemExe in the Node).</summary>
public class LaunchGuardTests
{
    // A literal program name handed straight to a process start.
    private static readonly Regex DirectBareLaunch = new(
        @"(new\s+ProcessStartInfo\s*\(\s*""[^""\\/:]+""|Process\.Start\s*\(\s*""[^""\\/:]+"")", RegexOptions.Compiled);

    // ...or to one of the app's own run helpers (the app's helpers take a full path, unlike the Node's).
    private static readonly Regex HelperBareLaunch = new(
        @"\b(RunCapture|Run|RunAsync)\s*\(\s*""[^""\\/:]+\.exe""", RegexOptions.Compiled);

    [Fact]
    public void App_never_launches_a_program_by_bare_name()
    {
        var dir = SourceDir("Venquery", "MainWindow.xaml.cs");
        AssertNone(dir, DirectBareLaunch);
        AssertNone(dir, HelperBareLaunch);
    }


    private static void AssertNone(string dir, Regex rule)
    {
        var hits = Lines(dir)
            .Where(x => rule.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0,
            "Launch by bare program name (use SystemTools.Exe / NodePaths.SystemExe):\n" + string.Join("\n", hits));
    }

    private static IEnumerable<(string f, int i, string line)> Lines(string dir)
        => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/obj/") && !f.Replace('\\', '/').Contains("/bin/"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => !x.line.TrimStart().StartsWith("//"));

    [Theory]
    [InlineData("powershell.exe", @"WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("schtasks.exe", "schtasks.exe")]
    [InlineData("pnputil", "pnputil.exe")]
    [InlineData("explorer.exe", "explorer.exe")]
    public void SystemTools_resolves_to_an_absolute_windows_path(string name, string tail)
    {
        var p = SystemTools.Exe(name);
        Assert.True(Path.IsPathRooted(p), p);
        Assert.EndsWith(tail, p, System.StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), p,
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Winget_is_always_resolved_to_an_absolute_path()
        => Assert.True(Path.IsPathRooted(WingetService.WingetExe()));

    [Theory]
    [InlineData("https://www.nvidia.com/drivers", true)]
    [InlineData("http://example.com", true)]
    [InlineData(@"C:\Users\x\Downloads\restore.ps1", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Only_web_links_are_opened_as_links(string target, bool web)
        => Assert.Equal(web, Venquery.MainWindow.IsWebLink(target));

    private static string SourceDir(string folder, string marker, [CallerFilePath] string thisFile = "")
    {
        var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", folder));
        Assert.True(File.Exists(Path.Combine(candidate, marker)), $"Launch guard couldn't find {marker} under {candidate}.");
        return candidate;
    }

    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_A112&SUBSYS_11111025&REV_F1", @"PCI\VEN_8086&DEV_A112")]
    [InlineData(@"USB\VID_046D&PID_C52B&REV_2400", @"USB\VID_046D&PID_C52B")]
    [InlineData(@"ACPI\INT33A1", @"ACPI\INT33A1")]
    [InlineData(@"PCI\VEN_8086", null)]                  // vendor only: too vague, never matched
    [InlineData(@"PCI\VEN_8086&CC_0C03", null)]
    [InlineData("", null)]
    public void Windows_Update_drivers_match_devices_by_vendor_and_device(string id, string? key)
        => Assert.Equal(key, Venquery.MainWindow.DeviceKey(id));
}
