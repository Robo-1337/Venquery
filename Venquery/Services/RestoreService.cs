using System.IO;

namespace Venquery.Services;

public enum RestorePartKind { Apps, PortableApps, Documents, Drivers }

/// <summary>One restorable part found in a backup folder.</summary>
public sealed record RestorePart(
    RestorePartKind Kind,
    string Path,
    string Title,
    string Summary,
    bool Available,
    string ScriptPath = "",
    bool SortedByType = false,
    int Items = 0,
    long Bytes = 0);

/// <summary>What a backup folder contains.</summary>
public sealed record BackupInventory(string Root, IReadOnlyList<RestorePart> Parts)
{
    public bool IsEmpty => Parts.Count == 0;
}

/// <summary>
/// Restoring from a Venquery backup: a "Venquery Backup - (PC)" folder from Prepare for Reset, or a standalone
/// "Portable Apps" / "Portable Documents" backup. Copies only ADD files (never overwrite: a taken name becomes
/// "name (2).ext"; documents land in a dated "Restored from backup" folder so nothing collides). Installed apps and
/// drivers are restored by the backup's own guarded scripts (the driver script is hardware-locked and refuses on a
/// different PC); Venquery only launches them after a confirmation. Windows' own file system + PowerShell only.
/// </summary>
public static class RestoreService
{
    public const string RestoredFolderPrefix = "Restored from backup";

