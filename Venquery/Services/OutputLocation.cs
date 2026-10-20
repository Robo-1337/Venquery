using System.IO;

namespace Venquery.Services;

/// <summary>Where Venquery writes its "keep next to me" output (the reset-backup folder, the System Reports
/// folder). Prefers the folder Venquery is running from, so a portable copy on a USB / the Desktop writes
/// right there; falls back to Downloads when that folder is read-only (e.g. Program Files, a locked stick).
/// Shared across editions (net8 + net48 Legacy).</summary>
public static class OutputLocation
{
    /// <summary>The directory the running exe sits in (empty if it can't be determined).</summary>
    public static string ExeDir()
    {
        try
        {
#if LEGACY
            // net48 has no Environment.ProcessPath; AppContext.BaseDirectory is the exe's folder there
            // (the Legacy build is framework-dependent, not single-file, so this is the real location).
            return (AppContext.BaseDirectory ?? "").TrimEnd('\\');
#else
            var p = Environment.ProcessPath;
            return string.IsNullOrEmpty(p) ? "" : Path.GetDirectoryName(p) ?? "";
#endif
        }
        catch { return ""; }
    }

    public static bool IsWritable(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, ".venq-write-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The base folder for Venquery's output: the exe folder if writable, else Downloads.</summary>
    public static string Base(out bool usedDownloadsFallback)
    {
        var exe = ExeDir();
        if (!string.IsNullOrEmpty(exe) && IsWritable(exe)) { usedDownloadsFallback = false; return exe; }
        usedDownloadsFallback = true;
        return DownloadService.DownloadsFolder();
    }

    public static string Base() => Base(out _);

    /// <summary>Makes a string safe to use as a single folder/file name.</summary>
    public static string SafeName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim().TrimEnd('.', ' ');
        return s.Length > 0 ? s : "Unknown";
    }
}
