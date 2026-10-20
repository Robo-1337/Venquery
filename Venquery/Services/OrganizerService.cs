using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Venquery.Services;

// ---------------------------------------------------------------------------
// File Organizer: tidy a folder (typically Downloads or the Desktop) by sorting loose files into folders by
// type. This is the first Venquery feature that MOVES the user's existing files (everything else only writes
// new ones), so it follows strict rules:
//   - nothing moves until the user has seen a preview and confirmed it;
//   - nothing is ever deleted or overwritten (a name clash becomes "name (2).ext");
//   - every run writes an undo log, and "Undo" puts every file back where it was;
//   - it never touches system locations, app folders, code projects, hidden/system files, shortcuts, or
//     files that are still downloading.
// WPF-free (testable) and .NET Framework compatible, so it ships in every edition including Legacy.
// ---------------------------------------------------------------------------

/// <summary>The folder a file is sorted into.</summary>
public enum OrgKind { Pictures, Videos, Music, Documents, Archives, DiskImages, Installers, Code, Fonts, Other }

/// <summary>Where "organize by type" puts things.</summary>
public enum OrgDestination
{
    /// <summary>Type folders inside the folder being organized (Downloads\Pictures, Downloads\Videos, ...).</summary>
    SubfoldersHere,
    /// <summary>Pictures/Videos/Music/Documents go to the user's own Windows library folders; the other types
    /// still get a subfolder here.</summary>
    WindowsLibraries,
}

/// <summary>Optional extra folder level by date (from the file's last-modified time).</summary>
public enum OrgDateSplit { None, Year, YearMonth }

/// <summary>One file found by an organizer scan. Selection + duplicate marks are UI state.</summary>
public sealed class OrgFile : INotifyPropertyChanged
{
    public string FullPath { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Folder relative to the scanned root ("" = directly in the root).</summary>
    public string RelativeFolder { get; init; } = "";
    public OrgKind Kind { get; init; }
    public long SizeBytes { get; init; }
    public DateTime Modified { get; init; }

    public string KindLabel => OrganizerService.KindLabel(Kind);
    public string SizeText => OrganizerService.FormatBytes(SizeBytes);
    public string DateText => Modified == DateTime.MinValue ? "" : Modified.ToString("yyyy-MM-dd");
    public string Where => RelativeFolder.Length == 0 ? "" : RelativeFolder;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnChanged(nameof(IsSelected)); } }

    /// <summary>Set by a duplicate search: "keep" for the copy that stays, "duplicate" for an extra copy.</summary>
    private string _dupMark = "";
    public string DupMark { get => _dupMark; set { _dupMark = value ?? ""; OnChanged(nameof(DupMark)); OnChanged(nameof(HasDupMark)); } }
    public bool HasDupMark => _dupMark.Length > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed record OrgScanResult(IReadOnlyList<OrgFile> Files, int SkippedFiles, int SkippedFolders,
    bool Truncated, bool Cancelled, TimeSpan Elapsed);

/// <summary>One planned move: <see cref="Source"/> goes into <see cref="DestFolder"/> (keeping its name, or
/// "name (2).ext" if that name is taken when the move runs).</summary>
public sealed record OrgMove(string Source, string DestFolder, long SizeBytes, string Label);

public sealed record OrgPlan(string Root, string Action, IReadOnlyList<OrgMove> Moves, int AlreadyInPlace)
{
    public long TotalBytes => Moves.Sum(m => m.SizeBytes);
    /// <summary>The moves grouped by destination folder, for the preview ("212 files → Pictures").</summary>
    public IReadOnlyList<(string DestFolder, string Label, int Count, long Bytes)> Groups =>
        Moves.GroupBy(m => m.DestFolder, StringComparer.OrdinalIgnoreCase)
             .Select(g => (g.Key, g.First().Label, g.Count(), g.Sum(m => m.SizeBytes)))
             .OrderByDescending(g => g.Item3).ToList();
}

public sealed record OrgRunResult(int Moved, int Skipped, IReadOnlyList<string> Notes, string LogPath, bool Cancelled);

