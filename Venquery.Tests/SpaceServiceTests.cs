using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>"What's using my space" must add up exactly, list the biggest things first, never follow a junction
/// (that would double count or loop) and never change anything.</summary>
public class SpaceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "venq-space-" + Guid.NewGuid().ToString("N"));
    public SpaceServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void F(string rel, int bytes)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
    }

    [Fact]
    public async Task Sizes_roll_up_and_the_biggest_come_first()
    {
        F(@"Videos\a.mp4", 5000); F(@"Videos\Holiday\b.mp4", 3000);
        F(@"Docs\c.pdf", 100);
        F("loose.bin", 50);

        var r = await SpaceService.ScanAsync(_root);

        Assert.Equal(8150, r.Root.Size);
        Assert.Equal(4, r.Root.Files);
        Assert.Equal(50, r.Root.LooseSize);
        Assert.Equal("Videos", r.Root.Children[0].Name);                 // biggest first
        Assert.Equal(8000, r.Root.Children[0].Size);
        Assert.Equal(3000, r.Root.Children[0].Children.Single().Size);
        Assert.Equal("a.mp4", Path.GetFileName(r.LargestFiles[0].FullPath));
        Assert.Equal(4, r.LargestFiles.Count);
        Assert.True(r.DriveTotal > 0);
    }

    [Fact]
    public async Task A_junction_is_not_followed()
    {
        F(@"Real\big.bin", 4000);
        var link = Path.Combine(_root, "Link");
        var mk = Process.Start(new ProcessStartInfo(SystemTools.Exe("cmd.exe"), $"/c mklink /J \"{link}\" \"{Path.Combine(_root, "Real")}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mk.WaitForExit(10000);
        if (!Directory.Exists(link)) return;   // junctions unavailable here: nothing to assert

        var r = await SpaceService.ScanAsync(_root);
        Assert.Equal(4000, r.Root.Size);                                  // counted once, through Real only
        Assert.DoesNotContain(r.Root.Children, c => c.Name == "Link");
        Directory.Delete(link);                                           // removes the junction, not the target
    }

    [Fact]
    public async Task Keeps_only_the_largest_hundred_files()
    {
        for (var i = 1; i <= 230; i++) F($@"many\f{i}.bin", i);
        var r = await SpaceService.ScanAsync(_root);
        Assert.Equal(100, r.LargestFiles.Count);
        Assert.Equal(230, r.LargestFiles[0].Size);
        Assert.Equal(131, r.LargestFiles[99].Size);
    }
}
