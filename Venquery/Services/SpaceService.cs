using System.Diagnostics;
using System.IO;

namespace Venquery.Services;

/// <summary>One folder in a space scan: its own total (everything beneath it) and its subfolders.</summary>
public sealed class SpaceNode
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long Size { get; internal set; }
    public int Files { get; internal set; }
    /// <summary>Bytes in files directly inside this folder (not in subfolders).</summary>
    public long LooseSize { get; internal set; }
    public int LooseFiles { get; internal set; }
    public List<SpaceNode> Children { get; } = new();
    public SpaceNode? Parent { get; internal set; }
    /// <summary>True when Windows refused to list (part of) this folder, so its size is a minimum.</summary>
    public bool Partial { get; internal set; }
}

public sealed record SpaceFile(string FullPath, long Size, DateTime Modified);

public sealed record SpaceScanResult(SpaceNode Root, IReadOnlyList<SpaceFile> LargestFiles, int Folders, int FilesCount,
    int Denied, bool Cancelled, TimeSpan Elapsed, long DriveTotal, long DriveFree);

/// <summary>
/// "What's using my space": a read-only walk of a drive or folder that adds up the size of every folder and keeps
/// the largest files. Nothing is changed. Junctions/symlinks are not followed (no double counting, no loops), and
/// folders Windows won't let a normal user read are counted as far as possible and marked partial. Uses only the
/// file system, so it works in every edition.
/// </summary>
public static class SpaceService
{
    private const int KeepLargest = 100;

    public static Task<SpaceScanResult> ScanAsync(string root, IProgress<(int folders, long bytes, string current)>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(root, progress, ct), ct);

    private static SpaceScanResult Scan(string root, IProgress<(int, long, string)>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var full = Path.GetFullPath(root);
        var top = new SpaceNode { Name = full, FullPath = full };
        var largest = new List<SpaceFile>();
        long smallestKept = 0;
        int folders = 0, files = 0, denied = 0;
        long bytes = 0;
        var lastReport = Stopwatch.StartNew();

        // Iterative depth-first walk (no recursion limit on very deep trees); sizes are rolled up afterwards.
        var order = new List<SpaceNode>();
        var stack = new Stack<SpaceNode>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) break;
            var node = stack.Pop();
            order.Add(node);
            folders++;
            var dir = new DirectoryInfo(node.FullPath);
            try
            {
                foreach (var fi in dir.EnumerateFiles())
                {
                    long len;
                    try { len = fi.Length; } catch { continue; }
                    node.LooseSize += len;
                    node.LooseFiles++;
                    files++;
                    bytes += len;
                    if (largest.Count < KeepLargest || len > smallestKept)
                    {
                        DateTime mod = DateTime.MinValue; try { mod = fi.LastWriteTime; } catch { }
                        largest.Add(new SpaceFile(fi.FullName, len, mod));
                        if (largest.Count > KeepLargest * 2)
                        {
                            largest = largest.OrderByDescending(f => f.Size).Take(KeepLargest).ToList();
                            smallestKept = largest[largest.Count - 1].Size;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            { node.Partial = true; denied++; }

            try
            {
                foreach (var sub in dir.EnumerateDirectories())
                {
                    try { if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    var child = new SpaceNode { Name = sub.Name, FullPath = sub.FullName, Parent = node };
                    node.Children.Add(child);
                    stack.Push(child);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            { node.Partial = true; denied++; }

            if (lastReport.ElapsedMilliseconds > 250) { progress?.Report((folders, bytes, node.FullPath)); lastReport.Restart(); }
        }

        // Roll sizes up: children were visited after their parent, so walk the visit order backwards.
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            n.Size += n.LooseSize;
            n.Files += n.LooseFiles;
            if (n.Parent is not null)
            {
                n.Parent.Size += n.Size;
                n.Parent.Files += n.Files;
                if (n.Partial) n.Parent.Partial = true;
            }
            n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }

        long total = 0, free = 0;
        try { var d = new DriveInfo(Path.GetPathRoot(full)!); if (d.IsReady) { total = d.TotalSize; free = d.TotalFreeSpace; } } catch { }
        var biggest = largest.OrderByDescending(f => f.Size).Take(KeepLargest).ToList();
        return new SpaceScanResult(top, biggest, folders, files, denied, ct.IsCancellationRequested, sw.Elapsed, total, free);
    }
}
