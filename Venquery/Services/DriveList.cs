using System.IO;

namespace Venquery.Services;

/// <summary>One selectable local drive for the scan-scope pickers on the Portable Apps and Loose Documents
/// pages. Read-only enumeration; picking a drive just adds its root to the next scan.</summary>
public sealed record DriveChoice(string Root, string Letter, string Label, DriveType Type, bool IsOsDrive)
{
    /// <summary>A short chip caption, e.g. "C:", "D: Data", "E: USB".</summary>
    public string Caption
    {
        get
        {
            var extra = !string.IsNullOrWhiteSpace(Label) ? " " + Label
                : Type == DriveType.Removable ? " USB"
                : "";
            return Letter + extra;
        }
    }
}

/// <summary>Enumerates the machine's ready local drives (fixed + removable) for the scan-scope pickers.
/// Network and optical drives are skipped so the UI can't stall on a slow share or spin up a disc.</summary>
public static class DriveList
{
    public static IReadOnlyList<DriveChoice> Ready()
    {
        var os = OsRoot();
        var list = new List<DriveChoice>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { return list; }
        foreach (var d in drives)
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!d.IsReady) continue;
                var root = d.RootDirectory.FullName;
                string label = "";
                try { label = d.VolumeLabel ?? ""; } catch { }
                var letter = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                list.Add(new DriveChoice(root, letter, label, d.DriveType,
                    string.Equals(root, os, StringComparison.OrdinalIgnoreCase)));
            }
            catch { /* a drive that vanished mid-enumeration or errors on query, skip it */ }
        }
        return list;
    }

    private static string OsRoot()
        => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
}
