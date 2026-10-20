using System.IO;

namespace Venquery.Services;

public sealed record PortableBackupResult(
    bool Ok,
    string Error,
    string DestFolder,
    int Copied,
    int Skipped,
    IReadOnlyList<string> Notes,
    bool Cancelled);

/// <summary>
/// Copies whole portable-app folders into a single "Portable Apps" folder the user can drop onto a
/// USB stick for reliable restoration. Pure WRITE work, it copies files, it never runs, installs or
/// deletes anything (existing folders are kept; a name clash gets a "(2)" copy rather than an
/// overwrite). Reparse points are skipped so junction loops can't hang it, per-file errors are
/// tolerated so one locked file doesn't abort an app, and it is fully cancellable.
/// </summary>
public static class PortableBackupService
{
    /// <summary>The default staging folder: "Portable Apps" inside Downloads.</summary>
    public static string DefaultDestination()
        => Path.Combine(DownloadService.DownloadsFolder(), "Portable Apps");

    /// <summary>Copies the given <paramref name="sources"/>, each of which may be a folder OR a
    /// loose file, into <paramref name="destRoot"/>. Used by both the Portable Apps backup (folders)
    /// and the loose-documents backup (folders + files).</summary>
    public static Task<PortableBackupResult> BackupAsync(
        IReadOnlyList<string> sources,
        string destRoot,
        IProgress<(int done, int total, string current)>? progress = null,
        CancellationToken ct = default,
        BackupReceipt? receipt = null)
        => Task.Run(() => Backup(sources, destRoot, progress, ct, receipt), ct);

    private static PortableBackupResult Backup(
        IReadOnlyList<string> sources, string destRoot,
        IProgress<(int, int, string)>? progress, CancellationToken ct, BackupReceipt? receipt)
    {
        var notes = new List<string>();
        int copied = 0, skipped = 0;

        try { Directory.CreateDirectory(destRoot); }
        catch (Exception ex) { return new PortableBackupResult(false, ex.Message, destRoot, 0, 0, notes, false); }

        var destFull = Normalize(destRoot);
        int total = sources.Count, done = 0;

        foreach (var src in sources)
        {
            if (ct.IsCancellationRequested)
                return new PortableBackupResult(true, "", destRoot, copied, skipped, notes, true);

            done++;
            var name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            progress?.Report((done, total, name));

            var isDir = Directory.Exists(src);
            var isFile = !isDir && File.Exists(src);
            if (string.IsNullOrWhiteSpace(name) || (!isDir && !isFile))
            {
                skipped++; notes.Add($"Skipped (not found): {name}");
                continue;
            }

            var target = UniquePath(Path.Combine(destRoot, name), isFile);
            try
            {
                if (isDir)
                {
                    var srcFull = Normalize(src);
                    // Never copy a folder into itself or into one of its own descendants (recursion).
                    if (destFull.Equals(srcFull, StringComparison.OrdinalIgnoreCase)
                        || IsUnder(destFull, srcFull) || IsUnder(srcFull, destFull))
                    {
                        skipped++; notes.Add($"Skipped (can't copy into itself): {name}");
                        continue;
                    }
                    CopyDir(srcFull, target, ct, receipt);
                }
                else
                {
                    File.Copy(src, target, overwrite: false);
                    receipt?.Add(src, target);
                }
                copied++;
            }
            catch (OperationCanceledException)
            {
                return new PortableBackupResult(true, "", destRoot, copied, skipped, notes, true);
            }
            catch (Exception ex)
            {
                skipped++; notes.Add($"Skipped ({ex.GetType().Name}): {name}");
            }
        }

        return new PortableBackupResult(true, "", destRoot, copied, skipped, notes, ct.IsCancellationRequested);
    }

    /// <summary>Like <see cref="BackupAsync"/> but sorts loose-document content into per-category sub-folders
    /// of <paramref name="destRoot"/> (Documents / Pictures / Videos / Audio / Archives), copying only files
    /// whose category is in <paramref name="enabled"/>. A folder source is walked and its content files are
    /// distributed by type (under the origin folder name, to avoid collisions); executables are never copied.</summary>
    public static Task<PortableBackupResult> BackupByCategoryAsync(
        IReadOnlyList<string> sources,
        string destRoot,
        DocCategory enabled,
        IProgress<(int done, int total, string current)>? progress = null,
        CancellationToken ct = default,
        BackupReceipt? receipt = null)
        => Task.Run(() => BackupByCategory(sources, destRoot, enabled, progress, ct, receipt), ct);

