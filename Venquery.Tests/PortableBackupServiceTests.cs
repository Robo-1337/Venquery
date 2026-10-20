using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the "back up selected app folders" copy: it must copy full folders, never
/// overwrite existing ones, and refuse to copy a folder into itself.</summary>
public class PortableBackupServiceTests : System.IDisposable
{
    private readonly string _work;
    public PortableBackupServiceTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "venquery-backuptest-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }
    public void Dispose() { try { Directory.Delete(_work, true); } catch { } }

    private string MakeApp(string name, params (string rel, string content)[] files)
    {
        var dir = Path.Combine(_work, "src", name);
        Directory.CreateDirectory(dir);
        foreach (var (rel, content) in files)
        {
            var p = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
        }
        return dir;
    }

    [Fact]
    public async Task Copies_the_whole_folder_including_subfolders()
    {
        var app = MakeApp("Sunshine", ("sunshine.exe", "MZ"), ("config.ini", "x"), (@"data\pref.json", "{}"));
        var dest = Path.Combine(_work, "out");

        var res = await PortableBackupService.BackupAsync(new[] { app }, dest);

        Assert.True(res.Ok);
        Assert.Equal(1, res.Copied);
        Assert.Equal(0, res.Skipped);
        Assert.True(File.Exists(Path.Combine(dest, "Sunshine", "sunshine.exe")));
        Assert.True(File.Exists(Path.Combine(dest, "Sunshine", "config.ini")));
        Assert.True(File.Exists(Path.Combine(dest, "Sunshine", "data", "pref.json")));
    }

    [Fact]
    public async Task Existing_folder_is_never_overwritten_a_second_copy_is_suffixed()
    {
        var app = MakeApp("Tool", ("tool.exe", "v2"));
        var dest = Path.Combine(_work, "out");
        Directory.CreateDirectory(Path.Combine(dest, "Tool"));
        File.WriteAllText(Path.Combine(dest, "Tool", "existing.txt"), "keep me");

        var res = await PortableBackupService.BackupAsync(new[] { app }, dest);

        Assert.Equal(1, res.Copied);
        Assert.True(File.Exists(Path.Combine(dest, "Tool", "existing.txt")));   // original untouched
        Assert.True(File.Exists(Path.Combine(dest, "Tool (2)", "tool.exe")));   // new copy suffixed
    }

    [Fact]
    public async Task Missing_source_is_skipped_not_fatal()
    {
        var ok = MakeApp("Good", ("good.exe", "MZ"));
        var missing = Path.Combine(_work, "src", "Ghost");
        var dest = Path.Combine(_work, "out");

        var res = await PortableBackupService.BackupAsync(new[] { ok, missing }, dest);

        Assert.True(res.Ok);
        Assert.Equal(1, res.Copied);
        Assert.Equal(1, res.Skipped);
        Assert.True(File.Exists(Path.Combine(dest, "Good", "good.exe")));
    }

    [Fact]
    public async Task Refuses_to_copy_a_folder_into_itself()
    {
        var app = MakeApp("SelfHost", ("selfhost.exe", "MZ"));
        var dest = Path.Combine(app, "Portable Apps");   // destination inside the source

        var res = await PortableBackupService.BackupAsync(new[] { app }, dest);

        Assert.True(res.Ok);
        Assert.Equal(0, res.Copied);
        Assert.Equal(1, res.Skipped);
        Assert.Contains(res.Notes, n => n.Contains("itself"));
    }

    [Fact]
    public async Task Default_destination_is_a_portable_apps_folder_in_downloads()
    {
        var dest = PortableBackupService.DefaultDestination();
        Assert.EndsWith("Portable Apps", dest);
        Assert.Contains("Downloads", dest);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BackupByCategory_sorts_a_folders_files_into_per_type_subfolders_and_honours_the_filter()
    {
        var dir = Path.Combine(_work, "src", "Mixed");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "report.docx"), "d");
        File.WriteAllText(Path.Combine(dir, "photo.png"), "p");
        File.WriteAllText(Path.Combine(dir, "clip.mp4"), "v");
        var dest = Path.Combine(_work, "out");

        var res = await PortableBackupService.BackupByCategoryAsync(
            new[] { dir }, dest, DocCategory.Documents | DocCategory.Pictures);

