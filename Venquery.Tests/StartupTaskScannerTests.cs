using System.Linq;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the Startup &amp; Tasks reads. The scheduled-task list comes from the Task Scheduler API, which
/// isn't translated, so it must work on any Windows language (schtasks' CSV headers are localized, and the old
/// parser found nothing on a non-English system).</summary>
public class StartupTaskScannerTests
{
    [Fact]
    public void Task_scheduler_api_lists_tasks_with_real_paths()
    {
        var tasks = StartupTaskScanner.ReadScheduledTasksViaApi();
        Assert.NotNull(tasks);
        Assert.NotEmpty(tasks!);                                   // every Windows install has scheduled tasks
        Assert.All(tasks!, t => Assert.StartsWith("\\", t.Path));
        Assert.All(tasks!, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
        Assert.Equal(tasks!.Count, tasks.Select(t => t.Path).Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(tasks!, t => t.IsMicrosoft);
    }

    [Fact]
    public void Public_read_returns_the_same_tasks_sorted_third_party_first()
    {
        var tasks = StartupTaskScanner.ReadScheduledTasks();
        Assert.NotEmpty(tasks);
        var firstMs = tasks.FindIndex(t => t.IsMicrosoft);
        if (firstMs >= 0) Assert.DoesNotContain(tasks.Skip(firstMs), t => !t.IsMicrosoft);
    }

    private const string Sid = "S-1-5-21-1-2-3-1001";

    [Theory]
    [InlineData("SecurityHealth", "x.exe", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Public", StartupScope.Machine, "Run", "SecurityHealth")]
    [InlineData("Old32", "x.exe", @"HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", "Public", StartupScope.Machine, "Run32", "Old32")]
    [InlineData("FDM", "fdm.exe", @"HKU\" + Sid + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"PC\Alex", StartupScope.User, "Run", "FDM")]
    [InlineData("Ollama", "Ollama.lnk", "Startup", @"PC\Alex", StartupScope.User, "StartupFolder", "Ollama.lnk")]
    [InlineData("Shared", "Shared.lnk", "Common Startup", "Public", StartupScope.Machine, "StartupFolder", "Shared.lnk")]
    public void Startup_entries_map_to_their_task_manager_switch(string name, string cmd, string loc, string user,
        StartupScope scope, string sub, string value)
    {
        var t = StartupTaskScanner.ApprovalTargetFor(new StartupItem(name, cmd, loc) { User = user }, Sid, "Alex");
        Assert.NotNull(t);
        Assert.Equal(scope, t!.Scope);
        Assert.Equal(sub, t.SubKey);
        Assert.Equal(value, t.ValueName);
        Assert.Equal(scope == StartupScope.Machine, t.NeedsAdmin);
    }

    [Theory]
    [InlineData("Other", @"HKU\S-1-5-21-9-9-9-1002\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "PC\\Someone")]   // another user's
    [InlineData("Once", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "Public")]                     // RunOnce: not switchable
    [InlineData("Bad\"Name", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Public")]                    // unsafe for reg.exe
    [InlineData("TheirShortcut", "Startup", "PC\\Someone")]                                                       // another user's Startup folder
    public void Entries_that_cannot_be_switched_safely_stay_review_only(string name, string loc, string user)
        => Assert.Null(StartupTaskScanner.ApprovalTargetFor(new StartupItem(name, "x.lnk", loc) { User = user }, Sid, "Alex"));

    [Fact]
    public void Switch_values_match_what_task_manager_writes()
    {
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, StartupTaskScanner.ApprovalBytes(true));
        var off = StartupTaskScanner.ApprovalBytes(false, new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
        Assert.Equal(3, off[0]);
        Assert.Equal(new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc).ToFileTimeUtc(), System.BitConverter.ToInt64(off, 4));
        var args = StartupTaskScanner.RegAddArgs(new StartupApprovalTarget(StartupScope.Machine, "Run", "DAX2_APP"), false);
        Assert.StartsWith(@"add ""HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"" /v ""DAX2_APP"" /t REG_BINARY /d 03000000", args);
        Assert.EndsWith(" /f", args);
    }

    [Fact]
    public void A_users_own_entry_switches_off_and_on_without_admin()
    {
        var name = "VenqueryTest-" + System.Guid.NewGuid().ToString("N");
        var t = new StartupApprovalTarget(StartupScope.User, "Run", name);
        try
        {
            Assert.True(StartupTaskScanner.IsApproved(t));                      // no value = on
            Assert.True(StartupTaskScanner.SetStartupEnabled(t, false).Ok);
            Assert.False(StartupTaskScanner.IsApproved(t));
            Assert.True(StartupTaskScanner.SetStartupEnabled(t, true).Ok);
            Assert.True(StartupTaskScanner.IsApproved(t));
        }
        finally
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(t.KeyPath, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);                     // remove only our test value
        }
    }

    [Fact]
    public void Toggling_refuses_a_task_name_that_could_inject_arguments()
    {
        var (ok, _) = StartupTaskScanner.SetTaskEnabled("\\Evil\" /TN \\Other", enable: false);
        Assert.False(ok);
    }
}
