using System.IO;
using System.Threading.Tasks;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Locks the "one main exe per app folder" matcher: it must pick the app's real exe and
/// leave out helper/updater/uninstaller binaries — the whole point of the Portable scan cleanup.</summary>
public class PortableAppScannerTests
{
    private static string Pick(string folder, params string[] exeNames)
        => PortableAppScanner.PickMainExe(exeNames.Select(n => $@"C:\Apps\{folder}\{n}").ToArray(), folder) is { } full
            ? System.IO.Path.GetFileName(full)
            : "(none)";

    [Fact]
    public void Exact_name_match_is_picked()
        => Assert.Equal("sunshine.exe", Pick("Sunshine", "sunshine.exe", "sunshine-updater.exe", "unins000.exe"));

    [Fact]
    public void Helper_binaries_are_excluded()
        => Assert.Equal("qbittorrent.exe",
            Pick("qBittorrent", "qbittorrent.exe", "crashpad_handler.exe", "updater.exe", "vc_redist.exe"));

    [Fact]
    public void Version_suffixed_folder_still_matches_the_base_exe()
    {
        Assert.Equal("PixPin.exe", Pick("PixPin_2.1.3", "PixPin.exe", "PixPin-uninstall.exe"));
        Assert.Equal("vlc.exe", Pick("vlc-3.0.20", "vlc.exe", "vlc-cache-gen.exe"));
    }

    [Fact]
    public void Punctuation_and_spacing_differences_still_match()
        => Assert.Equal("sublime_text.exe", Pick("Sublime Text", "sublime_text.exe", "plugin_host.exe"));

    [Fact]
    public void Abbreviated_exe_that_prefixes_the_folder_matches()
        => Assert.Equal("7z.exe", Pick("7-Zip", "7z.exe", "7zFM.exe", "7zG.exe"));

    [Fact]
    public void Longest_prefix_wins_when_several_qualify()
        => Assert.Equal("sunshine.exe", Pick("Sunshine", "sun.exe", "sunshine.exe"));

    [Fact]
    public void Case_is_ignored()
        => Assert.Equal("Firefox.exe", Pick("firefox", "Firefox.exe"));

    [Fact]
    public void No_matching_exe_returns_none()
    {
        Assert.Equal("(none)", Pick("OBS-Studio", "obs64.exe", "obs-ffmpeg-mux.exe")); // neither prefixes "obsstudio"
        Assert.Equal("(none)", Pick("RandomFolder", "setup.exe", "helper.exe"));
    }

    [Fact]
    public void A_single_char_folder_or_exe_never_coincidentally_matches()
    {
        Assert.Equal("(none)", Pick("A", "a.exe"));            // folder too short
        Assert.Equal("(none)", Pick("Photoshop", "p.exe"));   // 1-char exe ignored
    }

    [Theory]
    [InlineData("Sublime Text", "sublimetext")]
    [InlineData("PixPin_2.1.3", "pixpin213")]
    [InlineData("7-Zip", "7zip")]
    [InlineData("  Weird (x64) ", "weirdx64")]
    [InlineData("", "")]
    public void Normalize_folds_to_lowercase_alphanumerics(string input, string expected)
        => Assert.Equal(expected, PortableAppScanner.NormalizeName(input));

    /// <summary>End-to-end: builds a real folder tree and runs the actual scan, proving the walk +
    /// matcher return one main exe per app folder and drop the helpers and the non-matching folder.</summary>
    [Fact]
    public async Task Scan_returns_only_the_main_exe_of_each_app_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "venquery-porttest-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            void App(string folder, params string[] exes)
            {
                var d = Path.Combine(root, folder);
                Directory.CreateDirectory(d);
                foreach (var e in exes) File.WriteAllText(Path.Combine(d, e), "MZ");   // dummy, unsigned
            }
            App("Sunshine", "sunshine.exe", "sunshine-updater.exe", "unins000.exe");
            App("PixPin_2.1.3", "PixPin.exe", "PixPin-uninstall.exe");
            App("7-Zip", "7z.exe", "7zFM.exe");
            App("RandomFolder", "setup.exe", "helper.exe");                            // no name match -> nothing

            var result = await PortableAppScanner.ScanAsync(new[] { root }, System.Array.Empty<string>());
            var names = result.Executables.Select(e => e.Name).OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToArray();

            Assert.Equal(new[] { "7z.exe", "PixPin.exe", "sunshine.exe" }, names);
        }
        finally { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }
}
