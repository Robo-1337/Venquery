using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace Venquery.Services;

// ---------------------------------------------------------------------------
// Startup & scheduled-tasks review. Reads are read-only (WMI + Task Scheduler + registry). Two write actions,
// both per-action, confirmed in the app, and fully reversible (they only flip an on/off state; they NEVER
// delete a task or a startup entry):
//   - a scheduled task's enabled state: an elevated `schtasks /Change` (UAC each time);
//   - a startup program's on/off state (1.3.0): Windows' own "StartupApproved" switch, the exact one Task
//     Manager's Startup tab uses. The user's own entries are switched directly (no admin); machine-wide entries
//     (HKLM Run, the common Startup folder) need an elevated `reg add` (UAC).
// Shared across editions; both switches need Windows 8+, so the UI hides them on the Win7 Legacy build.
// ---------------------------------------------------------------------------

public sealed record StartupItem(string Name, string Command, string Location)
{
    /// <summary>The account WMI reports the entry for ("DOMAIN\\user", or "Public" for machine-wide entries).</summary>
    public string User { get; init; } = "";
    /// <summary>Where this entry's on/off switch lives; null when it can't be switched here (e.g. RunOnce, or
    /// another user's entries).</summary>
    public StartupApprovalTarget? Target { get; init; }
    /// <summary>Whether Windows will start it at sign-in (StartupApproved not set to "disabled").</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>Whose startup list an entry belongs to: the current user's own (no admin to switch) or the whole
/// PC's (admin needed).</summary>
public enum StartupScope { User, Machine }

/// <summary>Where a startup entry's on/off switch lives: under
/// (HKCU|HKLM)\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\(Run|Run32|StartupFolder),
/// value named after the entry.</summary>
public sealed record StartupApprovalTarget(StartupScope Scope, string SubKey, string ValueName)
{
    public bool NeedsAdmin => Scope == StartupScope.Machine;
    public string KeyPath => @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + SubKey;
}

public sealed record ScheduledTaskItem(string Path, string Name, bool Enabled, string NextRun)
{
    /// <summary>Windows' own tasks (under \Microsoft\), hidden by default so users act on third-party ones.</summary>
    public bool IsMicrosoft => Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
}

public static class StartupTaskScanner
{
    // ---- read: startup items (WMI, read-only) ----
    public static List<StartupItem> ReadStartupItems()
    {
        var list = new List<StartupItem>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, Command, Location, User FROM Win32_StartupCommand");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                var name = (o["Caption"] as string ?? "").Trim();
                if (name.Length == 0) name = "(unnamed)";
                var item = new StartupItem(name, (o["Command"] as string ?? "").Trim(), (o["Location"] as string ?? "").Trim())
                { User = (o["User"] as string ?? "").Trim() };
                var target = ApprovalTargetFor(item, CurrentUserSid(), Environment.UserName);
                list.Add(item with { Target = target, Enabled = target is null || IsApproved(target) });
            }
        }
        catch { }
        return list
            .GroupBy(i => i.Name + "|" + i.Command, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- startup programs: on/off (StartupApproved, like Task Manager) ----

    /// <summary>Maps a WMI startup entry to its StartupApproved switch. Run-key entries use the registry value name
    /// (the WMI caption); Startup-folder entries use the shortcut's file name. Only the current user's own entries
    /// and machine-wide ones are switchable; RunOnce entries, other users' entries and anything unrecognised return
    /// null (review-only).</summary>
    internal static StartupApprovalTarget? ApprovalTargetFor(StartupItem it, string currentSid, string currentUserName)
    {
        var loc = (it.Location ?? "").Trim();
        if (loc.Length == 0) return null;
        const string run = @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string run32 = @"\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
        string? scopeKey = null;
        StartupScope scope;
        if (loc.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)) { scope = StartupScope.Machine; scopeKey = loc.Substring(4); }
        else if (loc.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase)) { scope = StartupScope.User; scopeKey = loc.Substring(4); }
        else if (loc.StartsWith("HKU\\", StringComparison.OrdinalIgnoreCase))
        {
            var rest = loc.Substring(4);
            var cut = rest.IndexOf('\\');
            if (cut <= 0 || !string.Equals(rest.Substring(0, cut), currentSid, StringComparison.OrdinalIgnoreCase)) return null;   // another user's
            scope = StartupScope.User; scopeKey = rest.Substring(cut);
        }
        else if (loc.Equals("Startup", StringComparison.OrdinalIgnoreCase))
        {
            // The user's own Startup folder: only when WMI reports it for the signed-in user.
            if (it.User.Length > 0 && !it.User.EndsWith("\\" + currentUserName, StringComparison.OrdinalIgnoreCase)
                && !it.User.Equals(currentUserName, StringComparison.OrdinalIgnoreCase)) return null;
            return Valid(StartupScope.User, "StartupFolder", Path.GetFileName(it.Command.Trim('"')));
        }
        else if (loc.Equals("Common Startup", StringComparison.OrdinalIgnoreCase))
            return Valid(StartupScope.Machine, "StartupFolder", Path.GetFileName(it.Command.Trim('"')));
        else return null;

