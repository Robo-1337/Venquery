using System.IO;
using System.Text;

namespace Venquery.Services;

/// <summary>
/// The diagnostic log. Every line shown in the "View Log" console, every status-bar message and every unexpected
/// error is also appended, with a timestamp, to a per-session file under
/// <c>%LOCALAPPDATA%\&lt;edition settings folder&gt;\Logs</c>, so the record survives a crash or a closed window.
/// Unhandled exceptions additionally get their own <c>crash-*.log</c> with the full stack trace. Local only: these
/// files never leave the PC unless the user attaches them to something themselves. The newest 10 session logs and
/// 10 crash logs are kept. Thread-safe; never throws (logging must not be able to break the app).
/// </summary>
public static class AppLog
{
    private const int Keep = 10;
    private static readonly object Gate = new();
    private static string? _sessionPath;
    private static bool _broken;

    /// <summary>The folder holding the session and crash logs.</summary>
    public static string LogsDir
        => DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Edition.Current.SettingsDir, "Logs");

    /// <summary>Tests only: log into this folder instead (resets the session).</summary>
    internal static string? DirOverride
    {
        get => _dirOverride;
        set { lock (Gate) { _dirOverride = value; _sessionPath = null; _broken = false; } }
    }
    private static string? _dirOverride;

    /// <summary>This session's log file (created on first use), or "" if it couldn't be created.</summary>
    public static string SessionPath
    {
        get { lock (Gate) { EnsureSession(); return _sessionPath ?? ""; } }
    }

    /// <summary>Appends one timestamped line to this session's log.</summary>
    public static void Write(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (Gate)
        {
            if (_broken) return;
            try
            {
                EnsureSession();
                if (_sessionPath is null) return;
                File.AppendAllText(_sessionPath, $"[{DateTime.Now:HH:mm:ss}] {line.TrimEnd()}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { _broken = true; }   // a read-only profile or full disk: stop trying, never throw
        }
    }

    /// <summary>Records an unexpected error: a crash-*.log with the full details, and a line in the session log.
    /// Returns the crash log's path ("" if it couldn't be written).</summary>
    public static string WriteCrash(Exception ex, string context)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogsDir);
                var path = Path.Combine(LogsDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
                var sb = new StringBuilder();
                sb.AppendLine(Header());
                sb.AppendLine($"Context : {context}");
                sb.AppendLine($"When    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine(new string('-', 60));
                sb.AppendLine(ex.ToString());
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Prune("crash-*.log");
                Write($"ERROR ({context}): {ex.GetType().Name}: {ex.Message}  [details: {Path.GetFileName(path)}]");
                return path;
            }
        }
        catch { return ""; }
    }

    /// <summary>The whole current session log as text ("" if none).</summary>
    public static string ReadSession()
    {
        try
        {
            var p = SessionPath;
            if (p.Length == 0 || !File.Exists(p)) return "";
            // FileShare.ReadWrite: the log may be appended to while we read it.
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    /// <summary>App, edition, version, Windows build and architecture: the first lines of every log.</summary>
    public static string Header()
    {
        string winBuild = "";
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = k?.GetValue("ProductName") as string ?? "";
            var display = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? "";
            var build = k?.GetValue("CurrentBuild") as string ?? "";
            var ubr = k?.GetValue("UBR")?.ToString() ?? "";
            winBuild = $"{product} {display} (build {build}{(ubr.Length > 0 ? "." + ubr : "")})".Trim();
        }
        catch { winBuild = Environment.OSVersion.VersionString; }
        var asm = typeof(AppLog).Assembly.GetName().Version;
        return $"{Edition.Current.DisplayName} {asm?.ToString(3)} · {winBuild} · " +
               $"{(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")} Windows · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} process";
    }

    private static void EnsureSession()
    {
        if (_sessionPath is not null || _broken) return;
        try
        {
            Directory.CreateDirectory(LogsDir);
            var p = Path.Combine(LogsDir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(p, Header() + Environment.NewLine + $"Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss}" +
                                 Environment.NewLine + new string('-', 60) + Environment.NewLine, new UTF8Encoding(true));
            _sessionPath = p;
            Prune("session-*.log");
        }
        catch { _broken = true; }
    }

    private static void Prune(string pattern)
    {
        try
        {
            foreach (var old in Directory.GetFiles(LogsDir, pattern).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).Skip(Keep))
            {
                if (string.Equals(old, _sessionPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(old); } catch { }
            }
        }
        catch { }
    }
}