    private static PortableBackupResult BackupByCategory(
        IReadOnlyList<string> sources, string destRoot, DocCategory enabled,
        IProgress<(int, int, string)>? progress, CancellationToken ct, BackupReceipt? receipt)
    {
        var notes = new List<string>();
        int copied = 0, skipped = 0;

        try { Directory.CreateDirectory(destRoot); }
        catch (Exception ex) { return new PortableBackupResult(false, ex.Message, destRoot, 0, 0, notes, false); }

        var destFull = Normalize(destRoot);
        int total = sources.Count, done = 0;

        foreach (var src in sources)
        {
            if (ct.IsCancellationRequested)
                return new PortableBackupResult(true, "", destRoot, copied, skipped, notes, true);

            done++;
            var name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            progress?.Report((done, total, name));

            var isDir = Directory.Exists(src);
            var isFile = !isDir && File.Exists(src);
            if (string.IsNullOrWhiteSpace(name) || (!isDir && !isFile))
            {
                skipped++; notes.Add($"Skipped (not found): {name}");
                continue;
            }

            try
            {
                if (isFile)
                {
                    var cat = DocumentScanner.CategoryOf(src);
                    if (cat == DocCategory.None || (cat & enabled) == 0) { skipped++; continue; }
                    var dest = UniquePath(Path.Combine(destRoot, DocumentScanner.CategoryFolder(cat), name), isFile: true);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, overwrite: false);
                    receipt?.Add(src, dest);
                    copied++;
                }
                else
                {
                    var srcFull = Normalize(src);
                    if (destFull.Equals(srcFull, StringComparison.OrdinalIgnoreCase)
                        || IsUnder(destFull, srcFull) || IsUnder(srcFull, destFull))
                    {
                        skipped++; notes.Add($"Skipped (can't copy into itself): {name}");
                        continue;
                    }
                    var (c, s) = CopyFolderCategorized(srcFull, srcFull, name, destRoot, enabled, ct, receipt);
                    copied += c; skipped += s;
                }
            }
            catch (OperationCanceledException)
            {
                return new PortableBackupResult(true, "", destRoot, copied, skipped, notes, true);
            }
            catch (Exception ex)
            {
                skipped++; notes.Add($"Skipped ({ex.GetType().Name}): {name}");
            }
        }

        return new PortableBackupResult(true, "", destRoot, copied, skipped, notes, ct.IsCancellationRequested);
    }

    private static (int copied, int skipped) CopyFolderCategorized(
        string srcRoot, string cur, string folderName, string destRoot, DocCategory enabled, CancellationToken ct,
        BackupReceipt? receipt = null)
    {
        ct.ThrowIfCancellationRequested();
        try { if (new DirectoryInfo(cur).Attributes.HasFlag(FileAttributes.ReparsePoint)) return (0, 0); }
        catch { return (0, 0); }

        int copied = 0, skipped = 0;
        foreach (var file in SafeEnumFiles(cur))
        {
            ct.ThrowIfCancellationRequested();
            var cat = DocumentScanner.CategoryOf(file);
            if (cat == DocCategory.None || (cat & enabled) == 0) continue;
            try
            {
                // Relative path of the file under the origin folder (net48 has no Path.GetRelativePath).
                var rel = file.Length > srcRoot.Length
                    ? file.Substring(srcRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : Path.GetFileName(file);
                var dest = UniquePath(Path.Combine(destRoot, DocumentScanner.CategoryFolder(cat), folderName, rel), isFile: true);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: false);
                receipt?.Add(file, dest);
                copied++;
            }
            catch (OperationCanceledException) { throw; }
            catch { skipped++; }
        }
        foreach (var sub in SafeEnumDirs(cur))
        {
            ct.ThrowIfCancellationRequested();
            var (c, s) = CopyFolderCategorized(srcRoot, sub, folderName, destRoot, enabled, ct, receipt);
            copied += c; skipped += s;
        }
        return (copied, skipped);
    }

    private static string[] SafeEnumFiles(string dir)
    {
        try { return Directory.GetFiles(dir); } catch { return Array.Empty<string>(); }
    }

    private static string[] SafeEnumDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static void CopyDir(string src, string dst, CancellationToken ct, BackupReceipt? receipt = null)
    {
        ct.ThrowIfCancellationRequested();

        // Skip junctions / symlinks so we never loop through a reparse point.
        try { if (new DirectoryInfo(src).Attributes.HasFlag(FileAttributes.ReparsePoint)) return; }
        catch { return; }

        Directory.CreateDirectory(dst);

        // Use the Safe* helpers (like CopyFolderCategorized) so a denied SUBDIRECTORY doesn't throw out of
        // enumeration and abort the whole source folder; a bad dir just yields nothing.
        foreach (var file in SafeEnumFiles(src))
        {
            ct.ThrowIfCancellationRequested();
            // Best-effort per file: a locked/denied file is skipped, not fatal to the whole app.
            try
            {
                var to = Path.Combine(dst, Path.GetFileName(file));
                File.Copy(file, to, overwrite: false);
                receipt?.Add(file, to);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* locked / denied / vanished, skip this file */ }
        }

        foreach (var sub in SafeEnumDirs(src))
        {
            ct.ThrowIfCancellationRequested();
            CopyDir(sub, Path.Combine(dst, Path.GetFileName(sub)), ct, receipt);
        }
    }

    /// <summary>True when <paramref name="child"/> sits inside <paramref name="parent"/>.</summary>
    private static bool IsUnder(string child, string parent)
        => child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }

    /// <summary>A free name next to <paramref name="path"/>: "Folder (2)" for a folder, "report (2).pdf" for a
    /// file (the counter goes before the extension so the copy still opens with the right app).</summary>
    internal static string UniquePath(string path, bool isFile = false)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = isFile ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
        var ext = isFile ? Path.GetExtension(path) : "";
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }
}

