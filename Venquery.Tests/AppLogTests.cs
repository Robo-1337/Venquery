using System;
using System.IO;
using System.Linq;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>The diagnostic log is what support relies on: it must start with the system header, record every
/// line with a time, keep a full crash record, survive being read while written, and prune old files.</summary>
[Collection("AppLog")]   // AppLog is static: these tests must not run in parallel with each other
public class AppLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "venq-log-" + Guid.NewGuid().ToString("N"));

    public AppLogTests() => AppLog.DirOverride = _dir;

    public void Dispose()
    {
        AppLog.DirOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Session_log_has_a_header_and_timestamped_lines()
    {
        AppLog.Write("scan started");
        AppLog.Write("status: 42 devices");
        var path = AppLog.SessionPath;
        Assert.True(File.Exists(path));
        var text = AppLog.ReadSession();
        Assert.Contains(Edition.Current.DisplayName, text);           // header: edition
        Assert.Contains("Windows", text);                              // header: OS
        Assert.Matches(@"\[\d\d:\d\d:\d\d\] scan started", text);
        Assert.Contains("status: 42 devices", text);
    }

    [Fact]
    public void A_crash_gets_its_own_file_with_the_stack_trace_and_a_line_in_the_session()
    {
        Exception ex;
        try { throw new InvalidOperationException("boom"); } catch (Exception e) { ex = e; }
        var crash = AppLog.WriteCrash(ex, "UI");
        Assert.True(File.Exists(crash));
        var body = File.ReadAllText(crash);
        Assert.Contains("boom", body);
        Assert.Contains("Context : UI", body);
        Assert.Contains(nameof(A_crash_gets_its_own_file_with_the_stack_trace_and_a_line_in_the_session), body);   // stack trace
        Assert.Contains("ERROR (UI): InvalidOperationException: boom", AppLog.ReadSession());
    }

    [Fact]
    public void Old_logs_are_pruned_to_the_newest_ten()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < 15; i++) File.WriteAllText(Path.Combine(_dir, $"crash-2020010{i % 10}-0000{i:00}.log"), "x");
        Exception ex = new Exception("x");
        AppLog.WriteCrash(ex, "test");
        Assert.True(Directory.GetFiles(_dir, "crash-*.log").Length <= 10);
    }

    [Fact]
    public void Writing_never_throws_even_when_the_folder_is_unusable()
    {
        AppLog.DirOverride = "Z:\\\u0000invalid";   // an impossible path
        AppLog.Write("still fine");
        Assert.Equal("", AppLog.WriteCrash(new Exception("x"), "test"));
    }
}
