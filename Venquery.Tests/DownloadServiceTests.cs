using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Downloads are HTTPS-only (1.4.2 hardening): a plain-HTTP link is refused before any request is made.</summary>
public class DownloadServiceTests
{
    [Theory]
    [InlineData("https://downloadmirror.intel.com/x.exe", true)]
    [InlineData("HTTPS://example.com/a", true)]
    [InlineData("http://example.com/a.exe", false)]
    [InlineData("ftp://example.com/a.exe", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("example.com/a.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHttps_accepts_only_absolute_https(string? url, bool expected)
        => Assert.Equal(expected, DownloadService.IsHttps(url));

    [Fact]
    public async Task Plain_http_download_is_refused_without_touching_the_network()
    {
        var r = await new DownloadService().DownloadAsync("http://example.invalid/driver.exe", null, new Progress<(double, string)>(_ => { }));
        Assert.False(r.Ok);
        Assert.Contains("HTTPS", r.Error);
    }
}
