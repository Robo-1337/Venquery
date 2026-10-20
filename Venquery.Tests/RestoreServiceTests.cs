using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Restore must recognise every kind of Venquery backup, only ever ADD files (never overwrite), and leave
/// installed apps / drivers to the backup's own guarded scripts.</summary>
public class RestoreServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "venq-restore-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    private string F(string rel, string content = "x")
    {
        var p = Path.Combine(_base, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private const string AppsJson = "{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":\"7zip.7zip\"},{\"PackageIdentifier\":\"Git.Git\"}]}]}";

    [Fact]
    public void A_full_reset_backup_shows_all_four_parts()
    {
        var root = Path.Combine(_base, "Venquery Backup - Contoso Laptop");
        F(@"Venquery Backup - Contoso Laptop\Apps\venquery-apps-backup.json", AppsJson);
        F(@"Venquery Backup - Contoso Laptop\Apps\venquery-apps-backup.ps1", "# restore");
        F(@"Venquery Backup - Contoso Laptop\Portable Apps\Tool\tool.exe", "MZ");
        F(@"Venquery Backup - Contoso Laptop\Documents\Taxes\2025.pdf");
        F(@"Venquery Backup - Contoso Laptop\Documents\notes.txt");
        F(@"Venquery Backup - Contoso Laptop\Drivers\restore-drivers.ps1", "# locked");
        F(@"Venquery Backup - Contoso Laptop\Drivers\net\e1d.inf");

        var inv = RestoreService.Inspect(root);

        Assert.Equal(4, inv.Parts.Count);
        Assert.All(inv.Parts, p => Assert.True(p.Available, p.Title));
        Assert.Equal(2, inv.Parts.Single(p => p.Kind == RestorePartKind.Apps).Items);
        Assert.EndsWith("venquery-apps-backup.ps1", inv.Parts.Single(p => p.Kind == RestorePartKind.Apps).ScriptPath);
        Assert.Equal(1, inv.Parts.Single(p => p.Kind == RestorePartKind.PortableApps).Items);
        Assert.Equal(2, inv.Parts.Single(p => p.Kind == RestorePartKind.Documents).Items);
        Assert.Equal(1, inv.Parts.Single(p => p.Kind == RestorePartKind.Drivers).Items);
    }

    [Fact]
    public void Drivers_are_unavailable_when_the_export_was_never_run()
    {
        var root = Path.Combine(_base, "Venquery Backup - PC");
        F(@"Venquery Backup - PC\venquery-driver-backup.ps1", "# export, never run");
        var d = RestoreService.Inspect(root).Parts.Single(p => p.Kind == RestorePartKind.Drivers);
        Assert.False(d.Available);
    }

    [Fact]
    public void A_loose_documents_backup_is_recognised_as_sorted_by_type()
    {
        var root = Path.Combine(_base, "Portable Documents");
        F(@"Portable Documents\Pictures\beach.jpg"); F(@"Portable Documents\Documents\cv.pdf");
        F(@"Portable Documents\Backup receipt 2026-10-18 101010.txt");    // Venquery's own receipt isn't "content"
        var part = Assert.Single(RestoreService.Inspect(root).Parts);
        Assert.Equal(RestorePartKind.Documents, part.Kind);
        Assert.True(part.SortedByType);
        Assert.Equal(2, part.Items);
    }

    [Fact]
    public void Not_a_backup_means_nothing_to_restore()
        => Assert.True(RestoreService.Inspect(Path.Combine(_base, "missing")).IsEmpty);

    [Fact]
    public async Task Documents_restored_into_a_chosen_folder_never_overwrite()
    {
        F(@"Venquery Backup - PC\Documents\notes.txt", "from backup");
        var target = Path.Combine(_base, "Target");
        var dated = Path.Combine(target, $"{RestoreService.RestoredFolderPrefix} {DateTime.Now:yyyy-MM-dd}");
        Directory.CreateDirectory(dated);
        File.WriteAllText(Path.Combine(dated, "notes.txt"), "already here");
        var part = RestoreService.Inspect(Path.Combine(_base, "Venquery Backup - PC")).Parts.Single();

        var r = await RestoreService.RestoreDocumentsAsync(part, target);

        Assert.Equal(1, r.Copied);
        Assert.Equal("already here", File.ReadAllText(Path.Combine(dated, "notes.txt")));
        Assert.Equal("from backup", File.ReadAllText(Path.Combine(dated, "notes (2).txt")));
    }

    [Fact]
    public async Task Portable_apps_are_copied_to_the_chosen_folder()
    {
        F(@"Portable Apps\Tool\tool.exe", "MZ"); F(@"Portable Apps\Other\other.exe", "MZ");
        var part = RestoreService.Inspect(Path.Combine(_base, "Portable Apps")).Parts.Single();
        var target = Path.Combine(_base, "Apps here");
        var r = await RestoreService.RestorePortableAsync(part, target);
        Assert.Equal(2, r.Copied);
        Assert.True(File.Exists(Path.Combine(target, "Tool", "tool.exe")));
    }

    [Theory]
    [InlineData("Pictures", Environment.SpecialFolder.MyPictures)]
    [InlineData("Videos", Environment.SpecialFolder.MyVideos)]
    [InlineData("Audio", Environment.SpecialFolder.MyMusic)]
    [InlineData("Documents", Environment.SpecialFolder.Personal)]
    [InlineData("Disk Images", Environment.SpecialFolder.Personal)]
    public void Type_folders_go_back_to_the_matching_windows_library(string folder, Environment.SpecialFolder lib)
        => Assert.Equal(lib, RestoreService.KnownCategory(folder));
}
