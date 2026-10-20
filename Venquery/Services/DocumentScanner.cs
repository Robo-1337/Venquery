using System.Diagnostics;
using System.IO;

namespace Venquery.Services;

/// <summary>The kind of personal content an item holds. Flags, because a folder can hold several kinds.</summary>
[Flags]
public enum DocCategory
{
    None = 0,
    Documents = 1,
    Pictures = 2,
    Videos = 4,
    Audio = 8,
    Archives = 16,
    DiskImages = 32,
}

/// <summary>One backable item found in a personal folder (Documents / Pictures / Videos / Music /
/// Downloads / Desktop) or the C: root: either a loose personal file or a folder that holds personal
/// content (and no executables). <see cref="Categories"/> is the content kind(s) it holds.</summary>
public sealed record DocItem(string FullPath, string Name, bool IsFolder, long SizeBytes, int FileCount, DocCategory Categories, DateTime Modified);

public sealed record DocScanResult(
    IReadOnlyList<DocItem> Items,
    int RootsScanned,
    long TotalBytes,
    TimeSpan Elapsed,
    bool Truncated,
    bool Cancelled);

/// <summary>
/// Finds personal content a factory reset would wipe: documents, pictures, video, audio and archives in your
/// personal folders (Documents, Pictures, Videos, Music, Downloads, Desktop) or dumped straight in C:\.
/// A folder qualifies only if it holds at least one such file and NO executable (so app/tool folders are left
/// to the Portable Apps scanner); empty or junk folders are skipped. Each item is tagged with its content
/// category so the UI can filter by type and a backup can sort by type. Read-only, it inspects names,
/// extensions and sizes, never file contents. System/hidden folders and reparse points are skipped, depth and
/// result count are capped, and it is cancellable.
/// </summary>
public static class DocumentScanner
{
    private const int MaxDepth = 16;
    private const int MaxItems = 1000;

