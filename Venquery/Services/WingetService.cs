using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Venquery.Models;

namespace Venquery.Services;

public sealed record WingetExportResult(
    bool Ok,
    string Error,
    string RawJson,
    IReadOnlyList<AppBackupEntry> Restorable,
    IReadOnlyList<AppBackupEntry> Manual);

public sealed record BackupWriteResult(bool Ok, string Error, string JsonPath, string ScriptPath, int Count);

/// <summary>
/// Captures the machine's installed applications via Windows' own <c>winget export</c>
/// and turns them into a portable backup manifest plus a human-readable restore script.
///
/// Philosophy: this is READ-ONLY inspection (the same spirit as the WMI hardware scan)
/// plus WRITING two files to disk. It never runs <c>winget import</c> or installs
/// anything, restoring is a separate, deliberate act the user performs by opening and
/// running the generated restore.ps1 themselves.
/// </summary>
public sealed class WingetService
{
    /// <summary>Runs <c>winget export</c> to a temp file, then parses it into restorable
    /// apps (mapped to a winget package) and manual apps (no source winget could find).</summary>
    public async Task<WingetExportResult> ExportAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"venquery-winget-{Guid.NewGuid():N}.json");
        try
        {
            log?.Report("> winget export (reading installed apps, nothing is installed)…");
            var (started, exit, stdout, stderr) = await RunAsync(
                $"export -o \"{temp}\" --accept-source-agreements --disable-interactivity", log, ct);

            if (!started)
                return Fail("winget (App Installer) isn't available on this PC. Install “App Installer” " +
                            "from the Microsoft Store, then try again.");

            if (!File.Exists(temp))
            {
                var why = FirstMeaningfulLine(stderr) ?? FirstMeaningfulLine(stdout) ?? $"winget exited with code {exit}";
                return Fail($"winget export produced no file, {why}");
            }

            var raw = await File.ReadAllTextAsync(temp, ct);
            var restorable = ParseRestorable(raw);
            var manual = ParseManual(stdout + "\n" + stderr);

            log?.Report($"> winget export · {restorable.Count} restorable, {manual.Count} manual (no winget source)");
            return new WingetExportResult(true, "", raw, restorable, manual);
        }
        catch (OperationCanceledException)
        {
            return Fail("cancelled");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp cleanup is best-effort */ }
        }

        static WingetExportResult Fail(string error)
            => new(false, error, "", Array.Empty<AppBackupEntry>(), Array.Empty<AppBackupEntry>());
    }

    /// <summary>Writes backup.json (filtered to the selected package IDs) and a guarded,
    /// readable restore.ps1 next to it. Returns the two paths. Does not execute anything.
    /// <paramref name="sourceMachine"/> stamps the script with the origin PC so it can
    /// model-lock; <paramref name="allowCrossMachine"/> drops that lock when the user opted in.</summary>
    public BackupWriteResult WriteBackupFiles(
        string rawJson, ISet<string> selectedIds, IReadOnlyList<AppBackupEntry> manual,
        string folder, string sourceMachine, bool allowCrossMachine)
    {
        try
        {
            Directory.CreateDirectory(folder);

            // Re-parse and filter the export document to only the checked packages, keeping
            // winget's original Sources/SourceDetails shape so `winget import` accepts it.
            var root = JsonNode.Parse(rawJson)!;
            int kept = 0;
            if (root["Sources"] is JsonArray sources)
            {
                foreach (var src in sources)
                {
                    if (src?["Packages"] is not JsonArray pkgs) continue;
                    var filtered = new JsonArray();
                    foreach (var p in pkgs)
                    {
                        var id = p?["PackageIdentifier"]?.GetValue<string>();
                        if (id is not null && selectedIds.Contains(id)) { filtered.Add(p!.DeepClone()); kept++; }
                    }
                    src["Packages"] = filtered;
                }
            }

            var jsonPath = UniquePath(Path.Combine(folder, "venquery-apps-backup.json"));
            File.WriteAllText(jsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            var scriptPath = Path.ChangeExtension(jsonPath, ".ps1");
            File.WriteAllText(scriptPath,
                BuildRestoreScript(Path.GetFileName(jsonPath), kept, manual, sourceMachine, allowCrossMachine),
                new UTF8Encoding(true));

            return new BackupWriteResult(true, "", jsonPath, scriptPath, kept);
        }
        catch (Exception ex)
        {
            return new BackupWriteResult(false, ex.Message, "", "", 0);
        }
    }

    /// <summary>
    /// Builds the guarded restore script. It NEVER runs during backup, the user runs it.
    /// Guards, in order: (1) refuse on a different PC model unless cross-machine was allowed
    /// at export; (2) detect and SKIP apps already installed; (3) require a typed confirmation
    /// before installing only the missing apps.
    /// </summary>
    private static string BuildRestoreScript(
        string jsonFileName, int count, IReadOnlyList<AppBackupEntry> manual,
        string sourceMachine, bool allowCrossMachine, bool unattended = false)
    {
        var srcEsc = PsText.Lit(sourceMachine);   // PowerShell single-quote escape (incl. typographic quotes)

        var sb = new StringBuilder();
        // In unattended mode the script runs non-interactively, so it must not block on Read-Host.
        // The model-lock (Guard 1) and already-installed skip (Guard 2) are KEPT; only the typed-Y (Guard 3)
        // and the "Press Enter" pauses are dropped, and it logs to a transcript instead of the console.
        void Pause(string prompt) { if (!unattended) sb.AppendLine($"    Read-Host '{prompt}'"); }

        sb.AppendLine("# Venquery, App Restore Script");
        sb.AppendLine($"# Generated {DateTime.Now:yyyy-MM-dd HH:mm}" + (unattended ? " · unattended" : " · Safe Mode, review before running."));
        sb.AppendLine("#");
        if (!unattended) sb.AppendLine("# Venquery did NOT run this. You run it yourself, after reading it.");
        sb.AppendLine($"# It restores the {count} app(s) captured in {jsonFileName} from the winget catalog.");
        sb.AppendLine("#");
        sb.AppendLine("# Built-in safety:");
        sb.AppendLine("#   1. Apps already installed on this PC are detected and SKIPPED (never reinstalled).");
        sb.AppendLine("#   2. It only runs on the PC model that created it, unless that was allowed at export.");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$here = Split-Path -Parent $MyInvocation.MyCommand.Path");
        if (unattended)
        {
            sb.AppendLine("try { Start-Transcript -Path (Join-Path $here 'restore-log.txt') -Append | Out-Null } catch {}");
        }
        sb.AppendLine($"$backupFile = Join-Path $here '{jsonFileName}'");
        sb.AppendLine($"$SourceMachine = '{srcEsc}'");
        sb.AppendLine("$AllowAnyMachine = $" + (allowCrossMachine ? "true" : "false"));
        sb.AppendLine();
        sb.AppendLine("Write-Host 'Venquery - App Restore'");
        sb.AppendLine("Write-Host ('Backup was created on : ' + $SourceMachine)");
        sb.AppendLine();
        sb.AppendLine("# --- Guard 1: machine-model lock ---------------------------------------------");
        sb.AppendLine("try {");
        sb.AppendLine("    $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop");
        sb.AppendLine("    $thisMachine = (($cs.Manufacturer + ' ' + $cs.Model)).Trim()");
        sb.AppendLine("} catch { $thisMachine = '' }");
        sb.AppendLine("Write-Host ('This PC is            : ' + $thisMachine)");
        sb.AppendLine("# Fail-closed: if WMI could not read this PC's model ($thisMachine empty), REFUSE rather than");
        sb.AppendLine("# silently skipping the lock (the old '-and $thisMachine' guard passed on an unreadable model).");
        sb.AppendLine("if (-not $AllowAnyMachine -and -not $thisMachine) {");
        sb.AppendLine("    Write-Host '  RESTORE BLOCKED - could not read this PC''s model to verify it against the backup.'");
        Pause("Press Enter to close");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        sb.AppendLine("if (-not $AllowAnyMachine -and $SourceMachine -and $thisMachine -and ($SourceMachine -ne $thisMachine)) {");
        sb.AppendLine("    Write-Host '  RESTORE BLOCKED - different PC model'");
        sb.AppendLine("    Write-Host \"  This backup was made on '$SourceMachine' but this PC is '$thisMachine'.\"");
        Pause("Press Enter to close");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# --- Read the backup ---------------------------------------------------------");
        sb.AppendLine("if (-not (Test-Path $backupFile)) {");
        sb.AppendLine("    Write-Host \"Cannot find $backupFile\"");
        Pause("Press Enter to close");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        sb.AppendLine("$ids = @((Get-Content $backupFile -Raw | ConvertFrom-Json).Sources | ForEach-Object { $_.Packages } | ForEach-Object { $_.PackageIdentifier } | Where-Object { $_ })");
        sb.AppendLine("if ($ids.Count -eq 0) { Write-Host 'This backup contains no winget apps.'; exit 0 }");
        sb.AppendLine();
        sb.AppendLine("# --- Guard 2: detect what is already installed (these are skipped) ------------");
        sb.AppendLine("Write-Host 'Checking which apps are already installed (this can take a moment)...'");
        sb.AppendLine("$toInstall = @(); $already = @()");
        sb.AppendLine("foreach ($id in $ids) {");
        sb.AppendLine("    $hit = winget list --id $id -e --accept-source-agreements --disable-interactivity 2>$null | Select-String -SimpleMatch $id");
        sb.AppendLine("    if ($hit) { $already += $id } else { $toInstall += $id }");
        sb.AppendLine("}");
        sb.AppendLine("if ($already.Count -gt 0) {");
        sb.AppendLine("    Write-Host (\"Already installed - will NOT be touched (\" + $already.Count + \"):\")");
        sb.AppendLine("    $already | ForEach-Object { Write-Host ('   - ' + $_) }");
        sb.AppendLine("}");
        sb.AppendLine("if ($toInstall.Count -eq 0) {");
        sb.AppendLine("    Write-Host 'Every app in this backup is already installed. Nothing to do.'");
        Pause("Press Enter to close");
        sb.AppendLine("    exit 0");
        sb.AppendLine("}");
        sb.AppendLine("Write-Host (\"Missing - will be installed (\" + $toInstall.Count + \"):\")");
        sb.AppendLine("$toInstall | ForEach-Object { Write-Host ('   + ' + $_) }");
        sb.AppendLine();
        if (!unattended)
        {
            sb.AppendLine("# --- Guard 3: explicit confirmation before anything installs -----------------");
            sb.AppendLine("$answer = Read-Host (\"Install these \" + $toInstall.Count + \" app(s)? Type Y to proceed\")");
            sb.AppendLine("if ($answer -notmatch '^[Yy]') { Write-Host 'Cancelled - nothing was installed.'; Read-Host 'Press Enter'; exit 0 }");
            sb.AppendLine();
        }
        sb.AppendLine("foreach ($id in $toInstall) {");
        sb.AppendLine("    Write-Host ('Installing ' + $id + ' ...')");
        sb.AppendLine("    winget install --id $id -e --accept-package-agreements --accept-source-agreements --disable-interactivity");
        sb.AppendLine("}");
        sb.AppendLine("Write-Host 'Restore complete. Apps that were already installed were left untouched.'");
        if (!unattended) sb.AppendLine("Read-Host 'Press Enter to close'");
        if (unattended) sb.AppendLine("try { Stop-Transcript | Out-Null } catch {}");

        if (manual.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"# The following {manual.Count} app(s) had no winget package and must be");
            sb.AppendLine("# reinstalled manually from the vendor:");
            foreach (var m in manual)
            {
                var clean = m.Id.Replace("\r", " ").Replace("\n", " ");
                sb.AppendLine($"#   - {clean}");
            }
        }
        return sb.ToString();
    }

    /// <summary>Unattended restore: write an unattended, guarded restore.ps1 + a minimal winget-format
    /// <c>venquery-apps-backup.json</c> for the given app ids, into <paramref name="folder"/>, for a caller
    /// that runs the script at the next sign-in. Reuses the same model-lock + already-installed guards
    /// as the desktop restore, just without the interactive prompts.</summary>
    public BackupWriteResult WriteRestoreFiles(
        IReadOnlyList<string> appIds, string folder, string sourceMachine, bool allowCrossMachine)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var ids = appIds.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList();
            if (ids.Count == 0) return new BackupWriteResult(false, "No app ids.", "", "", 0);

            // Minimal winget-import-shaped document the restore script reads (Sources[].Packages[].PackageIdentifier).
            var doc = new JsonObject
            {
                ["Sources"] = new JsonArray(new JsonObject
                {
                    ["Packages"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["PackageIdentifier"] = id }).ToArray()),
                    ["SourceDetails"] = new JsonObject { ["Name"] = "winget", ["Type"] = "Microsoft.PreIndexed.Package" },
                }),
            };
            var jsonPath = UniquePath(Path.Combine(folder, "venquery-apps-backup.json"));
            File.WriteAllText(jsonPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

            var scriptPath = UniquePath(Path.Combine(folder, "restore-apps.ps1"));
            var script = BuildRestoreScript(Path.GetFileName(jsonPath), ids.Count, Array.Empty<AppBackupEntry>(),
                sourceMachine, allowCrossMachine, unattended: true);
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
            return new BackupWriteResult(true, "", jsonPath, scriptPath, ids.Count);
        }
        catch (Exception ex) { return new BackupWriteResult(false, ex.Message, "", "", 0); }
    }

    // --- parsing -----------------------------------------------------------

    private static List<AppBackupEntry> ParseRestorable(string rawJson)
    {
        var list = new List<AppBackupEntry>();
        try
        {
            var root = JsonNode.Parse(rawJson);
            if (root?["Sources"] is JsonArray sources)
                foreach (var src in sources)
                    if (src?["Packages"] is JsonArray pkgs)
                        foreach (var p in pkgs)
                        {
                            var id = p?["PackageIdentifier"]?.GetValue<string>();
                            if (string.IsNullOrWhiteSpace(id)) continue;
                            var ver = p?["Version"]?.GetValue<string>() ?? "";
                            list.Add(new AppBackupEntry { Id = id, Version = ver, Restorable = true });
                        }
        }
        catch { /* malformed export, surface zero restorable rather than crash */ }
        return list.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // winget prints a line per app it couldn't map to a source, e.g.:
    //   "Installed package is not available from any source: Some App Name"
    private static List<AppBackupEntry> ParseManual(string output)
    {
        var manual = new List<AppBackupEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var idx = line.IndexOf("not available from any source", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var colon = line.IndexOf(':', idx);
            var name = colon >= 0 ? line[(colon + 1)..].Trim() : "";
            if (name.Length > 0 && seen.Add(name))
                manual.Add(new AppBackupEntry { Id = name, Restorable = false });
        }
        return manual.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FirstMeaningfulLine(string s)
        => s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    // --- availability + install (App Installer / winget) -------------------

    /// <summary>The winget to launch, always as an absolute path (never a bare-name search, which would look in
    /// Venquery's own folder and the current folder first, where a look-alike winget.exe could be planted).
    /// Order: the per-user App Execution Alias (also finds a winget installed while Venquery runs, which isn't on
    /// the PATH Venquery inherited), then an explicit walk of PATH, then the machine-wide App Installer package
    /// folder (how a SYSTEM process reaches winget). If none exists the alias path is
    /// returned anyway; starting it then fails and callers treat that as "winget isn't installed".</summary>
    public static string WingetExe()
    {
        var alias = "";
        try
        {
            alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(alias)) return alias;
        }
        catch { /* keep looking */ }
        try
        {
            foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0 || !Path.IsPathRooted(dir)) continue;   // a relative entry would mean "current folder"
                var c = Path.Combine(dir, "winget.exe");
                if (File.Exists(c)) return c;
            }
        }
        catch { /* keep looking */ }
        try
        {
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var best = Directory.GetDirectories(apps, "Microsoft.DesktopAppInstaller_*_8wekyb3d8bbwe")
                .Select(d => (dir: d, ver: Version.TryParse(Path.GetFileName(d).Split('_')[1], out var v) ? v : new Version(0, 0)))
                .OrderByDescending(x => x.ver)
                .Select(x => Path.Combine(x.dir, "winget.exe"))
                .FirstOrDefault(File.Exists);
            if (best is not null) return best;
        }
        catch { /* not listable for a standard user, fine */ }
        return string.IsNullOrEmpty(alias) ? Path.Combine(Environment.SystemDirectory, "winget.exe") : alias;
    }

