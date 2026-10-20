using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>The hardware fingerprint is the lock that stops a backup being restored onto a
/// machine it wasn't captured from. It must be strict and FAIL-CLOSED: any empty/unreadable field, or any
/// difference, means "no match" (the caller then refuses unless the user explicitly allowed cross-machine).</summary>
public class MachineFingerprintTests
{
    private static MachineFingerprint Fp(string model = "Dell OptiPlex 7090", string cpu = "Intel Core i7-10700",
        string board = "Dell 0ABCDE", string gpu = "Intel UHD 630; NVIDIA RTX 3060")
        => new() { Model = model, Cpu = cpu, Board = board, Gpu = gpu };

    [Fact]
    public void Identical_fingerprints_match()
    {
        Assert.True(Fp().Matches(Fp()));
    }

    [Fact]
    public void Match_is_case_and_whitespace_insensitive()
    {
        Assert.True(Fp().Matches(Fp(model: "dell optiplex 7090  ", cpu: "  INTEL CORE I7-10700")));
    }

    [Theory]
    [InlineData("Different Model", "Intel Core i7-10700", "Dell 0ABCDE", "Intel UHD 630; NVIDIA RTX 3060")]
    [InlineData("Dell OptiPlex 7090", "Intel Core i5-9500", "Dell 0ABCDE", "Intel UHD 630; NVIDIA RTX 3060")]
    [InlineData("Dell OptiPlex 7090", "Intel Core i7-10700", "ASUS PRIME", "Intel UHD 630; NVIDIA RTX 3060")]
    [InlineData("Dell OptiPlex 7090", "Intel Core i7-10700", "Dell 0ABCDE", "Intel UHD 630")]
    public void Any_single_field_difference_fails(string model, string cpu, string board, string gpu)
    {
        Assert.False(Fp().Matches(Fp(model, cpu, board, gpu)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_unreadable_field_on_either_side_fails_closed(string blank)
    {
        // An unreadable field (WMI failure) must never be treated as a match — on either side.
        Assert.False(Fp(model: blank).Matches(Fp()));
        Assert.False(Fp().Matches(Fp(cpu: blank)));
        Assert.False(Fp(board: blank).Matches(Fp(board: blank)));   // both empty still refuses
    }

    [Fact]
    public void Null_other_fails()
    {
        Assert.False(Fp().Matches(null));
    }
}
