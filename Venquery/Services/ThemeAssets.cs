using System.IO;

namespace Venquery.Services;

/// <summary>Locates the per-install <c>Themes</c> folder (next to the running app) and imports
/// background images into it. Write-only like the rest of the app, importing copies the chosen
/// file in; nothing is executed. Backgrounds are stored by file name (not full path) so a portable
/// copy keeps working when its USB drive letter changes.</summary>
public static class ThemeAssets
{
    /// <summary>The <c>Themes</c> folder next to the running exe, created on demand.</summary>
    public static string Folder()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Themes");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Full path of a stored background by file name, or null if unset/missing.</summary>
    public static string? Resolve(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        // Only ever resolve a bare file name inside the Themes folder, strip any directory parts so a
        // hand-edited settings value can't point the background at a file outside it.
        fileName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(fileName)) return null;
        var path = Path.Combine(Folder(), fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>True if the file is actually a WebP (RIFF????WEBP), WPF can't render those, so a
    /// <c>.png</c>/<c>.jpg</c> that is really a WebP has to be rejected rather than shown blank.</summary>
    public static bool IsWebp(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[12];
            if (fs.Read(head, 0, 12) < 12) return false;
            return head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
                && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P';
        }
        catch { return false; }
    }

    /// <summary>Copy an image into the Themes folder; returns the stored file name. Re-importing the
    /// same file reuses its name; a name clash with a different file gets a "(2)" suffix so nothing
    /// is overwritten.</summary>
    public static string Import(string sourcePath)
    {
        var name = Path.GetFileName(sourcePath);
        var dest = Path.Combine(Folder(), name);

        // Picked from inside the Themes folder already? It's where it needs to be, use it as-is
        // instead of copying a file onto itself (which throws "being used by another process").
        if (PathsEqual(sourcePath, dest)) return name;

        if (File.Exists(dest) && !SameContent(sourcePath, dest))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            var n = 2;
            do { name = $"{stem} ({n++}){ext}"; dest = Path.Combine(Folder(), name); }
            while (File.Exists(dest) && !SameContent(sourcePath, dest));
        }
        // An identical copy is already stored: reuse it (and don't rewrite a file that may be on screen).
        if (File.Exists(dest)) return name;
        File.Copy(sourcePath, dest, overwrite: false);
        return name;
    }

    /// <summary>Same bytes (size first, then a SHA-256 of each). Two different images that merely share a
    /// size used to count as "the same file" and the new one silently overwrote the stored one.</summary>
    private static bool SameContent(string a, string b)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] Hash(string p) { using var fs = File.OpenRead(p); return sha.ComputeHash(fs); }
            return Hash(a).SequenceEqual(Hash(b));
        }
        catch { return false; }
    }

    /// <summary>True if two paths point at the same file (normalized, case-insensitive).</summary>
    private static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