#if DEBUG
    /// <summary>Debug-only test hook: with VENQUERY_SIMULATE_NO_WINGET=1 set, winget reports missing until
    /// the installer script launched from Venquery exits, so the install flow can be exercised on a PC where
    /// winget can't be removed (it's a non-removable system app on some builds).</summary>
    internal static bool SimulateMissing =
        Environment.GetEnvironmentVariable("VENQUERY_SIMULATE_NO_WINGET") == "1";
#endif

    /// <summary>Off-the-UI-thread <see cref="IsInstalled"/> (it can wait up to 5 s on a slow first run).</summary>
    public static Task<bool> IsInstalledAsync() => Task.Run(IsInstalled);

    /// <summary>True if winget is on this PC. Windows 10/11 usually ship it as "App Installer", but the
    /// LTSC / IoT Enterprise LTSC editions (and Server) don't, so we offer to install it.</summary>
    public static bool IsInstalled()
    {
#if DEBUG
        if (SimulateMissing) return false;
#endif
        try
        {
            var psi = new ProcessStartInfo(WingetExe(), "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }   // not on PATH
        catch { return false; }
    }

    /// <summary>Best-effort Windows edition read. <c>IsLtsc</c> flags the LTSC / IoT Enterprise LTSC / LTSB
    /// editions, which ship without the Store and App Installer (so winget + its MSIX deps are missing).</summary>
    public static (bool IsLtsc, string EditionId, string ProductName) DetectEdition()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var edition = k?.GetValue("EditionID") as string ?? "";
            var product = k?.GetValue("ProductName") as string ?? "";
            bool Has(string s, string sub) => s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
            // EnterpriseS / EnterpriseSN / IoTEnterpriseS are the LTSC EditionIDs; LTSB is the older name.
            var isLtsc = Has(edition, "EnterpriseS") || Has(edition, "LTSC") || Has(product, "LTSC") || Has(product, "LTSB");
            return (isLtsc, edition, product);
        }
        catch { return (false, "", ""); }
    }

    /// <summary>Writes a guarded, readable script that installs winget (Microsoft's "App Installer") and its
    /// MSIX dependencies (VCLibs, VCLibs UWPDesktop, Windows App Runtime) from Microsoft's own official
    /// winget-cli release, for the current user. Venquery only WRITES it; the user runs it (it asks for a
    /// typed Y first). This is the LTSC path where the Store isn't available to do it. No admin needed.</summary>
    public static RepairScriptResult WriteInstallScript(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return new RepairScriptResult(false, "No output folder.", "");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "venquery-install-winget.ps1");
            for (var i = 1; File.Exists(file); i++) file = Path.Combine(folder, $"venquery-install-winget ({i}).ps1");
            File.WriteAllText(file, BuildInstallScript(), new UTF8Encoding(true));
            return new RepairScriptResult(true, "", file);
        }
        catch (Exception ex) { return new RepairScriptResult(false, ex.Message, ""); }
    }

    /// <summary>Opens the generated install script in a visible PowerShell window as the current user (a
    /// per-user App Installer install needs no elevation). <paramref name="onExit"/> runs on a thread-pool
    /// thread with the script's exit code (0 = winget answered, 1 = failed) once the window closes, so the
    /// caller must marshal any UI work. Throws if PowerShell can't be started.</summary>
    public static void LaunchInstallScript(string scriptPath, Action<int> onExit)
    {
        var ps = SystemTools.Exe("powershell.exe");
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(ps, $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            { UseShellExecute = true, WorkingDirectory = Environment.SystemDirectory },
            EnableRaisingEvents = true,
        };
        p.Exited += (_, _) =>
        {
            var code = -1;
            try { code = p.ExitCode; } catch { }
            try { p.Dispose(); } catch { }
#if DEBUG
            SimulateMissing = false;
#endif
            try { onExit(code); } catch { /* a callback failure must never escape onto the thread pool */ }
        };
        p.Start();
    }

    /// <summary>The release Venquery installs from: Microsoft's own winget-cli GitHub release ("latest"
    /// redirects to the current version's assets). The dependency zip holds every MSIX framework the
    /// current App Installer needs, laid out per architecture (x64/, x86/, arm64/).</summary>
    internal const string WingetReleaseBase = "https://github.com/microsoft/winget-cli/releases/latest/download";

    private static string BuildInstallScript()
        => InstallScriptTemplate
            .Replace("__GENERATED__", DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
            .Replace("__RELEASE__", WingetReleaseBase);

    // Kept free of .NET method calls beyond a guarded TLS tweak so it also runs under Constrained Language
    // Mode (common on locked-down IoT/LTSC images). Variables before a ':' are written ${name} because
    // PowerShell would otherwise read "$name:" as a drive-qualified variable.
    private const string InstallScriptTemplate = """
# Venquery, install winget (Microsoft App Installer) + its dependencies
# Generated __GENERATED__, review before running.
#
# Why: some Windows editions (LTSC / IoT Enterprise LTSC, Server) ship WITHOUT the Store and
# App Installer, so 'winget' and the MSIX frameworks it needs aren't present. This downloads
# them from Microsoft's own winget-cli release and installs them for the current user (no
# administrator needed). Sources, all Microsoft-owned:
#   Dependencies : __RELEASE__/DesktopAppInstaller_Dependencies.zip
#                  (Microsoft.VCLibs.140.00, VCLibs UWPDesktop, Microsoft.WindowsAppRuntime)
#   App Installer: __RELEASE__/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle
#                  (fallback: https://aka.ms/getwinget)
#
# Safety: it lists what it will do, then waits for you to type Y. Nothing is downloaded before
# that. A log of the run is saved next to this script (venquery-install-winget.log).

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell's progress bar makes big downloads crawl
$release = '__RELEASE__'
$logFile = Join-Path $PSScriptRoot 'venquery-install-winget.log'
try { Start-Transcript -Path $logFile -Force | Out-Null } catch { }

$arch = $env:PROCESSOR_ARCHITEW6432
if (-not $arch) { $arch = $env:PROCESSOR_ARCHITECTURE }
switch ($arch) {
    'AMD64' { $arch = 'x64' }
    'ARM64' { $arch = 'arm64' }
    'x86'   { $arch = 'x86' }
    default { Write-Host "Unsupported processor architecture '$arch'." -ForegroundColor Red; Read-Host 'Press Enter to close'; exit 1 }
}

Write-Host ''
Write-Host 'Venquery, install winget (Microsoft App Installer)' -ForegroundColor Cyan
Write-Host "This downloads about 320 MB from Microsoft's winget-cli release and installs, for the current user:"
Write-Host '  - VCLibs, VCLibs UWPDesktop and the Windows App Runtime (the frameworks winget needs)'
Write-Host '  - App Installer (winget)'
Write-Host "  Processor: $arch"
$answer = Read-Host 'Do this now? Type Y to proceed'
if ($answer -notmatch '^[Yy]') {
    Write-Host 'Cancelled, nothing was changed.'
    try { Stop-Transcript | Out-Null } catch { }
    Read-Host 'Press Enter to close'
    exit 2
}

$failed = $false
$tmp = Join-Path $env:TEMP ('venquery-winget-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
try {
    # Older Windows 10 / Server builds don't offer TLS 1.2 by default; GitHub requires it.
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null

    Write-Host ''
    Write-Host '[1/4] Downloading the dependencies (about 100 MB)...' -ForegroundColor Cyan
    $depsZip = Join-Path $tmp 'deps.zip'
    Invoke-WebRequest -Uri "$release/DesktopAppInstaller_Dependencies.zip" -OutFile $depsZip -UseBasicParsing
    Expand-Archive -Path $depsZip -DestinationPath (Join-Path $tmp 'deps') -Force
    $deps = @(Get-ChildItem -LiteralPath (Join-Path $tmp "deps\$arch") -Filter '*.appx' -ErrorAction SilentlyContinue)
    if ($deps.Count -eq 0) { throw "The dependency package has no files for $arch." }

    Write-Host '[2/4] Downloading App Installer (about 220 MB)...' -ForegroundColor Cyan
    $bundle = Join-Path $tmp 'AppInstaller.msixbundle'
    try { Invoke-WebRequest -Uri "$release/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle" -OutFile $bundle -UseBasicParsing }
    catch {
        Write-Host '  The GitHub download failed, trying https://aka.ms/getwinget instead...' -ForegroundColor Yellow
        Invoke-WebRequest -Uri 'https://aka.ms/getwinget' -OutFile $bundle -UseBasicParsing
    }

    Write-Host '[3/4] Installing the dependencies...' -ForegroundColor Cyan
    foreach ($d in $deps) {
        # The files are named <Name>_<Version>_<arch>.appx
        $parts = $d.BaseName -split '_'
        $name = $parts[0]
        $ver = [version]$parts[1]
        $have = Get-AppxPackage -Name $name | Where-Object { $_.Architecture -eq $arch -and [version]$_.Version -ge $ver } | Select-Object -First 1
        if ($have) { Write-Host "  ${name} $($have.Version) is already installed, OK"; continue }
        try {
            Add-AppxPackage -Path $d.FullName
            Write-Host "  ${name} $ver installed" -ForegroundColor Green
        }
        catch {
            if ($_.Exception.Message -match '0x80073D06') { Write-Host "  ${name}: a newer version is already installed, OK" }
            else { throw "Could not install ${name}: $($_.Exception.Message)" }
        }
    }

    Write-Host '[4/4] Installing App Installer (winget)...' -ForegroundColor Cyan
    try { Add-AppxPackage -Path $bundle }
    catch {
        if ($_.Exception.Message -match '0x80073D06') { Write-Host '  A newer App Installer is already installed, OK' }
        else { throw "Could not install App Installer: $($_.Exception.Message)" }
    }

    # Make sure the 'winget' command alias is registered (Microsoft's own documented repair step).
    $wg = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
    if (-not (Test-Path -LiteralPath $wg)) {
        try { Add-AppxPackage -RegisterByFamilyName -MainPackage 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' } catch { }
        Start-Sleep -Seconds 3
    }
    $v = $null
    try { $v = & $wg --version } catch { }
    Write-Host ''
    if ($v) { Write-Host "Done. winget $v is installed. Go back to Venquery, it picks it up when this window closes." -ForegroundColor Green }
    else {
        $failed = $true
        Write-Host 'The packages installed, but winget did not answer yet. Sign out and back in, then try again in Venquery.' -ForegroundColor Yellow
    }
}
catch {
    $failed = $true
    Write-Host ''
    Write-Host ('  [error] ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host "  A log of this run is saved at: $logFile"
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
try { Stop-Transcript | Out-Null } catch { }
Read-Host 'Press Enter to close'
if ($failed) { exit 1 } else { exit 0 }
""";

    // --- process runner ----------------------------------------------------

    private static async Task<(bool started, int exit, string stdout, string stderr)> RunAsync(
        string args, IProgress<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(WingetExe(), args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,               // no console flash, this is a GUI app
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return (false, -1, "", "");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, -1, "", "");           // winget.exe not found on PATH
        }

        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        // Guard against a hung winget with a generous ceiling; the user can also rescan.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(true); } catch { }
            log?.Report("  winget timed out after 3 minutes");
        }

        var stdout = await outTask;
        var stderr = await errTask;
        return (true, proc.HasExited ? proc.ExitCode : -1, stdout, stderr);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