        if (scopeKey.Equals(run, StringComparison.OrdinalIgnoreCase)) return Valid(scope, "Run", it.Name);
        if (scopeKey.Equals(run32, StringComparison.OrdinalIgnoreCase)) return Valid(scope, "Run32", it.Name);
        return null;   // RunOnce / RunServices / anything else: review-only
    }

    private static StartupApprovalTarget? Valid(StartupScope scope, string sub, string? name)
        => SafeValueName(name) ? new StartupApprovalTarget(scope, sub, name!) : null;

    /// <summary>A value name we'll pass to reg.exe for machine-wide entries: no quotes, no control characters and no
    /// trailing backslash (which would escape the closing quote on the command line).</summary>
    internal static bool SafeValueName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name!.IndexOf('"') < 0 && !name.Any(char.IsControl) && !name.EndsWith("\\", StringComparison.Ordinal);

    internal static string CurrentUserSid()
    {
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? ""; } catch { return ""; }
    }

    /// <summary>True unless StartupApproved marks the entry disabled. Windows writes 12 bytes: the first is even
    /// (02/06) when on and odd (03/07) when off, followed by the time it was switched off. No value = on.</summary>
    internal static bool IsApproved(StartupApprovalTarget t)
    {
        try
        {
            var root = t.Scope == StartupScope.Machine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
            using var k = root.OpenSubKey(t.KeyPath);
            return !(k?.GetValue(t.ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1);
        }
        catch { return true; }
    }

    /// <summary>The 12-byte StartupApproved value Task Manager writes: 02 (on) or 03 (off) + 3 zero bytes + the
    /// switch-off time as a FILETIME (zeros when on).</summary>
    internal static byte[] ApprovalBytes(bool enable, DateTime? when = null)
    {
        var data = new byte[12];
        data[0] = enable ? (byte)0x02 : (byte)0x03;
        if (!enable) BitConverter.GetBytes((when ?? DateTime.UtcNow).ToFileTimeUtc()).CopyTo(data, 4);
        return data;
    }

    /// <summary>The exact elevated command used for a machine-wide entry (also what the tests check).</summary>
    internal static string RegAddArgs(StartupApprovalTarget t, bool enable)
        => $"add \"HKLM\\{t.KeyPath}\" /v \"{t.ValueName}\" /t REG_BINARY /d {BitConverter.ToString(ApprovalBytes(enable)).Replace("-", "")} /f";

    /// <summary>Turns a startup program on or off. The user's own entries are written straight to HKCU (no admin);
    /// machine-wide entries go through an elevated reg.exe (one UAC prompt). Only the on/off flag changes: the
    /// program's Run entry or shortcut is never touched, so it's fully reversible. Blocks until done; call from a
    /// background thread.</summary>
    public static (bool Ok, string Message) SetStartupEnabled(StartupApprovalTarget t, bool enable)
    {
        if (!SafeValueName(t.ValueName)) return (false, "That entry's name can't be safely changed.");
        try
        {
            if (t.Scope == StartupScope.User)
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(t.KeyPath);
                k!.SetValue(t.ValueName, ApprovalBytes(enable), Microsoft.Win32.RegistryValueKind.Binary);
                return (true, $"Startup program turned {(enable ? "on" : "off")}.");
            }
            var psi = new ProcessStartInfo(SystemTools.Exe("reg.exe"), RegAddArgs(t, enable))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = SystemTools.WorkDir };
            using var p = Process.Start(psi);
            if (p is null) return (false, "Could not start reg.exe.");
            p.WaitForExit();
            return p.ExitCode == 0
                ? (true, $"Startup program turned {(enable ? "on" : "off")}.")
                : (false, $"Windows reported error {p.ExitCode} while changing it.");
        }
        catch (System.ComponentModel.Win32Exception) { return (false, "Administrator permission was not granted."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ---- read: scheduled tasks (Task Scheduler API, read-only; schtasks as a fallback) ----
    public static List<ScheduledTaskItem> ReadScheduledTasks()
    {
        // The Task Scheduler's own API is language-independent. schtasks' CSV is not: its column headers and the
        // "Enabled" value are translated, so on a non-English Windows the CSV parser found no tasks at all.
        var viaApi = ReadScheduledTasksViaApi();
        if (viaApi is not null && viaApi.Count > 0)
            return viaApi.OrderBy(t => t.IsMicrosoft).ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return ReadScheduledTasksViaSchtasks();
    }

    /// <summary>Walks every Task Scheduler folder through the "Schedule.Service" COM object (late-bound, so it
    /// needs no interop assembly and works the same on the .NET 8 and .NET Framework builds). Read-only. Folders
    /// or tasks the current user may not read are skipped. Null when the service can't be reached.</summary>
    internal static List<ScheduledTaskItem>? ReadScheduledTasksViaApi()
    {
        object? svc = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return null;
            svc = Activator.CreateInstance(type);
            if (svc is null) return null;
            Com(svc, "Connect");
            var root = Com(svc, "GetFolder", "\\");
            if (root is null) return null;
            var list = new List<ScheduledTaskItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            WalkTaskFolder(root, list, seen, 0);
            return list;
        }
        catch { return null; }
        finally
        {
            try { if (svc is not null && System.Runtime.InteropServices.Marshal.IsComObject(svc)) System.Runtime.InteropServices.Marshal.ReleaseComObject(svc); }
            catch { }
        }
    }

    private static void WalkTaskFolder(object folder, List<ScheduledTaskItem> list, HashSet<string> seen, int depth)
    {
        if (depth > 32) return;
        try
        {
            var tasks = Com(folder, "GetTasks", 1);               // 1 = TASK_ENUM_HIDDEN, include hidden tasks
            var count = tasks is null ? 0 : Convert.ToInt32(ComGet(tasks, "Count"));
            for (var i = 1; i <= count; i++)                       // COM collections are 1-based
            {
                try
                {
                    var task = ComItem(tasks!, i);
                    if (task is null) continue;
                    var path = ComGet(task, "Path") as string ?? "";
                    if (path.Length == 0 || !seen.Add(path)) continue;
                    var enabled = ComGet(task, "Enabled") is bool en && en;
                    var next = "";
                    try { if (ComGet(task, "NextRunTime") is DateTime n && n.Year > 1900) next = n.ToString("g"); } catch { }
                    var name = ComGet(task, "Name") as string;
                    if (string.IsNullOrEmpty(name)) name = path.Substring(path.LastIndexOf('\\') + 1);
                    list.Add(new ScheduledTaskItem(path, name!, enabled, next));
                }
                catch { /* one unreadable task, skip it */ }
            }
        }
        catch { /* a folder this user can't list, skip its tasks */ }
        try
        {
            var subs = Com(folder, "GetFolders", 0);
            var count = subs is null ? 0 : Convert.ToInt32(ComGet(subs, "Count"));
            for (var i = 1; i <= count; i++)
            {
                try { var sub = ComItem(subs!, i); if (sub is not null) WalkTaskFolder(sub, list, seen, depth + 1); }
                catch { }
            }
        }
        catch { }
    }

    private static object? Com(object target, string method, params object[] args)
        => target.GetType().InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod, null, target, args);

    // A COM collection's Item is an indexed PROPERTY (InvokeMethod fails with DISP_E_MEMBERNOTFOUND).
    private static object? ComItem(object collection, int index)
        => collection.GetType().InvokeMember("Item", System.Reflection.BindingFlags.GetProperty, null, collection, new object[] { index });

    private static object? ComGet(object target, string property)
        => target.GetType().InvokeMember(property, System.Reflection.BindingFlags.GetProperty, null, target, null);

    /// <summary>Fallback parser for schtasks' verbose CSV (English column names only).</summary>
    private static List<ScheduledTaskItem> ReadScheduledTasksViaSchtasks()
    {
        var csv = RunCapture(SystemTools.Exe("schtasks.exe"), "/query /fo CSV /v");
        var list = new List<ScheduledTaskItem>();
        if (string.IsNullOrEmpty(csv)) return list;

        var lines = csv.Replace("\r\n", "\n").Split('\n');
        int nameIdx = -1, stateIdx = -1, nextIdx = -1;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var fields = ParseCsvLine(raw);
            // Header row (repeats between folders on some builds), (re)learn the column positions.
            if (fields.Contains("TaskName") && fields.Contains("Scheduled Task State"))
            {
                nameIdx = Array.IndexOf(fields, "TaskName");
                stateIdx = Array.IndexOf(fields, "Scheduled Task State");
                nextIdx = Array.IndexOf(fields, "Next Run Time");
                continue;
            }
            if (nameIdx < 0 || stateIdx < 0) continue;
            if (fields.Length <= Math.Max(nameIdx, stateIdx)) continue;
            var path = fields[nameIdx].Trim();
            if (path.Length == 0 || path == "TaskName") continue;
            if (!seen.Add(path)) continue;   // one row per trigger, keep the first
            var state = fields[stateIdx].Trim();
            var next = nextIdx >= 0 && nextIdx < fields.Length ? fields[nextIdx].Trim() : "";
            var name = path.TrimEnd('\\');
            var cut = name.LastIndexOf('\\');
            if (cut >= 0 && cut < name.Length - 1) name = name.Substring(cut + 1);
            list.Add(new ScheduledTaskItem(path, name, state.Equals("Enabled", StringComparison.OrdinalIgnoreCase), next));
        }
        return list.OrderBy(t => t.IsMicrosoft).ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- write: enable/disable one task (elevated, per-action, reversible) ----
    /// <summary>Flips a task's enabled state via an elevated <c>schtasks /Change</c> (one UAC prompt).
    /// Reversible; never deletes. Returns (ok, message). Blocks until the elevated call exits, call from a
    /// background thread.</summary>
    public static (bool Ok, string Message) SetTaskEnabled(string taskPath, bool enable)
    {
        if (string.IsNullOrWhiteSpace(taskPath)) return (false, "No task selected.");
        // A task name containing a quote or control char could break out of the quoting and inject extra
        // arguments into the elevated schtasks call, refuse it rather than build an unsafe command line.
        if (taskPath.IndexOf('"') >= 0 || taskPath.Any(char.IsControl))
            return (false, "That task name contains characters that can't be safely passed to schtasks.");
        try
        {
            var psi = new ProcessStartInfo(Venquery.Services.SystemTools.Exe("schtasks.exe"),
                $"/Change /TN \"{taskPath}\" /{(enable ? "ENABLE" : "DISABLE")}")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            if (p is null) return (false, "Could not start schtasks.");
            p.WaitForExit();
            return p.ExitCode == 0
                ? (true, $"Task {(enable ? "enabled" : "disabled")}.")
                : (false, $"schtasks reported error {p.ExitCode} (some protected tasks can't be changed).");
        }
        catch (System.ComponentModel.Win32Exception) { return (false, "Administrator permission was not granted."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ---- helpers ----
    private static string RunCapture(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var outText = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return outText;
        }
        catch { return ""; }
    }

    // Minimal RFC-4180-ish CSV line parser: handles quoted fields with embedded commas and "" escapes.
    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }
}