    // Content extensions, partitioned by category. The union is what counts as "worth keeping".
    private static readonly HashSet<string> DocExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".ods",
        ".odp", ".csv", ".tsv", ".md", ".pages", ".numbers", ".key", ".epub", ".mobi", ".tex", ".one",
        ".docm", ".dotx", ".dotm", ".xlsm", ".xltx", ".xlsb", ".pptm", ".potx", ".ppsx", ".pub",
        ".vsd", ".vsdx", ".accdb", ".mdb", ".xps", ".oxps", ".odg",
        ".wpd", ".wps", ".djvu",
        // Mail, contacts and calendars: saved messages, Outlook data files (.pst is the user's own archive;
        // .ost is a re-downloadable cache and deliberately left out), address-book and calendar exports.
        ".eml", ".msg", ".pst", ".vcf", ".ics",
    };
    private static readonly HashSet<string> PicExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".svg",
        ".psd", ".ai", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2",
        ".avif", ".jxl", ".jfif", ".raf", ".pef", ".srw", ".eps", ".xcf",
    };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg",
        // (.ts / .mts are left out on purpose: they are far more often TypeScript source than video.)
        ".3gp", ".3g2", ".m2ts", ".vob", ".ogv", ".asf", ".divx", ".rm", ".rmvb",
    };
    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma",
        ".opus", ".aiff", ".aif", ".mid", ".midi", ".amr", ".m4b", ".ape", ".wv",
    };
    private static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".zst",
    };
    // Disk images: OS/installer ISOs, raw and virtual-machine disks, Windows images, Mac images, CD images.
    // (.bin and .mdf are left out on purpose: .bin is mostly firmware/game data and .mdf is also SQL Server's
    // data-file extension.) Usually very large, so they get their own category the user can switch off.
    private static readonly HashSet<string> DiskImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".vdi", ".qcow2", ".dmg", ".wim", ".esd", ".nrg", ".cue",
    };

    /// <summary>The content category of a file by its extension (None if it isn't backable content).</summary>
    public static DocCategory CategoryOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (DocExts.Contains(ext)) return DocCategory.Documents;
        if (PicExts.Contains(ext)) return DocCategory.Pictures;
        if (VideoExts.Contains(ext)) return DocCategory.Videos;
        if (AudioExts.Contains(ext)) return DocCategory.Audio;
        if (ArchiveExts.Contains(ext)) return DocCategory.Archives;
        if (DiskImageExts.Contains(ext)) return DocCategory.DiskImages;
        return DocCategory.None;
    }

    /// <summary>All five categories OR'd together (the "everything" mask).</summary>
    public const DocCategory AllCategories =
        DocCategory.Documents | DocCategory.Pictures | DocCategory.Videos | DocCategory.Audio | DocCategory.Archives
        | DocCategory.DiskImages;

    /// <summary>A human label for the category flag(s), e.g. "Documents, Pictures".</summary>
    public static string CategoriesLabel(DocCategory c)
    {
        if (c == DocCategory.None) return "";
        var parts = new List<string>();
        if (c.HasFlag(DocCategory.Documents)) parts.Add("Documents");
        if (c.HasFlag(DocCategory.Pictures)) parts.Add("Pictures");
        if (c.HasFlag(DocCategory.Videos)) parts.Add("Videos");
        if (c.HasFlag(DocCategory.Audio)) parts.Add("Audio");
        if (c.HasFlag(DocCategory.Archives)) parts.Add("Archives");
        if (c.HasFlag(DocCategory.DiskImages)) parts.Add("Disk images");
        return string.Join(", ", parts);
    }

    /// <summary>The sub-folder name a backup files a single category under.</summary>
    public static string CategoryFolder(DocCategory c) => c switch
    {
        DocCategory.Documents => "Documents",
        DocCategory.Pictures => "Pictures",
        DocCategory.Videos => "Videos",
        DocCategory.Audio => "Audio",
        DocCategory.Archives => "Archives",
        DocCategory.DiskImages => "Disk Images",
        _ => "Other",
    };

    // Presence of any of these marks a folder as an app / system folder, so it's excluded.
    private static readonly HashSet<string> ExeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".msi", ".msix", ".appx", ".com", ".scr", ".drv", ".ocx", ".cpl",
    };

    // Top-level C:\ folders that are never personal data.
    private static readonly HashSet<string> ExcludedRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users",
        "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "$WinREAgent",
        "Config.Msi", "MSOCache", "Intel", "AMD", "NVIDIA", "$SysReset", "OneDriveTemp",
    };

    /// <summary>What "scan the Windows drive" means: the user's personal content folders, Documents /
    /// Pictures / Videos / Music / Downloads (and their Public counterparts), plus the Desktop and the C:\
    /// root. These are the places a Windows reset wipes, so this is where personal files worth backing up
    /// actually live. Known-folder paths follow any OneDrive redirection automatically (e.g. Documents
    /// redirected into OneDrive). Nothing scans these unless the user picks the OS drive, see
    /// <see cref="RootsFor"/>.</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        string F(Environment.SpecialFolder sf) => Environment.GetFolderPath(sf);
        var roots = new List<string>
        {
            F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.Personal),          // Documents ("My Documents")
            F(Environment.SpecialFolder.MyPictures),
            F(Environment.SpecialFolder.MyVideos),
            F(Environment.SpecialFolder.MyMusic),
            DownloadService.DownloadsFolder(),              // Downloads
            // Public / shared versions of the same folders.
            F(Environment.SpecialFolder.CommonDesktopDirectory),
            F(Environment.SpecialFolder.CommonDocuments),
            F(Environment.SpecialFolder.CommonPictures),
            F(Environment.SpecialFolder.CommonVideos),
            F(Environment.SpecialFolder.CommonMusic),
            OsDriveRoot(),                                  // loose files dumped straight in C:\
        };
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The scan roots for the drives the user picked. The OS drive expands to
    /// <see cref="DefaultRoots"/> (the personal folders, which live under C:\Users and so aren't reached by
    /// the root pass, plus the C:\ root itself); any other drive is scanned from its root. De-duplicated, and
    /// empty when nothing was picked, the scanner never adds C: on its own.</summary>
    public static IReadOnlyList<string> RootsFor(IEnumerable<string> pickedDriveRoots)
    {
        var os = OsDriveRoot();
        var roots = new List<string>();
        foreach (var d in pickedDriveRoots)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            if (string.Equals(d.TrimEnd('\\', '/') + "\\", os, StringComparison.OrdinalIgnoreCase))
                roots.AddRange(DefaultRoots());
            else roots.Add(d);
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Venquery's own output folders (backups, reports, diagnostics) live under Downloads / next to the exe;
    // skip them so a re-scan doesn't offer to back up a previous backup.
    private static readonly HashSet<string> VenqueryOutputFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Portable Apps", "Portable Documents", "SystemReports", "Driver Backup", "Venquery Diagnostics",
    };
    private static bool IsVenqueryOutput(string folderName)
        => VenqueryOutputFolders.Contains(folderName)
        || folderName.StartsWith("Venquery Backup", StringComparison.OrdinalIgnoreCase);

    /// <summary>The roots of other ready local drives (fixed + removable) besides the OS drive, for
    /// the optional "also scan other drives" pass. Network and CD/DVD drives are deliberately skipped
    /// so the scan can't hang on a slow share or spin up optical media. (A Windows reset only wipes the
    /// OS drive, so these usually survive, this is for rounding up files scattered across every drive.)</summary>
    public static IReadOnlyList<string> OtherDriveRoots()
    {
        var os = OsDriveRoot();
        var roots = new List<string>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { return roots; }
        foreach (var d in drives)
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!d.IsReady) continue;
                var root = d.RootDirectory.FullName;
                if (string.Equals(root, os, StringComparison.OrdinalIgnoreCase)) continue;
                roots.Add(root);
            }
            catch { /* a drive that vanished mid-enumeration or errors on query, skip it */ }
        }
        return roots;
    }

    private static string OsDriveRoot()
        => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

    public static Task<DocScanResult> ScanAsync(
        IReadOnlyList<string> roots,
        IProgress<(int items, string current)>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(roots, progress, ct), ct);

    private static DocScanResult Scan(
        IReadOnlyList<string> roots, IProgress<(int, string)>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var items = new List<DocItem>();
        long totalBytes = 0;
        int rootsScanned = 0;
        var truncated = false;

        foreach (var root in roots)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            var isDriveRoot = IsDriveRoot(root);
            rootsScanned++;

            // Loose files sitting directly at the root.
            foreach (var file in SafeGetFiles(root))
            {
                if (ct.IsCancellationRequested) break;
                if (items.Count >= MaxItems) { truncated = true; break; }
                var cat = CategoryOf(file);
                if (cat == DocCategory.None) continue;
                long size = SafeSize(file);
                items.Add(new DocItem(file, Path.GetFileName(file), false, size, 1, cat, SafeMtime(file)));
                totalBytes += size;
            }

            // Immediate subfolders, each is a candidate "documents folder".
            foreach (var dir in SafeGetDirs(root))
            {
                if (ct.IsCancellationRequested) break;
                if (items.Count >= MaxItems) { truncated = true; break; }

                var folderName = Path.GetFileName(dir);
                if (isDriveRoot && ExcludedRootFolders.Contains(folderName)) continue;
                if (IsVenqueryOutput(folderName)) continue;   // don't offer to back up our own backups
                if (IsHiddenOrSystemOrReparse(dir)) continue;

                progress?.Report((items.Count, folderName));

                var (contentFiles, hasExe, size, cats, newest) = Inspect(dir, 0, ct);
                if (hasExe || contentFiles == 0) continue;   // app/system folder, or nothing worth keeping

                items.Add(new DocItem(dir, folderName, true, size, contentFiles, cats, newest));
                totalBytes += size;
            }
        }

        sw.Stop();
        // Roots can overlap (a picked folder inside a ticked drive or inside a personal folder): keep each path
        // once, and drop anything that sits inside a folder item already listed, so a backup never copies the
        // same files twice.
        items = DropCovered(items);
        totalBytes = items.Sum(i => i.SizeBytes);
        var ordered = items
            .OrderByDescending(i => i.IsFolder)                        // folders first
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DocScanResult(ordered, rootsScanned, totalBytes, sw.Elapsed, truncated, ct.IsCancellationRequested);
    }

    internal static List<DocItem> DropCovered(List<DocItem> items)
    {
        var unique = items.GroupBy(i => i.FullPath.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var folders = unique.Where(i => i.IsFolder).Select(i => i.FullPath.TrimEnd('\\') + "\\").ToList();
        return unique.Where(i => !folders.Any(f => i.FullPath.StartsWith(f, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Walks a candidate folder: counts personal-content files, notes whether any executable is
    /// present, sums size, and accumulates which content categories it holds. Stops early on an executable.</summary>
    private static (int contentFiles, bool hasExe, long size, DocCategory categories, DateTime newest) Inspect(string dir, int depth, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || depth > MaxDepth) return (0, false, 0, DocCategory.None, DateTime.MinValue);
        if (depth > 0 && IsHiddenOrSystemOrReparse(dir)) return (0, false, 0, DocCategory.None, DateTime.MinValue);

        int content = 0;
        long size = 0;
        var cats = DocCategory.None;
        var newest = DateTime.MinValue;
        foreach (var file in SafeGetFiles(dir))
        {
            var ext = Path.GetExtension(file);
            if (ExeExts.Contains(ext)) return (content, true, size, cats, newest);   // it's an app/system folder
            var cat = CategoryOf(file);
            if (cat != DocCategory.None)
            {
                content++; cats |= cat;
                var m = SafeMtime(file);
                if (m > newest) newest = m;
            }
            size += SafeSize(file);
        }

        foreach (var sub in SafeGetDirs(dir))
        {
            if (ct.IsCancellationRequested) break;
            var (c, exe, s, sc, sn) = Inspect(sub, depth + 1, ct);
            if (exe) return (content + c, true, size + s, cats | sc, newest > sn ? newest : sn);
            content += c;
            size += s;
            cats |= sc;
            if (sn > newest) newest = sn;
        }
        return (content, false, size, cats, newest);
    }

    private static bool IsDriveRoot(string path)
    {
        try { return string.Equals(Path.GetFullPath(path), Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsHiddenOrSystemOrReparse(string dir)
    {
        try
        {
            var a = new DirectoryInfo(dir).Attributes;
            return a.HasFlag(FileAttributes.ReparsePoint) || a.HasFlag(FileAttributes.System) || a.HasFlag(FileAttributes.Hidden);
        }
        catch { return true; }   // unreadable → treat as skip
    }

    private static string[] SafeGetFiles(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch { return Array.Empty<string>(); }
    }

    private static string[] SafeGetDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    private static long SafeSize(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }

    private static DateTime SafeMtime(string file)
    {
        try { return new FileInfo(file).LastWriteTime; } catch { return DateTime.MinValue; }
    }
}
