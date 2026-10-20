using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Venquery.Models;

namespace Venquery.Services;

public sealed record PortableScanResult(
    IReadOnlyList<PortableExe> Executables,
    int FoldersScanned,
    int Signed,
    int Unsigned,
    TimeSpan Elapsed,
    bool Truncated,
    bool Cancelled);

/// <summary>
/// Walks one or more roots and reports the ONE "main" executable of each app folder, the exe
/// whose name matches its folder, and reports its Authenticode signature (reusing
/// <see cref="Authenticode"/>, the same OS trust check the downloader uses). This keeps the list
/// to "what apps are here", not every helper/updater/uninstaller exe. Read-only: it lists files,
/// it never runs them. See <see cref="PickMainExe"/> for the matching rule.
///
/// Reliability guards, because a whole-drive walk is hostile terrain:
///   • reparse points (junctions / symlinks) are skipped, so directory loops can't hang it;
///   • access-denied folders are skipped rather than aborting the walk;
///   • depth is capped and the result set is capped, so a pathological tree can't run away;
///   • it is fully cancellable.
/// </summary>
public static class PortableAppScanner
{
    private const int MaxDepth = 16;
    private const int MaxResults = 2000;   // a clean checklist, not a forensic dump

    /// <summary>Sensible default excludes: the protected system trees (fast + irrelevant to
    /// portable tools) plus per-user AppData, which holds installed-app payloads, not
    /// standalone tools, including it would bury the signal in noise.</summary>
    public static IReadOnlyList<string> DefaultExcludes()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[] { win, pf, pf86, pd, appData, roaming }
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The recommended default scan root, the current user's profile folder.</summary>
    public static string DefaultRoot()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static Task<PortableScanResult> ScanAsync(
        IReadOnlyList<string> roots,
        IReadOnlyCollection<string> excluded,
        IProgress<(int folders, int exes, string current)>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(roots, excluded, progress, ct), ct);

    private static PortableScanResult Scan(
        IReadOnlyList<string> roots,
        IReadOnlyCollection<string> excluded,
        IProgress<(int, int, string)>? progress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = new ConcurrentBag<PortableExe>();
        var excl = new HashSet<string>(excluded.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        int folders = 0, exeCount = 0;
        var capped = false;

        void Report(string current) => progress?.Report((Volatile.Read(ref folders), Volatile.Read(ref exeCount), current));

        // Signature-check and record one executable. Cheap because we only ever call it on the
        // single main exe of a folder, not on every helper binary.
        void AddExe(string file)
        {
            long size = 0;
            var mtime = DateTime.MinValue;
            try { var fi = new FileInfo(file); size = fi.Length; mtime = fi.LastWriteTime; } catch { /* vanished / locked */ }
            var sig = Authenticode.Verify(file);
            results.Add(new PortableExe
            {
                Name = Path.GetFileName(file),
                FullPath = file,
                SizeBytes = size,
                Modified = mtime,
                Signed = sig.Valid,
                Signer = sig.Signer,
                IsSelected = false,                  // nothing pre-selected, the user picks what to back up / export
            });
            Interlocked.Increment(ref exeCount);
        }

        // Walk one directory subtree, single-threaded within the call.
        void Walk(string dir, int depth)
        {
            if (ct.IsCancellationRequested || Volatile.Read(ref exeCount) >= MaxResults) return;
            if (depth > MaxDepth) return;
            if (excl.Contains(Normalize(dir))) return;

            // Skip junctions / symlinks so we never loop through a reparse point.
            try
            {
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            }
            catch { return; }

            var scanned = Interlocked.Increment(ref folders);
            if (scanned % 64 == 0) Report(dir);

            // Only this folder's main exe (name matches the folder), never its helper binaries.
            var main = PickMainExe(SafeGetFiles(dir, "*.exe"), Path.GetFileName(dir));
            if (main is not null)
            {
                if (Volatile.Read(ref exeCount) >= MaxResults) { capped = true; return; }
                AddExe(main);
            }

            foreach (var sub in SafeGetDirs(dir))
                Walk(sub, depth + 1);
        }

        // Parallelise across the immediate children of each root; each child subtree is
        // walked on a single thread. Files sitting directly in a root are handled too.
        var topLevel = new List<(string dir, int depth)>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            if (excl.Contains(Normalize(root))) continue;

            // A loose exe sitting directly in the scan root only counts if it matches the root
            // folder's own name, otherwise it's not an "app folder", so it's left out.
            var rootMain = PickMainExe(SafeGetFiles(root, "*.exe"), Path.GetFileName(root));
            if (rootMain is not null)
            {
                if (Volatile.Read(ref exeCount) >= MaxResults) capped = true;
                else AddExe(rootMain);
            }
            Interlocked.Increment(ref folders);
            foreach (var sub in SafeGetDirs(root)) topLevel.Add((sub, 1));
        }

        try
        {
            var po = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
            };
            Parallel.ForEach(topLevel, po, item => Walk(item.dir, item.depth));
        }
        catch (OperationCanceledException) { /* cancellation is a normal outcome */ }

        sw.Stop();
        var ordered = results
            .GroupBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First())   // overlapping roots
            .OrderBy(e => e.Signed)                                   // unsigned first (needs attention)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var signed = ordered.Count(e => e.Signed);

        return new PortableScanResult(
            ordered, folders, signed, ordered.Count - signed, sw.Elapsed,
            capped || Volatile.Read(ref exeCount) >= MaxResults,
            ct.IsCancellationRequested);
    }

    /// <summary>
    /// Chooses the one "main" executable of an app folder: the exe whose name, compared
    /// case-insensitively, ignoring spaces and punctuation, is the longest prefix of the folder's
    /// name. So <c>Sunshine\sunshine.exe</c>, <c>PixPin_2.1.3\PixPin.exe</c>,
    /// <c>Sublime Text\sublime_text.exe</c> and <c>7-Zip\7z.exe</c> are picked, while helper exes
    /// (updaters, uninstallers, crash handlers), whose longer names the folder doesn't start with, 
    /// are left out. Returns null when nothing matches (min. 2 characters, to avoid coincidences).
    /// </summary>
    internal static string? PickMainExe(string[] exeFiles, string folderName)
    {
        var folder = NormalizeName(folderName);
        if (folder.Length < 2) return null;

        string? best = null;
        var bestLen = 1;   // require a match of at least 2 characters
        foreach (var f in exeFiles)
        {
            var name = NormalizeName(Path.GetFileNameWithoutExtension(f));
            if (name.Length < 2 || !folder.StartsWith(name, StringComparison.Ordinal)) continue;

            // Longest prefix wins; ties broken deterministically by path so results are stable.
            if (name.Length > bestLen || (name.Length == bestLen && best is not null && string.CompareOrdinal(f, best) < 0))
            {
                best = f;
                bestLen = name.Length;
            }
        }
        return best;
    }

    /// <summary>Folds a name to lowercase letters and digits only, so "Sublime Text" and
    /// "sublime_text" compare equal and "PixPin_2.1.3" starts with "PixPin".</summary>
    internal static string NormalizeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static string[] SafeGetFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }   // access denied / path too long / removed
    }

    private static string[] SafeGetDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }
}
