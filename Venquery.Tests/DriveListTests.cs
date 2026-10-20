using System.Linq;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>The drive picker on the Portable / Documents pages is built from this list. It must always
/// surface the OS drive and flag it, so "additive" scanning (personal + OS drive + ticked drives) holds.</summary>
public class DriveListTests
{
    [Fact]
    public void Ready_includes_and_flags_the_os_drive()
    {
        var drives = DriveList.Ready();
        Assert.NotEmpty(drives);
        Assert.Contains(drives, d => d.IsOsDrive);
        // Exactly one OS drive, and its caption starts with a drive letter.
        var os = drives.Single(d => d.IsOsDrive);
        Assert.Matches("^[A-Za-z]:", os.Letter);
        Assert.False(string.IsNullOrWhiteSpace(os.Caption));
    }
}