    /// <summary>Backup folders Venquery can see without asking: next to Venquery, in Downloads, on the Desktop, and at
    /// the top of any removable drive (a USB stick). Newest first.</summary>
    public static IReadOnlyList<string> FindBackups()
    {
        var places = new List<string> { OutputLocation.Base(), DownloadService.DownloadsFolder(),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        try
        {
            foreach (var d in DriveInfo.GetDrives())
                try { if (d.IsReady && d.DriveType == DriveType.Removable) places.Add(d.RootDirectory.FullName); } catch { }
        }
        catch { }
        var found = new List<(string Path, DateTime When)>();
        foreach (var place in places.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(place); } catch { continue; }
            foreach (var d in dirs)
            {
                var n = System.IO.Path.GetFileName(d);
                if (n.StartsWith("Venquery Backup", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Portable Apps", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Portable Documents", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime when = DateTime.MinValue; try { when = Directory.GetLastWriteTime(d); } catch { }
                    if (!Inspect(d).IsEmpty) found.Add((d, when));
                }
            }
        }
        return found.OrderByDescending(f => f.When).Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Works out what <paramref name="folder"/> holds. The folder may be a full Prepare-for-Reset backup or
    /// one of its parts.</summary>
    public static BackupInventory Inspect(string folder)
    {
        var parts = new List<RestorePart>();
        if (!Directory.Exists(folder)) return new BackupInventory(folder, parts);
        var name = System.IO.Path.GetFileName(folder.TrimEnd('\\'));

        if (name.Equals("Portable Apps", StringComparison.OrdinalIgnoreCase)) { AddPortable(folder, parts); return new BackupInventory(folder, parts); }
        if (name.Equals("Portable Documents", StringComparison.OrdinalIgnoreCase) || name.Equals("Documents", StringComparison.OrdinalIgnoreCase))
        { AddDocuments(folder, parts); return new BackupInventory(folder, parts); }
        if (name.Equals("Drivers", StringComparison.OrdinalIgnoreCase)) { AddDrivers(folder, parts); return new BackupInventory(folder, parts); }

        // Installed apps: the winget manifest + its restore script, here or in Apps\.
        foreach (var appsDir in new[] { System.IO.Path.Combine(folder, "Apps"), folder })
        {
            var json = SafeFiles(appsDir, "venquery-apps-backup*.json").OrderByDescending(f => f).FirstOrDefault();
            if (json is null) continue;
            var script = System.IO.Path.ChangeExtension(json, ".ps1");
            if (!File.Exists(script)) script = SafeFiles(appsDir, "restore-apps*.ps1").FirstOrDefault() ?? "";
            var count = CountApps(json);
            parts.Add(new RestorePart(RestorePartKind.Apps, appsDir, "Installed apps",
                File.Exists(script) ? $"{count} app(s) reinstalled with Windows' own winget (the backup's guarded restore script)"
                                    : "The app list is here, but its restore script is missing",
                File.Exists(script), script, Items: count));
            break;
        }
        var portable = System.IO.Path.Combine(folder, "Portable Apps");
        if (Directory.Exists(portable)) AddPortable(portable, parts);
        foreach (var docs in new[] { "Documents", "Portable Documents" })
        {
            var d = System.IO.Path.Combine(folder, docs);
            if (Directory.Exists(d)) AddDocuments(d, parts);
        }
        var drivers = System.IO.Path.Combine(folder, "Drivers");
        if (Directory.Exists(drivers) || File.Exists(System.IO.Path.Combine(folder, "venquery-driver-backup.ps1"))) AddDrivers(drivers, parts);
        return new BackupInventory(folder, parts);
    }

    private static void AddPortable(string dir, List<RestorePart> parts)
    {
        var apps = SafeDirs(dir).Length;
        var (files, bytes) = Measure(dir);
        if (apps == 0) return;
        parts.Add(new RestorePart(RestorePartKind.PortableApps, dir, "Portable apps",
            $"{apps} app folder(s), {OrganizerService.FormatBytes(bytes)}, copied to a folder you choose", true, Items: apps, Bytes: bytes));
    }

    private static void AddDocuments(string dir, List<RestorePart> parts)
    {
        var (files, bytes) = Measure(dir);
        if (files == 0) return;
        // A Loose Documents backup is sorted into Documents/Pictures/Videos/Audio/Archives/Disk Images folders.
        var sorted = SafeDirs(dir).Select(System.IO.Path.GetFileName)
            .Any(n => KnownCategory(n!) is not null);
        parts.Add(new RestorePart(RestorePartKind.Documents, dir, "Documents, pictures and media",
            $"{files} file(s), {OrganizerService.FormatBytes(bytes)}, copied back into your Pictures, Videos, Music and Documents folders",
            true, SortedByType: sorted, Items: files, Bytes: bytes));
    }

    private static void AddDrivers(string dir, List<RestorePart> parts)
    {
        var script = System.IO.Path.Combine(dir, "restore-drivers.ps1");
        var ok = File.Exists(script);
        var infs = ok ? SafeFilesDeep(dir, "*.inf") : 0;
        parts.Add(new RestorePart(RestorePartKind.Drivers, dir, "Drivers",
            ok ? $"{infs} driver package(s), installed by the backup's own hardware-locked script (Administrator, refuses on a different PC)"
               : "The driver export was never run, so there are no drivers to restore",
            ok, ok ? script : "", Items: infs));
    }

    /// <summary>"Pictures" → the Windows Pictures folder, etc.; null for a folder that isn't a known category.</summary>
    internal static Environment.SpecialFolder? KnownCategory(string folderName) => folderName.ToLowerInvariant() switch
    {
        "documents" => Environment.SpecialFolder.Personal,
        "pictures" => Environment.SpecialFolder.MyPictures,
        "videos" => Environment.SpecialFolder.MyVideos,
        "audio" or "music" => Environment.SpecialFolder.MyMusic,
        "archives" or "disk images" or "other" => Environment.SpecialFolder.Personal,
        _ => null,
    };

    /// <summary>Copies documents back. With <paramref name="targetFolder"/> null they go into the Windows library
    /// folders (a type-sorted backup goes Pictures→Pictures, Videos→Videos, Audio→Music, everything else→Documents),
    /// each time inside a dated "Restored from backup" folder. With a target folder, everything goes there (keeping the
    /// type folders). Copies never overwrite.</summary>
    public static async Task<PortableBackupResult> RestoreDocumentsAsync(RestorePart part, string? targetFolder,
        IProgress<(int done, int total, string current)>? progress = null, CancellationToken ct = default, BackupReceipt? receipt = null)
    {
        var stamp = $"{RestoredFolderPrefix} {DateTime.Now:yyyy-MM-dd}";
        if (!string.IsNullOrWhiteSpace(targetFolder))
            return await PortableBackupService.BackupAsync(SafeEntries(part.Path), System.IO.Path.Combine(targetFolder!, stamp), progress, ct, receipt);

        var notes = new List<string>();
        int copied = 0, skipped = 0;
        var cancelled = false;
        string lastDest = "";
        var groups = new List<(string[] Sources, string Dest)>();
        if (part.SortedByType)
        {
            foreach (var cat in SafeDirs(part.Path))
            {
                var lib = KnownCategory(System.IO.Path.GetFileName(cat)) ?? Environment.SpecialFolder.Personal;
                var dest = System.IO.Path.Combine(Environment.GetFolderPath(lib), stamp);
                if (lib == Environment.SpecialFolder.Personal && KnownCategory(System.IO.Path.GetFileName(cat)) is not null
                    && !System.IO.Path.GetFileName(cat).Equals("Documents", StringComparison.OrdinalIgnoreCase))
                    dest = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(cat));   // Archives / Disk Images keep their own folder
                groups.Add((SafeEntries(cat), dest));
            }
            var loose = SafeFiles(part.Path, "*").Where(f => !System.IO.Path.GetFileName(f).StartsWith("Backup receipt", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (loose.Length > 0) groups.Add((loose, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), stamp)));
        }
        else
        {
            groups.Add((SafeEntries(part.Path), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), stamp)));
        }
        foreach (var (sources, dest) in groups)
        {
            if (sources.Length == 0) continue;
            var r = await PortableBackupService.BackupAsync(sources, dest, progress, ct, receipt);
            copied += r.Copied; skipped += r.Skipped; notes.AddRange(r.Notes);
            lastDest = dest;
            if (r.Cancelled) { cancelled = true; break; }
            if (!r.Ok) notes.Add($"Couldn't write to {dest}: {r.Error}");
        }
        return new PortableBackupResult(true, "", lastDest, copied, skipped, notes, cancelled);
    }

    /// <summary>Copies the backed-up portable app folders into <paramref name="targetFolder"/> (never overwriting).</summary>
    public static Task<PortableBackupResult> RestorePortableAsync(RestorePart part, string targetFolder,
        IProgress<(int done, int total, string current)>? progress = null, CancellationToken ct = default, BackupReceipt? receipt = null)
        => PortableBackupService.BackupAsync(SafeDirs(part.Path), targetFolder, progress, ct, receipt);

    /// <summary>Where restored portable apps go by default: a "Portable Apps" folder in the user's profile.</summary>
    public static string DefaultPortableTarget()
        => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Portable Apps");

    // ---- helpers -------------------------------------------------------------------------------------------

    private static int CountApps(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            var n = 0;
            if (doc.RootElement.TryGetProperty("Sources", out var sources))
                foreach (var s in sources.EnumerateArray())
                    if (s.TryGetProperty("Packages", out var pk)) n += pk.GetArrayLength();
            return n;
        }
        catch { return 0; }
    }

    /// <summary>Everything directly inside a folder except Venquery's own receipts.</summary>
    private static string[] SafeEntries(string dir)
        => SafeDirs(dir).Concat(SafeFiles(dir, "*").Where(f => !System.IO.Path.GetFileName(f).StartsWith("Backup receipt", StringComparison.OrdinalIgnoreCase))).ToArray();

    private static (int files, long bytes) Measure(string dir)
    {
        int files = 0; long bytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (System.IO.Path.GetFileName(f).StartsWith("Backup receipt", StringComparison.OrdinalIgnoreCase)) continue;
                files++;
                try { bytes += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return (files, bytes);
    }

    private static int SafeFilesDeep(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Count(); } catch { return 0; }
    }

    private static string[] SafeFiles(string dir, string pattern) { try { return Directory.GetFiles(dir, pattern); } catch { return Array.Empty<string>(); } }
    private static string[] SafeDirs(string dir) { try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); } }
}