/// <summary>Collects every file a backup copied (source → copy) so it can be verified and listed afterwards.</summary>
public sealed class BackupReceipt
{
    private readonly List<(string Source, string Copy)> _files = new();
    public IReadOnlyList<(string Source, string Copy)> Files { get { lock (_files) return _files.ToList(); } }
    internal void Add(string source, string copy) { lock (_files) _files.Add((source, copy)); }

    private static string Size(long b)
    {
        if (b < 1024) return b + " B";
        double v = b; string[] u = { "KB", "MB", "GB", "TB" }; var i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
        return v.ToString(v < 10 ? "0.0" : "0") + " " + u[i];
    }

    /// <summary>Checks every copy (it exists and is the same size as its source) and writes
    /// "Backup receipt yyyy-MM-dd HHmmss.txt" into <paramref name="destRoot"/>, one line per file. Returns how many
    /// copies verified, how many need a look, and the receipt's path ("" if it couldn't be written).</summary>
    public (int Verified, int Problems, string Path) WriteAndVerify(string destRoot, string title)
    {
        var files = Files;
        int ok = 0, bad = 0;
        long total = 0;
        var lines = new List<string>();
        foreach (var (src, copy) in files)
        {
            long copyLen = -1, srcLen = -1;
            try { if (File.Exists(copy)) copyLen = new FileInfo(copy).Length; } catch { }
            try { if (File.Exists(src)) srcLen = new FileInfo(src).Length; } catch { }
            // Verified = the copy exists and matches the source's size (or the source is gone but the copy is there).
            var good = copyLen >= 0 && (srcLen < 0 || srcLen == copyLen);
            if (good) { ok++; total += copyLen; } else bad++;
            var rel = copy.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase) ? copy.Substring(destRoot.Length).TrimStart('\\') : copy;
            lines.Add($"{(good ? "OK   " : "CHECK")}  {Size(Math.Max(0, copyLen)),10}  {rel}    <-  {src}");
        }
        var path = "";
        try
        {
            Directory.CreateDirectory(destRoot);
            path = System.IO.Path.Combine(destRoot, $"Backup receipt {DateTime.Now:yyyy-MM-dd HHmmss}.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine($"Made {DateTime.Now:yyyy-MM-dd HH:mm} on {Environment.MachineName} by Venquery");
            sb.AppendLine($"{ok} file(s) verified ({Size(total)})" + (bad > 0 ? $", {bad} need a look (marked CHECK: missing or a different size)" : ", every copy checked"));
            sb.AppendLine(new string('-', 70));
            foreach (var l in lines) sb.AppendLine(l);
            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
        }
        catch { path = ""; }
        return (ok, bad, path);
    }
}
