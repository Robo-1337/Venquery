using System.IO;
using System.Text.Json;

namespace Venquery.Services;

/// <summary>Small persisted UI preferences (theme + window size), stored as JSON in
/// %LOCALAPPDATA%\Venquery. Best-effort: any failure silently falls back to defaults.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "dark";

    /// <summary>File name (within the Themes folder next to the app) of a custom background image,
    /// or "" for none. Stored by name so a portable copy survives a drive-letter change.</summary>
    public string BackgroundImage { get; set; } = "";

    /// <summary>How strongly the background image shows through (0.1–0.9); the rest blends into the
    /// theme's base colour so content stays readable.</summary>
    public double BackgroundOpacity { get; set; } = 0.35;

    /// <summary>Showcase screen: its OWN background image (a bare name in the Themes folder, "" = none),
    /// independent of the main-window <see cref="BackgroundImage"/>.</summary>
    public string ShowcaseBackgroundImage { get; set; } = "";

    /// <summary>Showcase screen: opacity of its background image (0.1–0.9), separate from the main window's.</summary>
    public double ShowcaseBackgroundOpacity { get; set; } = 0.35;

    /// <summary>Showcase screen: which layout preset to open ("hero" / "sheet" / "poster" / "tiles").</summary>
    public string ShowcasePreset { get; set; } = "hero";

    /// <summary>Showcase screen: reveal per-part model numbers + manufacturers (off = a clean summary).</summary>
    public bool ShowcaseShowDetails { get; set; }

    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>Which nav-rail view to reopen on launch ("drivers" / "backup" / "portable" / "settings").</summary>
    public string LastView { get; set; } = "drivers";

    /// <summary>Portable-app scan scope: false = user folder (default), true = whole C: drive.</summary>
    public bool PortableWholeDrive { get; set; }

    /// <summary>Left-hand mode: move the navigation rail to the right side of the window.</summary>
    public bool NavigationOnRight { get; set; }

    /// <summary>Loose-documents scan scope: false = Desktop + C: only (default), true = also scan the
    /// other ready fixed/removable drives.</summary>
    public bool DocScanOtherDrives { get; set; }

    /// <summary>Off (default): a backup only restores on the PC that created it. On: the exported
    /// restore script drops the machine-model lock, allowing restore on a different PC model.</summary>
    public bool AllowCrossMachineRestore { get; set; }


    /// <summary>Where Portable Apps backups go: the parent folder Venquery creates "Portable Apps" inside.
    /// Empty (or a folder that no longer exists) = Downloads.</summary>
    public string PortableBackupParent { get; set; } = "";

    /// <summary>Where Loose Documents backups go: the parent folder Venquery creates "Portable Documents"
    /// inside. Empty (or a folder that no longer exists) = Downloads.</summary>
    public string DocBackupParent { get; set; } = "";

    /// <summary>After a Portable Apps / Loose Documents / Reset-wizard copy, check every copied file and write a
    /// "Backup receipt" listing them into the backup folder. On by default.</summary>
    public bool BackupReceipts { get; set; } = true;

    /// <summary>Specific folders the user added to the Portable Apps scan scope (searched alongside any ticked
    /// drives). Remembered between sessions; untick a folder chip to remove it.</summary>
    public List<string> PortableScanFolders { get; set; } = new();

    /// <summary>Specific folders the user added to the Loose Documents scan scope.</summary>
    public List<string> DocScanFolders { get; set; } = new();

    /// <summary>File Organizer: the last folder organized ("" = suggest Downloads).</summary>
    public string OrganizerFolder { get; set; } = "";
    /// <summary>File Organizer: also look inside subfolders (off = loose files only).</summary>
    public bool OrganizerIncludeSubfolders { get; set; }
    /// <summary>File Organizer: send pictures/videos/music/documents to the Windows library folders.</summary>
    public bool OrganizerUseLibraries { get; set; }
    /// <summary>File Organizer: extra folder level by date: "none", "year" or "month".</summary>
    public string OrganizerDateSplit { get; set; } = "none";
    /// <summary>File Organizer: the user's own rules (".psd" → "Design", or "Installers" → "Setup files").</summary>
    public List<OrganizerRuleSetting> OrganizerRules { get; set; } = new();
    /// <summary>File Organizer: how many days a run stays undoable before its undo record is removed (0 = never expire).</summary>
    public int OrganizerUndoDays { get; set; } = 2;   // OrganizerService.DefaultUndoDays





    // Per-build folder (Venquery-Community / Venquery-Legacy) so builds installed side
    // by side don't fight over theme, window size or last-used view.
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Edition.Current.SettingsDir);
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Repair(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings());
        }
        catch { /* corrupt/unreadable, use defaults */ }
        return new AppSettings();
    }

    /// <summary>A hand-edited or partly-written file can carry <c>null</c> for a text or list setting, which the
    /// rest of the app never expects. Put the default back for any such value.</summary>
    internal static AppSettings Repair(AppSettings s)
    {
        var defaults = new AppSettings();
        foreach (var prop in typeof(AppSettings).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
            if (prop.PropertyType.IsValueType) continue;
            if (prop.GetValue(s) is null) prop.SetValue(s, prop.GetValue(defaults));
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Write a temp file, then swap it in, so a crash or power cut mid-save can't leave a half-written
            // settings.json behind (which would silently reset every setting on the next launch).
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch { /* non-fatal */ }
    }
}

/// <summary>One File Organizer rule: <see cref="Match"/> is an extension (".psd") or a type name ("Installers"),
/// <see cref="Folder"/> the folder (relative to the organized folder) it goes to.</summary>
public sealed class OrganizerRuleSetting
{
    public string Match { get; set; } = "";
    public string Folder { get; set; } = "";
}

