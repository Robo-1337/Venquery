using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Venquery.Models;


// ---------------------------------------------------------------
// Backup & Restore (winget) view-models
// ---------------------------------------------------------------

/// <summary>One application discovered by <c>winget export</c>. "Restorable" entries
/// map to a winget package and go into backup.json; non-restorable ones have no winget
/// source and are listed for the user to reinstall by hand, we never guess a source.</summary>
public sealed class AppBackupEntry : INotifyPropertyChanged
{
    /// <summary>winget PackageIdentifier (e.g. "Google.Chrome"), or the friendly name
    /// for manual apps that winget couldn't map to any source.</summary>
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public bool Restorable { get; init; } = true;

    /// <summary>What the row shows as its title.</summary>
    public string DisplayName => Id;
    public string Detail => Restorable
        ? (string.IsNullOrEmpty(Version) ? "winget package" : $"winget · {Version}")
        : "not in winget, reinstall manually";

    // Only restorable apps can be (de)selected for inclusion in backup.json.
    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ---------------------------------------------------------------
// Portable Apps indexer view-models
// ---------------------------------------------------------------

/// <summary>A standalone .exe found on disk, with its Authenticode signature status.
/// Reuses the same OS trust check the downloader uses, so an unsigned loose binary is
/// flagged the same way here as a freshly downloaded one.</summary>
public sealed class PortableExe : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long SizeBytes { get; init; }
    public System.DateTime Modified { get; init; }
    public bool Signed { get; init; }
    public string Signer { get; init; } = "";

    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
    public string SizeText => FormatSize(SizeBytes);
    public string DateText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd");
    public string SignatureText => Signed
        ? (string.IsNullOrEmpty(Signer) ? "✓ signed" : $"✓ {Signer}")
        : "⚠ unsigned";
    // "ok" / "warn" drive the badge colour in XAML (green / amber), same convention
    // as DeviceRow.ProgressKind and the download signature note.
    public string SignatureKind => Signed ? "ok" : "warn";

    // Ticked rows go into the exported checklist. Unsigned files start unticked so the
    // user makes a deliberate choice to keep something the OS can't vouch for.
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnChanged(); } }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):0.0} KB";
        return $"{bytes} B";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ---------------------------------------------------------------
// Loose Documents backup view-model
// ---------------------------------------------------------------

/// <summary>A loose personal file or a documents folder found on the Desktop / C: root that the
/// user can copy into a Portable Documents backup. Nothing is pre-selected, the user picks.</summary>
public sealed class DocEntry : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsFolder { get; init; }
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }
    public Venquery.Services.DocCategory Categories { get; init; }
    public System.DateTime Modified { get; init; }

    public string Location => Path.GetDirectoryName(FullPath) ?? "";
    public string SizeText => FormatSize(SizeBytes);
    public string DateText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd");
    public string CategoryText => Venquery.Services.DocumentScanner.CategoriesLabel(Categories);
    public string KindText => IsFolder ? "folder" : "file";
    public string KindKind => IsFolder ? "folder" : "file";   // drives the badge colour in XAML
    public string Detail => IsFolder
        ? $"{FileCount} content file{(FileCount == 1 ? "" : "s")}  ·  {SizeText}"
        : $"{Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant()}  ·  {SizeText}";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnChanged(); } }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):0.0} KB";
        return $"{bytes} B";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