/// <summary>A past organize run that can be undone (unless it was undone already or the user kept it).</summary>
public sealed record OrgHistoryEntry(string LogPath, DateTime When, string Root, string Action, int Count, bool Undone, bool Kept = false)
{
    public bool CanUndo => !Undone && !Kept && Count > 0;
}

/// <summary>The undo log written for every run (JSON).</summary>
public sealed class OrgLog
{
    public int Version { get; set; } = 1;
    public DateTime When { get; set; }
    public string Root { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Undone { get; set; }
    /// <summary>The user pressed "Keep changes": the run is final and no longer offered for undo.</summary>
    public bool Kept { get; set; }
    public List<OrgLogMove> Moves { get; set; } = new();
    /// <summary>Folders this run created (removed again by Undo if they end up empty).</summary>
    public List<string> CreatedFolders { get; set; } = new();
}

public sealed class OrgLogMove
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>The user's own organize rules: send a specific extension to a folder of their choosing, and/or rename
/// a type's folder (Installers → "Setup files"). Folder values are RELATIVE to the folder being organized (one to
/// three levels, e.g. "Design\\Photoshop") and are validated by <see cref="OrganizerService.WhyBadFolder"/>.</summary>
public sealed class OrgRules
{
    public Dictionary<string, string> ExtensionFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<OrgKind, string> KindFolders { get; } = new();

    /// <summary>Builds rules from (match, folder) pairs: match is ".ext" (or "ext") or a type name such as
    /// "Installers"/"Disk images". Invalid pairs are skipped.</summary>
    public static OrgRules From(IEnumerable<(string Match, string Folder)> pairs)
    {
        var r = new OrgRules();
        foreach (var (match, folder) in pairs)
        {
            if (string.IsNullOrWhiteSpace(match) || OrganizerService.WhyBadFolder(folder) is not null) continue;
            var f = OrganizerService.CleanFolder(folder);
            var kind = OrganizerService.AllKinds.Cast<OrgKind?>().FirstOrDefault(k =>
                string.Equals(k!.Value.ToString(), match.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(OrganizerService.KindLabel(k.Value), match.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(OrganizerService.FolderName(k.Value), match.Trim(), StringComparison.OrdinalIgnoreCase));
            if (kind is not null) { r.KindFolders[kind.Value] = f; continue; }
            var ext = OrganizerService.CleanExtension(match);
            if (ext is not null) r.ExtensionFolders[ext] = f;
        }
        return r;
    }

    /// <summary>Every folder name the rules create (top level), so a later scan doesn't re-sort them.</summary>
    public IEnumerable<string> TopFolders()
        => ExtensionFolders.Values.Concat(KindFolders.Values).Select(f => f.Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase);
}

public static class OrganizerService
{
    private const int MaxFiles = 50000;
    private const int MaxDepth = 12;

    // ---- types -------------------------------------------------------------------------------------------

    private static readonly HashSet<string> InstallerExts = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".msp", ".msu" };

    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".cs", ".cpp", ".cc", ".c", ".h", ".hpp", ".java",
        ".kt", ".go", ".rs", ".rb", ".php", ".swift", ".html", ".htm", ".css", ".scss", ".json", ".xml", ".yaml",
        ".yml", ".toml", ".ini", ".cfg", ".sql", ".sh", ".ps1", ".psm1", ".bat", ".cmd", ".vbs", ".lua", ".ipynb",
        ".reg",
    };

    private static readonly HashSet<string> FontExts = new(StringComparer.OrdinalIgnoreCase)
    { ".ttf", ".otf", ".ttc", ".woff", ".woff2", ".fon" };

    /// <summary>Never moved: downloads still in progress, shortcuts, and Windows/Office housekeeping files.</summary>
    private static readonly HashSet<string> LeaveAloneExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp", ".!ut", ".!qb", ".aria2",
        ".lnk", ".url",
    };

