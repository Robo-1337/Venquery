using Venquery.Models;
using Xunit;

namespace Venquery.Tests;

public class SystemInfoTests
{
    [Fact]
    public void Composes_model_and_spec_lines()
    {
        var s = new SystemInfo("Dell", "XPS 15", "Laptop", "Windows 11", "i7", "16 GB RAM");
        Assert.Equal("Dell XPS 15", s.ModelLine);
        Assert.Equal("Dell XPS 15", s.DisplayTitle);
        Assert.Contains("Laptop", s.SpecLine);
        Assert.Contains("Windows 11", s.SpecLine);
        Assert.Contains("16 GB RAM", s.SpecLine);
        Assert.True(s.HasAnything);
    }

    [Fact]
    public void Empty_info_has_nothing_and_falls_back_title()
    {
        var s = new SystemInfo("", "", "", "", "", "");
        Assert.False(s.HasAnything);
        Assert.Equal("This PC", s.DisplayTitle);
    }

    [Fact]
    public void Model_line_skips_blank_manufacturer()
    {
        var s = new SystemInfo("", "MyBoard", "Desktop", "", "", "");
        Assert.Equal("MyBoard", s.ModelLine);
        Assert.True(s.HasAnything);
    }
}

public class PortableExeTests
{
    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(5242880, "5.0 MB")]     // 5 * 1024^2
    [InlineData(3221225472, "3.0 GB")]  // 3 * 1024^3
    public void Size_text_scales_units(long bytes, string expected)
        => Assert.Equal(expected, new PortableExe { SizeBytes = bytes }.SizeText);

    [Fact]
    public void Unsigned_is_warn()
    {
        var e = new PortableExe { Signed = false };
        Assert.Contains("unsigned", e.SignatureText);
        Assert.Equal("warn", e.SignatureKind);
    }

    [Fact]
    public void Signed_is_ok_and_names_signer_when_present()
    {
        Assert.Contains("signed", new PortableExe { Signed = true, Signer = "" }.SignatureText);
        Assert.Contains("Acme", new PortableExe { Signed = true, Signer = "Acme" }.SignatureText);
        Assert.Equal("ok", new PortableExe { Signed = true }.SignatureKind);
    }
}

public class AppBackupEntryTests
{
    [Fact]
    public void Restorable_detail_shows_winget_and_version()
    {
        Assert.Equal("winget package", new AppBackupEntry { Id = "A", Restorable = true, Version = "" }.Detail);
        var withVer = new AppBackupEntry { Id = "A", Restorable = true, Version = "1.2" }.Detail;
        Assert.StartsWith("winget", withVer);
        Assert.Contains("1.2", withVer);
    }

    [Fact]
    public void Manual_detail_says_reinstall_manually()
        => Assert.Contains("reinstall manually", new AppBackupEntry { Id = "A", Restorable = false }.Detail);

    [Fact]
    public void Display_name_is_the_package_id()
        => Assert.Equal("Google.Chrome", new AppBackupEntry { Id = "Google.Chrome" }.DisplayName);
}
