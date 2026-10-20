using System.IO;
using System.Runtime.InteropServices;

namespace Venquery.Services;

/// <summary>Headless command-line entry point. Lets Venquery run without its WPF window, the point of
/// Phase 3 is that Venquery works on Windows Server Core, which has no desktop shell.
///
/// <para><c>--version</c> / <c>--help</c> work in every edition; <c>--health</c> / <c>--report</c> / <c>--diag</c> /
/// <c>--fix</c> are compiled behind <c>CLI</c> (every .NET 8 edition, not Legacy). All commands are read-only.</para></summary>
public static class Cli
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    private const int AttachParentProcess = -1;

    /// <summary>Handle a CLI invocation. Returns true if a command was handled (the caller should then
    /// exit without showing a window); false for a normal launch (let the GUI start).</summary>
    public static bool TryHandle(string[] args)
    {
        if (args is null || args.Length == 0) return false;

        bool Has(params string[] flags) =>
            args.Any(a => flags.Any(f => string.Equals(a, f, StringComparison.OrdinalIgnoreCase)));
        // Value of `--flag value` or `--flag=value`; "" if the flag is present with no value; null if absent.
        string? Val(string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length && !args[i + 1].StartsWith("-") ? args[i + 1] : "";
                if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(flag.Length + 1);
            }
            return null;
        }

        var help = Has("--help", "-h", "-?", "/?");
        var version = Has("--version", "-v");
        var report = Has("--report");
        var health = Has("--health");
        var diag = Has("--diag");                       // admin deep read only (server troubleshooting)
        var fix = Has("--fix");                         // write a (combined) guarded maintenance script
        if (!help && !version && !report && !health && !diag && !fix) return false;   // unknown args → GUI runs

        var json = Has("--json");
        var admin = Has("--admin") || diag;             // fold the elevated deep read into the output
        EnsureConsole();

        if (help) { WriteHelp(); return true; }
        if (version) { Out($"{Edition.Current.DisplayName} {MainWindow.AppVersion}"); return true; }

#if CLI
        if (fix) { Out(RunFix(Val("--fix") ?? "", Val("--volume"), Val("--out"), Has("--autodelete"))); return true; }
        // --health / --report / --diag. Run on the thread pool and block: OnStartup runs on the WPF UI
        // thread with a DispatcherSynchronizationContext already installed, so awaiting the scanners'
        // continuations directly here would deadlock (the dispatcher loop isn't pumping yet).
        var mode = diag ? "diag" : report ? "report" : "health";
        try { Out(Task.Run(() => HeadlessReport.RunAsync(mode, json, admin)).GetAwaiter().GetResult()); }
        catch (Exception ex) { Out("Error: " + ex.Message); }
#else
        var mode = fix ? "fix" : diag ? "diag" : report ? "report" : "health";
        Out($"'--{mode}' isn't available in {Edition.Current.DisplayName}.");
#endif
        return true;
    }

    // A WinExe has no console of its own. Attach to the launching shell's console when there isn't one,
    // then rebind Console.Out to a flushing writer on the real stdout handle, this makes both an
    // interactive console AND output redirection (`> file`) capture what we write.
    private static void EnsureConsole()
    {
        try
        {
            if (GetConsoleWindow() == IntPtr.Zero) AttachConsole(AttachParentProcess);
            // The report text uses ·/→/°, emit UTF-8 so `> file` yields a correct UTF-8 file and, on a
            // real console, switch its codepage to UTF-8 so those glyphs render instead of mojibake.
            var enc = new System.Text.UTF8Encoding(false);
            try { Console.OutputEncoding = enc; } catch { /* throws on some redirected handles, ignore */ }
            var writer = new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true };
            Console.SetOut(writer);
        }
        catch { /* best effort, if we can't attach, writes simply go nowhere */ }
    }

    private static void Out(string s) { try { Console.Out.WriteLine(s); Console.Out.Flush(); } catch { } }

#if CLI
    // Write a (combined) guarded maintenance script from the given action ids, the headless equivalent of
    // the Pro Tools "Generate selected script" button, for Server Core.
    private static string RunFix(string idsCsv, string? volume, string? outDir, bool autoDelete)
    {
        var ids = idsCsv.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0)
            return "Usage: --fix <id[,id]> [--volume X:] [--out DIR] [--autodelete]\r\nActions:\r\n"
                 + string.Join("\r\n", ProToolsService.Fixes.Select(f => $"  {f.Id,-13} {f.Title}"));
        var folder = string.IsNullOrWhiteSpace(outDir) ? DownloadService.DownloadsFolder() : outDir!;
        var res = ProToolsService.WriteFixScript(ids, folder, string.IsNullOrWhiteSpace(volume) ? "C:" : volume!, autoDelete);
        if (!res.Ok) return "Error: " + res.Error;
        return $"Wrote {res.ScriptPath}\r\nReview it, then run as Administrator:\r\n  {MainWindow.RunCommandFor(res.ScriptPath)}";
    }
#endif

    private static void WriteHelp()
    {
        Out($"{Edition.Current.DisplayName} {MainWindow.AppVersion}");
        Out("");
        Out("Usage:");
        Out("  Venquery                     Launch the graphical interface (default).");
        Out("  Venquery --version           Print the version and exit.");
        Out("  Venquery --help              Show this help.");
        Out("  Venquery --health [--json]          Read-only system health (storage, memory, battery, uptime).");
        Out("  Venquery --report [--json] [--admin] Full system & driver report (--admin adds the elevated deep read).");
        Out("  Venquery --diag [--json]            Admin deep read only (SMART, TPM/BitLocker, services, network…).");
        Out("  Venquery --fix <id[,id]> [--volume X:] [--out DIR] [--autodelete]");
        Out("                                     Write a guarded maintenance script (--fix with no id lists the actions).");
        Out("");
        Out("  --json        Emit machine-readable JSON instead of text.");
        Out("  --admin       Include the administrator deep read (run from an elevated shell for full detail).");
        Out("  --autodelete  The generated script removes itself once it finishes.");
        Out("");
        Out("  Reads are read-only; fixes are scripts you run yourself (confirm + UAC + typed Y).");
    }
}