    /// <summary>The type folder for a file. Personal-content types come from the same table the backups use
    /// (<see cref="DocumentScanner.CategoryOf"/>), so the organizer and the backups always agree.</summary>
    public static OrgKind KindOf(string path)
    {
        switch (DocumentScanner.CategoryOf(path))
        {
            case DocCategory.Pictures: return OrgKind.Pictures;
            case DocCategory.Videos: return OrgKind.Videos;
            case DocCategory.Audio: return OrgKind.Music;
            case DocCategory.Documents: return OrgKind.Documents;
            case DocCategory.Archives: return OrgKind.Archives;
            case DocCategory.DiskImages: return OrgKind.DiskImages;
        }
        var ext = Path.GetExtension(path);
        if (InstallerExts.Contains(ext)) return OrgKind.Installers;
        if (CodeExts.Contains(ext)) return OrgKind.Code;
        if (FontExts.Contains(ext)) return OrgKind.Fonts;
        return OrgKind.Other;
    }

    /// <summary>Null when <paramref name="folder"/> is an acceptable rule folder, else why not. Must be relative
    /// (no drive, no leading slash), 1 to 3 levels, no "." or ".." parts, no characters Windows forbids, and no
    /// reserved device names.</summary>
    public static string? WhyBadFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Type a folder name.";
        var f = folder!.Trim().Replace('/', '\\').Trim('\\');
        if (f.Length == 0) return "Type a folder name.";
        if (f.Contains(':') || folder.Trim().StartsWith("\\", StringComparison.Ordinal)) return "Use a folder name, not a full path.";
        var parts = f.Split('\\');
        if (parts.Length > 3) return "Use at most three levels (for example Design\\Photoshop).";
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length == 0 || t == "." || t == "..") return "Folder names can't be empty, \".\" or \"..\".";
            if (t.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "That folder name has a character Windows doesn't allow.";
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "That's a reserved Windows name.";
            if (t.EndsWith(".", StringComparison.Ordinal)) return "Folder names can't end with a dot.";
        }
        return null;
    }

    internal static string CleanFolder(string folder)
        => string.Join("\\", folder.Trim().Replace('/', '\\').Trim('\\').Split('\\').Select(p => p.Trim()));

    /// <summary>".psd" from "psd", ".PSD" or " .psd "; null if it isn't a plausible extension.</summary>
    public static string? CleanExtension(string? match)
    {
        var e = (match ?? "").Trim().TrimStart('*');
        if (!e.StartsWith(".", StringComparison.Ordinal)) e = "." + e;
        return System.Text.RegularExpressions.Regex.IsMatch(e, @"^\.[A-Za-z0-9_\-]{1,15}$") ? e.ToLowerInvariant() : null;
    }

    public static string FolderName(OrgKind k) => k switch
    {
        OrgKind.DiskImages => "Disk Images",
        _ => k.ToString(),
    };

    public static string KindLabel(OrgKind k) => k switch
    {
        OrgKind.DiskImages => "Disk images",
        OrgKind.Code => "Code & scripts",
        _ => k.ToString(),
    };

    public static IReadOnlyList<OrgKind> AllKinds { get; } = (OrgKind[])Enum.GetValues(typeof(OrgKind));

    /// <summary>The folder names the organizer creates (plus "Duplicates"); a scan never re-sorts files that
    /// are already inside one of these.</summary>
    private static readonly HashSet<string> OutputFolderNames =
        new(AllKinds.Select(FolderName).Concat(new[] { "Duplicates" }), StringComparer.OrdinalIgnoreCase);

    public static bool IsLeftAlone(string fileName)
        => LeaveAloneExts.Contains(Path.GetExtension(fileName))
           || fileName.StartsWith("~$", StringComparison.Ordinal)                       // Office lock file
           || fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
           || fileName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);

    // ---- where it may run ----------------------------------------------------------------------------------

    /// <summary>Null when <paramref name="folder"/> may be organized, otherwise the reason it may not. Refuses
    /// drive roots, Windows/Program Files/ProgramData, the user-profile folder itself and AppData: moving files
    /// there can break Windows or installed apps.</summary>
    public static string? WhyNot(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Choose a folder first.";
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\', '/'); } catch { return "That isn't a valid folder path."; }
        if (!Directory.Exists(full)) return "That folder doesn't exist (any more).";
        if (Path.GetPathRoot(full + "\\")!.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase))
            return "A whole drive can't be organized, pick a folder on it (for example Downloads).";
        string F(Environment.SpecialFolder f) => (Environment.GetFolderPath(f) ?? "").TrimEnd('\\');
        var profile = F(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0 && full.Equals(profile, StringComparison.OrdinalIgnoreCase))
            return "Your user folder itself holds Windows settings, pick a folder inside it (for example Downloads).";
        foreach (var sys in new[]
                 {
                     F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.ProgramFiles),
                     F(Environment.SpecialFolder.ProgramFilesX86), F(Environment.SpecialFolder.CommonApplicationData),
                     F(Environment.SpecialFolder.ApplicationData), F(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (sys.Length == 0) continue;
            if (full.Equals(sys, StringComparison.OrdinalIgnoreCase) || full.StartsWith(sys + "\\", StringComparison.OrdinalIgnoreCase))
                return "That's a Windows or app folder, organizing it could break things.";
        }
        return null;
    }

    // ---- scan ---------------------------------------------------------------------------------------------

    public static Task<OrgScanResult> ScanAsync(string root, bool includeSubfolders,
        IProgress<(int files, string current)>? progress = null, CancellationToken ct = default, OrgRules? rules = null)
        => Task.Run(() => Scan(root, includeSubfolders, progress, ct, rules), ct);

    private static OrgScanResult Scan(string root, bool includeSubfolders, IProgress<(int, string)>? progress, CancellationToken ct, OrgRules? rules)
    {
        var skipNames = new HashSet<string>(OutputFolderNames, StringComparer.OrdinalIgnoreCase);
        if (rules is not null) foreach (var n in rules.TopFolders()) skipNames.Add(n);
        var sw = Stopwatch.StartNew();
        var files = new List<OrgFile>();
        int skippedFiles = 0, skippedFolders = 0;
        var truncated = false;
        var rootFull = Path.GetFullPath(root).TrimEnd('\\');

        void Visit(string dir, int depth)
        {
            if (ct.IsCancellationRequested || truncated) return;
            foreach (var f in SafeFiles(dir))
            {
                if (ct.IsCancellationRequested) return;
                if (files.Count >= MaxFiles) { truncated = true; return; }
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { skippedFiles++; continue; }
                if (IsLeftAlone(fi.Name) || (fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                { skippedFiles++; continue; }
                var rel = dir.Length > rootFull.Length ? dir.Substring(rootFull.Length).TrimStart('\\') : "";
                long size = 0; try { size = fi.Length; } catch { }
                DateTime mod = DateTime.MinValue; try { mod = fi.LastWriteTime; } catch { }
                files.Add(new OrgFile { FullPath = fi.FullName, Name = fi.Name, RelativeFolder = rel, Kind = KindOf(fi.Name), SizeBytes = size, Modified = mod });
                if (files.Count % 200 == 0) progress?.Report((files.Count, fi.Name));
            }
            if (!includeSubfolders || depth >= MaxDepth) return;
            foreach (var sub in SafeDirs(dir))
            {
                if (ct.IsCancellationRequested) return;
                var name = Path.GetFileName(sub);
                if (IsHiddenSystemOrLink(sub) || skipNames.Contains(name) || IsProtectedTree(sub, ct))
                { skippedFolders++; continue; }
                progress?.Report((files.Count, name));
                Visit(sub, depth + 1);
            }
        }

        if (Directory.Exists(rootFull)) Visit(rootFull, 0);
        sw.Stop();
        return new OrgScanResult(files, skippedFiles, skippedFolders, truncated, ct.IsCancellationRequested, sw.Elapsed);
    }

    /// <summary>A subfolder that must stay intact: an installed/portable app (it holds DLLs or drivers), or a
    /// code project (a git repo, a solution, node_modules, a package manifest). Moving files out of either breaks
    /// it. Checked over the whole subtree (bounded).</summary>
    internal static bool IsProtectedTree(string dir, CancellationToken ct)
    {
        var stack = new Stack<(string d, int depth)>();
        stack.Push((dir, 0));
        var seen = 0;
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) return true;
            var (d, depth) = stack.Pop();
            if (++seen > 5000) return true;                       // a huge tree: leave it alone rather than guess
            foreach (var sub in SafeDirs(d))
            {
                var n = Path.GetFileName(sub);
                if (n.Equals(".git", StringComparison.OrdinalIgnoreCase) || n.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || n.Equals(".svn", StringComparison.OrdinalIgnoreCase) || n.Equals(".hg", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (depth < MaxDepth && !IsHiddenSystemOrLink(sub)) stack.Push((sub, depth + 1));
            }
            foreach (var f in SafeFiles(d))
            {
                var ext = Path.GetExtension(f);
                var n = Path.GetFileName(f);
                if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".sys", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) || ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("package.json", StringComparison.OrdinalIgnoreCase) || n.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) || n.Equals("go.mod", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // ---- plans -------------------------------------------------------------------------------------------

    /// <summary>"Organize by type": each file of an enabled kind goes into its type folder (here, or the Windows
    /// library for pictures/videos/music/documents), optionally one level deeper by year or year-month. Files
    /// already sitting in their destination are left alone.</summary>
    public static OrgPlan PlanByType(string root, IEnumerable<OrgFile> files, ISet<OrgKind> kinds,
        OrgDestination destination, OrgDateSplit dateSplit, OrgRules? rules = null)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\');
        var moves = new List<OrgMove>();
        var inPlace = 0;
        foreach (var f in files)
        {
            if (!kinds.Contains(f.Kind)) continue;
            // An extension rule wins (and always lands inside this folder); then a renamed type folder; then the default.
            string baseDir;
            if (rules is not null && rules.ExtensionFolders.TryGetValue(Path.GetExtension(f.Name), out var extFolder))
                baseDir = Path.Combine(rootFull, extFolder);
            else if (rules is not null && rules.KindFolders.TryGetValue(f.Kind, out var kindFolder)
                     && !(destination == OrgDestination.WindowsLibraries && HasLibrary(f.Kind)))
                baseDir = Path.Combine(rootFull, kindFolder);
            else
                baseDir = BaseFolderFor(rootFull, f.Kind, destination);
            var dest = dateSplit switch
            {
                OrgDateSplit.Year when f.Modified != DateTime.MinValue => Path.Combine(baseDir, f.Modified.ToString("yyyy")),
                OrgDateSplit.YearMonth when f.Modified != DateTime.MinValue => Path.Combine(baseDir, f.Modified.ToString("yyyy"), f.Modified.ToString("yyyy-MM")),
                _ => baseDir,
            };
            if (SameFolder(Path.GetDirectoryName(f.FullPath)!, dest)) { inPlace++; continue; }
            moves.Add(new OrgMove(f.FullPath, dest, f.SizeBytes, Relative(rootFull, dest)));
        }
        return new OrgPlan(rootFull, "Organize by type", moves, inPlace);
    }

    /// <summary>"Move ticked files to a folder".</summary>
    public static OrgPlan PlanMoveTo(string root, IEnumerable<OrgFile> files, string targetFolder)
    {
        var target = Path.GetFullPath(targetFolder).TrimEnd('\\');
        var moves = new List<OrgMove>();
        var inPlace = 0;
        foreach (var f in files)
        {
            if (SameFolder(Path.GetDirectoryName(f.FullPath)!, target)) { inPlace++; continue; }
            moves.Add(new OrgMove(f.FullPath, target, f.SizeBytes, target));
        }
        return new OrgPlan(Path.GetFullPath(root).TrimEnd('\\'), "Move to " + target, moves, inPlace);
    }

    /// <summary>Moves every extra copy of a duplicate group into a "Duplicates" folder (the kept copy stays).</summary>
    public static OrgPlan PlanDuplicates(string root, IEnumerable<IReadOnlyList<OrgFile>> groups)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\');
        var dest = Path.Combine(rootFull, "Duplicates");
        var moves = groups.SelectMany(g => g.Skip(1))
            .Select(f => new OrgMove(f.FullPath, dest, f.SizeBytes, "Duplicates")).ToList();
        return new OrgPlan(rootFull, "Move duplicate copies", moves, 0);
    }

    private static bool HasLibrary(OrgKind k) => k is OrgKind.Pictures or OrgKind.Videos or OrgKind.Music or OrgKind.Documents;

    private static string BaseFolderFor(string root, OrgKind kind, OrgDestination destination)
    {
        if (destination == OrgDestination.WindowsLibraries)
        {
            var lib = kind switch
            {
                OrgKind.Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                OrgKind.Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                OrgKind.Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                OrgKind.Documents => Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                _ => "",
            };
            if (!string.IsNullOrWhiteSpace(lib)) return lib.TrimEnd('\\');
        }
        return Path.Combine(root, FolderName(kind));
    }

    private static bool SameFolder(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string Relative(string root, string path)
        => path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length + 1) : path;

    // ---- duplicates ----------------------------------------------------------------------------------------

    /// <summary>Groups of byte-identical files (same size, then same SHA-256). In each group the copy to KEEP
    /// comes first: the one without a "(1)"/"- Copy" style suffix, then the oldest. Empty files are ignored.</summary>
    public static Task<List<IReadOnlyList<OrgFile>>> FindDuplicatesAsync(IReadOnlyList<OrgFile> files,
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var candidates = files.Where(f => f.SizeBytes > 0).GroupBy(f => f.SizeBytes).Where(g => g.Count() > 1).ToList();
            var total = candidates.Sum(g => g.Count());
            var done = 0;
            var result = new List<IReadOnlyList<OrgFile>>();
            using var sha = SHA256.Create();
            foreach (var bySize in candidates)
            {
                var byHash = new Dictionary<string, List<OrgFile>>();
                foreach (var f in bySize)
                {
                    ct.ThrowIfCancellationRequested();
                    done++;
                    if (done % 20 == 0) progress?.Report((done, total));
                    string h;
                    try { using var fs = File.OpenRead(f.FullPath); h = BitConverter.ToString(sha.ComputeHash(fs)); }
                    catch { continue; }                                     // locked/vanished: not a duplicate candidate
                    if (!byHash.TryGetValue(h, out var list)) byHash[h] = list = new List<OrgFile>();
                    list.Add(f);
                }
                foreach (var g in byHash.Values.Where(l => l.Count > 1))
                    result.Add(g.OrderBy(f => LooksLikeCopy(f.Name) ? 1 : 0).ThenBy(f => f.Modified).ThenBy(f => f.Name.Length).ToList());
            }
            progress?.Report((total, total));
            return result;
        }, ct);

    internal static bool LooksLikeCopy(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return System.Text.RegularExpressions.Regex.IsMatch(stem, @"(\s\(\d+\)|\s-\sCopy(\s\(\d+\))?|_copy\d*|\scopy\d*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // ---- run + undo ------------------------------------------------------------------------------------------

    /// <summary>Where undo logs live (per edition, beside the settings).</summary>
    public static string HistoryDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Edition.Current.SettingsDir, "Organizer");

    /// <summary>Carries out a plan. Each file is MOVED (never copied-and-deleted by hand, never overwritten); a
    /// name clash in the destination gets "name (2).ext". Every completed move is recorded in an undo log that is
    /// saved as the run goes, so even a crash or a cancel leaves an accurate log. A locked file (open in another
    /// app) is skipped. <paramref name="historyDir"/> is for tests.</summary>
    public static Task<OrgRunResult> ExecuteAsync(OrgPlan plan, IProgress<(int done, int total, string current)>? progress = null,
        CancellationToken ct = default, string? historyDir = null)
        => Task.Run(() =>
        {
            var dir = historyDir ?? HistoryDir();
            Directory.CreateDirectory(dir);
            var logPath = UniqueLogPath(dir);
            var log = new OrgLog { When = DateTime.Now, Root = plan.Root, Action = plan.Action };
            var notes = new List<string>();
            int moved = 0, skipped = 0, done = 0;
            var cancelled = false;
            try
            {
                foreach (var m in plan.Moves)
                {
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                    done++;
                    var name = Path.GetFileName(m.Source);
                    progress?.Report((done, plan.Moves.Count, name));
                    try
                    {
                        if (!File.Exists(m.Source)) { skipped++; notes.Add($"Skipped (no longer there): {name}"); continue; }
                        CreateTracked(m.DestFolder, log.CreatedFolders);
                        var dest = PortableBackupService.UniquePath(Path.Combine(m.DestFolder, name), isFile: true);
                        long before = 0; try { before = new FileInfo(m.Source).Length; } catch { }
                        File.Move(m.Source, dest);                         // never overwrites; Windows copies+deletes across drives
                        if (!File.Exists(dest) || new FileInfo(dest).Length != before)
                        {
                            notes.Add($"Check this file, the moved copy looks different: {dest}");
                        }
                        log.Moves.Add(new OrgLogMove { From = m.Source, To = dest });
                        moved++;
                        if (moved % 100 == 0) SaveLog(logPath, log);
                    }
                    catch (IOException ex) { skipped++; notes.Add($"Skipped (in use or blocked): {name}, {ex.Message}"); }
                    catch (UnauthorizedAccessException) { skipped++; notes.Add($"Skipped (access denied): {name}"); }
                }
            }
            finally
            {
                if (log.Moves.Count > 0 || log.CreatedFolders.Count > 0) SaveLog(logPath, log);
            }
            return new OrgRunResult(moved, skipped, notes, log.Moves.Count > 0 ? logPath : "", cancelled);
        });

    /// <summary>Puts every file of a run back where it came from (newest move first). A file that was moved or
    /// deleted since, or whose original name is now taken, is skipped and reported, never overwritten. Folders
    /// the run created are removed again only if they're empty.</summary>
    public static Task<OrgRunResult> UndoAsync(string logPath, IProgress<(int done, int total, string current)>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            var log = LoadLog(logPath) ?? throw new InvalidOperationException("That undo record can't be read.");
            if (log.Undone) return new OrgRunResult(0, 0, new[] { "This run was already undone." }, logPath, false);
            if (log.Kept) return new OrgRunResult(0, 0, new[] { "This run was kept, so it can't be undone." }, logPath, false);
            var notes = new List<string>();
            int restored = 0, skipped = 0, done = 0;
            var moves = Enumerable.Reverse(log.Moves).ToList();
            foreach (var m in moves)
            {
                if (ct.IsCancellationRequested) break;
                done++;
                progress?.Report((done, moves.Count, Path.GetFileName(m.To)));
                try
                {
                    if (!File.Exists(m.To)) { skipped++; notes.Add($"Not found any more: {m.To}"); continue; }
                    if (File.Exists(m.From)) { skipped++; notes.Add($"Left in place, a file named {Path.GetFileName(m.From)} is back in the original folder: {m.To}"); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(m.From)!);
                    File.Move(m.To, m.From);
                    restored++;
                }
                catch (Exception ex) { skipped++; notes.Add($"Couldn't move back {Path.GetFileName(m.To)}: {ex.Message}"); }
            }
            foreach (var d in Enumerable.Reverse(log.CreatedFolders))
            {
                try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch { /* not empty or in use, leave it */ }
            }
            // Keep only the moves whose file is still sitting in its new place (skipped or not reached yet), so a
            // second Undo can retry exactly those. Nothing left means the run is fully undone.
            log.Moves = log.Moves.Where(x => File.Exists(x.To)).ToList();
            log.Undone = log.Moves.Count == 0;
            SaveLog(logPath, log);
            return new OrgRunResult(restored, skipped, notes, logPath, ct.IsCancellationRequested);
        });

    /// <summary>Past runs, newest first.</summary>
    public static IReadOnlyList<OrgHistoryEntry> History(string? historyDir = null)
    {
        var dir = historyDir ?? HistoryDir();
        var list = new List<OrgHistoryEntry>();
        try
        {
            foreach (var f in Directory.GetFiles(dir, "organize-*.json"))
            {
                var log = LoadLog(f);
                if (log is null) continue;
                list.Add(new OrgHistoryEntry(f, log.When, log.Root, log.Action, log.Moves.Count, log.Undone, log.Kept));
            }
        }
        catch { /* no history yet */ }
        return list.OrderByDescending(h => h.When).ToList();
    }

    // ---- keep + retention ------------------------------------------------------------------------------------

    /// <summary>How long a run stays undoable unless the user picks otherwise (AppSettings.OrganizerUndoDays).</summary>
    public const int DefaultUndoDays = 2;
    /// <summary>The choices offered on the Organize page; 0 = never expire.</summary>
    public static readonly int[] UndoDayChoices = { 1, 2, 7, 30, 0 };

    /// <summary>A stored setting made safe: negative or unknown values fall back to the default.</summary>
    public static int NormalizeUndoDays(int days) => Array.IndexOf(UndoDayChoices, days) >= 0 ? days : DefaultUndoDays;

    public static string UndoDaysLabel(int days) => days switch
    {
        0 => "Never expire",
        1 => "1 day",
        7 => "1 week",
        _ => $"{days} days",
    };

    /// <summary>When a run stops being undoable, or null when undo never expires.</summary>
    public static DateTime? UndoExpires(DateTime when, int undoDays)
        => NormalizeUndoDays(undoDays) is var d && d > 0 ? when.AddDays(d) : null;

    /// <summary>"Keep changes": marks a run final so it's no longer offered for undo. The log stays on disk until
    /// normal retention removes it, so this moves no files and loses nothing.</summary>
    public static bool KeepRun(string logPath)
    {
        var log = string.IsNullOrWhiteSpace(logPath) ? null : LoadLog(logPath);
        if (log is null) return false;
        log.Kept = true;
        SaveLog(logPath, log);
        return true;
    }

    /// <summary>Deletes undo logs older than the undo window (only Venquery's own organize-*.json files, never the
    /// user's files, which stay exactly where the run put them). <paramref name="undoDays"/> 0 = never expire.
    /// Returns how many logs were removed.</summary>
    public static int PruneHistory(int undoDays, string? historyDir = null, DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        var removed = 0;
        foreach (var h in History(historyDir))
        {
            if (UndoExpires(h.When, undoDays) is not { } exp || exp > at) continue;
            try { File.Delete(h.LogPath); removed++; } catch { /* in use, try next time */ }
        }
        return removed;
    }

    /// <summary>The "can be undone until" hint for an undoable run ("" when it can't be undone).</summary>
    public static string UndoHint(OrgHistoryEntry h, int undoDays)
    {
        if (!h.CanUndo) return "";
        return UndoExpires(h.When, undoDays) is { } exp ? $"Can be undone until {exp:g}" : "Can be undone any time";
    }

    private static void CreateTracked(string folder, List<string> created)
    {
        if (Directory.Exists(folder)) return;
        // Record every level that doesn't exist yet (outermost first), so Undo can remove them again.
        var missing = new Stack<string>();
        var d = folder;
        while (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) { missing.Push(d); d = Path.GetDirectoryName(d); }
        Directory.CreateDirectory(folder);
        while (missing.Count > 0)
        {
            var m = missing.Pop();
            if (!created.Contains(m, StringComparer.OrdinalIgnoreCase)) created.Add(m);
        }
    }

    private static string UniqueLogPath(string dir)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var p = Path.Combine(dir, $"organize-{stamp}.json");
        for (var i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"organize-{stamp}-{i}.json");
        return p;
    }

    private static readonly JsonSerializerOptions LogJson = new() { WriteIndented = true };

    private static void SaveLog(string path, OrgLog log)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(log, LogJson));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        catch { /* best effort; the moves themselves already happened */ }
    }

    internal static OrgLog? LoadLog(string path)
    {
        try { return JsonSerializer.Deserialize<OrgLog>(File.ReadAllText(path)); }
        catch { return null; }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static string[] SafeFiles(string dir) { try { return Directory.GetFiles(dir); } catch { return Array.Empty<string>(); } }
    private static string[] SafeDirs(string dir) { try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); } }

    private static bool IsHiddenSystemOrLink(string dir)
    {
        try
        {
            var a = new DirectoryInfo(dir).Attributes;
            return (a & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0;
        }
        catch { return true; }
    }

    public static string FormatBytes(long b)
    {
        if (b < 1024) return b + " B";
        double v = b; string[] u = { "KB", "MB", "GB", "TB" }; var i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
        return v.ToString(v < 10 ? "0.0" : "0") + " " + u[i];
    }
}
