using System.IO;

namespace Venquery.Services;

/// <summary>Absolute paths for the Windows programs Venquery launches. Launching by bare name ("powershell.exe")
/// lets Windows search the app's own folder and the current folder FIRST, so a look-alike exe planted next to
/// a portable Venquery (typically Downloads) would be run instead, elevated when the call uses "runas". Every
/// launch of a Windows tool goes through <see cref="Exe"/> with <see cref="WorkDir"/> as its working folder.
/// (LaunchGuardTests enforces it.)</summary>
public static class SystemTools
{
    /// <summary>The full path of a Windows tool. System32 tools resolve to the real System32 (Sysnative when a
    /// 32-bit process asks, so it isn't redirected to SysWOW64 copies); explorer lives in the Windows folder;
    /// PowerShell in its versioned folder. Unknown names still resolve under System32, never via a search.</summary>
    public static string Exe(string name)
    {
        var file = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (file.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(win, "explorer.exe");
        var sys = Environment.SystemDirectory;
        if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
        {
            var sysnative = Path.Combine(win, "Sysnative");
            if (Directory.Exists(sysnative)) sys = sysnative;
        }
        if (file.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        return Path.Combine(sys, file);
    }

    /// <summary>A neutral working folder for launched tools (never the app's or the user's current folder).</summary>
    public static string WorkDir => Environment.SystemDirectory;
}