        Assert.True(res.Ok);
        Assert.True(File.Exists(Path.Combine(dest, "Documents", "Mixed", "report.docx")));
        Assert.True(File.Exists(Path.Combine(dest, "Pictures", "Mixed", "photo.png")));
        Assert.False(Directory.Exists(Path.Combine(dest, "Videos")));   // filtered out
    }

    [Fact]
    public async Task BackupByCategory_puts_a_loose_file_under_its_category_root()
    {
        var docFile = Path.Combine(_work, "src", "loose.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(docFile)!);
        File.WriteAllText(docFile, "x");
        var dest = Path.Combine(_work, "out2");

        var res = await PortableBackupService.BackupByCategoryAsync(new[] { docFile }, dest, DocumentScanner.AllCategories);

        Assert.True(res.Ok);
        Assert.True(File.Exists(Path.Combine(dest, "Documents", "loose.pdf")));
    }

    [Fact]
    public async Task A_second_copy_of_a_file_keeps_its_extension()
    {
        // Regression: duplicates used to be named "loose.pdf (2)", which no longer opens as a PDF.
        var docFile = Path.Combine(_work, "src", "loose.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(docFile)!);
        File.WriteAllText(docFile, "x");
        var dest = Path.Combine(_work, "out3");

        await PortableBackupService.BackupByCategoryAsync(new[] { docFile }, dest, DocumentScanner.AllCategories);
        await PortableBackupService.BackupByCategoryAsync(new[] { docFile }, dest, DocumentScanner.AllCategories);
        await PortableBackupService.BackupAsync(new[] { docFile }, dest);
        await PortableBackupService.BackupAsync(new[] { docFile }, dest);

        Assert.True(File.Exists(Path.Combine(dest, "Documents", "loose.pdf")));
        Assert.True(File.Exists(Path.Combine(dest, "Documents", "loose (2).pdf")));
        Assert.True(File.Exists(Path.Combine(dest, "loose.pdf")));
        Assert.True(File.Exists(Path.Combine(dest, "loose (2).pdf")));
    }

    [Fact]
    public void Duplicate_folder_names_get_a_counter_suffix()
    {
        var dir = Path.Combine(_work, "Photos");
        Directory.CreateDirectory(dir);
        Assert.Equal(Path.Combine(_work, "Photos (2)"), PortableBackupService.UniquePath(dir));
    }

    [Theory]
    [InlineData(System.Environment.SpecialFolder.Windows, true)]
    [InlineData(System.Environment.SpecialFolder.ProgramFiles, true)]
    [InlineData(System.Environment.SpecialFolder.CommonApplicationData, true)]
    [InlineData(System.Environment.SpecialFolder.MyDocuments, false)]
    public void Backup_destination_refuses_system_folders(System.Environment.SpecialFolder f, bool system)
    {
        var path = Path.Combine(System.Environment.GetFolderPath(f), "Backups");
        Assert.Equal(system, Venquery.MainWindow.IsSystemLocation(path));
    }

    [Fact]
    public async Task A_receipt_lists_and_verifies_every_copied_file()
    {
        var app = MakeApp("Tool", ("tool.exe", "MZ-1234"), (@"data\cfg.json", "{}"));
        var dest = Path.Combine(_work, "rc");
        var receipt = new BackupReceipt();
        var res = await PortableBackupService.BackupAsync(new[] { app }, dest, null, default, receipt);
        Assert.True(res.Ok);
        Assert.Equal(2, receipt.Files.Count);

        var (ok, bad, path) = receipt.WriteAndVerify(dest, "test backup");
        Assert.Equal(2, ok);
        Assert.Equal(0, bad);
        var text = File.ReadAllText(path);
        Assert.Contains("2 file(s) verified", text);
        Assert.Contains(Path.Combine("Tool", "data", "cfg.json"), text);

        // A copy that went missing or changed size is flagged, not hidden.
        File.WriteAllText(Path.Combine(dest, "Tool", "tool.exe"), "x");
        var (_, bad2, path2) = receipt.WriteAndVerify(dest, "again");
        Assert.Equal(1, bad2);
        Assert.Contains("CHECK", File.ReadAllText(path2));
    }
}
