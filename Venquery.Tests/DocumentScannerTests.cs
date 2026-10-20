using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the loose-documents classifier: a folder is captured only if it holds personal
/// content and no executable; empty/junk/app folders are skipped; loose personal files are captured.</summary>
public class DocumentScannerTests : System.IDisposable
{
    private readonly string _root;
    public DocumentScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "venquery-doctest-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void File_(string relPath, string content = "x")
    {
        var p = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
    }

    private async Task<DocScanResult> Scan() => await DocumentScanner.ScanAsync(new[] { _root });

    private static string[] Names(DocScanResult r) => r.Items.Select(i => i.Name).OrderBy(n => n).ToArray();

    [Fact]
    public async Task Folder_with_documents_and_no_exe_is_captured()
    {
        File_(@"Taxes\2025-return.pdf");
        File_(@"Taxes\receipts\scan1.jpg");
        var r = await Scan();
        Assert.Contains("Taxes", Names(r));
        var taxes = r.Items.Single(i => i.Name == "Taxes");
        Assert.True(taxes.IsFolder);
        Assert.Equal(2, taxes.FileCount);
    }

    [Fact]
    public async Task Folder_containing_an_exe_is_skipped_even_with_documents()
    {
        File_(@"SomeApp\readme.txt");
        File_(@"SomeApp\app.exe");
        Assert.DoesNotContain("SomeApp", Names(await Scan()));
    }

    [Fact]
    public async Task Folder_with_a_dll_deep_inside_is_skipped()
    {
        File_(@"Mixed\notes.docx");
        File_(@"Mixed\lib\engine.dll");
        Assert.DoesNotContain("Mixed", Names(await Scan()));
    }

    [Fact]
    public void CategoryOf_maps_extensions_to_the_right_category()
    {
        Assert.Equal(DocCategory.Documents, DocumentScanner.CategoryOf(@"c:\x\report.docx"));
        Assert.Equal(DocCategory.Documents, DocumentScanner.CategoryOf(@"c:\x\scan.pdf"));
        Assert.Equal(DocCategory.Pictures, DocumentScanner.CategoryOf(@"c:\x\photo.JPG"));
        Assert.Equal(DocCategory.Videos, DocumentScanner.CategoryOf(@"c:\x\clip.mp4"));
        Assert.Equal(DocCategory.Audio, DocumentScanner.CategoryOf(@"c:\x\song.flac"));
        Assert.Equal(DocCategory.Archives, DocumentScanner.CategoryOf(@"c:\x\bundle.zip"));
        Assert.Equal(DocCategory.None, DocumentScanner.CategoryOf(@"c:\x\app.exe"));
    }

    [Theory]
    [InlineData("mail.pst", DocCategory.Documents)]
    [InlineData("message.eml", DocCategory.Documents)]
    [InlineData("contacts.vcf", DocCategory.Documents)]
    [InlineData("photo.avif", DocCategory.Pictures)]
    [InlineData("photo.HEIC", DocCategory.Pictures)]
    [InlineData("raw.raf", DocCategory.Pictures)]
    [InlineData("phone.3gp", DocCategory.Videos)]
    [InlineData("camcorder.m2ts", DocCategory.Videos)]
    [InlineData("voice.opus", DocCategory.Audio)]
    [InlineData("book.m4b", DocCategory.Audio)]
    [InlineData("backup.zst", DocCategory.Archives)]
    [InlineData("Win11_24H2.iso", DocCategory.DiskImages)]
    [InlineData("disk.vhdx", DocCategory.DiskImages)]
    [InlineData("vm.vmdk", DocCategory.DiskImages)]
    [InlineData("installer.dmg", DocCategory.DiskImages)]
    [InlineData("install.wim", DocCategory.DiskImages)]
    // Ambiguous extensions stay out: firmware blobs and SQL Server data files are not disk images.
    [InlineData("firmware.bin", DocCategory.None)]
    [InlineData("database.mdf", DocCategory.None)]
    // TypeScript sources and the Outlook cache are NOT personal media.
    [InlineData("index.ts", DocCategory.None)]
    [InlineData("module.mts", DocCategory.None)]
    [InlineData("cache.ost", DocCategory.None)]
    public void CategoryOf_covers_common_personal_formats(string file, DocCategory expected)
        => Assert.Equal(expected, DocumentScanner.CategoryOf(@"c:\x\" + file));

    [Fact]
    public async Task Folder_categories_are_the_union_of_its_files()
    {
        File_(@"Mix\a.docx");
        File_(@"Mix\pics\b.png");
        File_(@"Mix\clip.mp4");
        var folder = (await Scan()).Items.Single(i => i.Name == "Mix");
        Assert.Equal(DocCategory.Documents | DocCategory.Pictures | DocCategory.Videos, folder.Categories);
    }

    [Fact]
    public async Task Scanned_items_carry_a_recent_modified_date()
    {
        File_("lonely.pdf");
        File_(@"Work\report.docx");
        var r = await Scan();
        var cutoff = System.DateTime.Now.AddMinutes(-5);
        Assert.True(r.Items.Single(i => i.Name == "lonely.pdf").Modified > cutoff);
        Assert.True(r.Items.Single(i => i.Name == "Work").Modified > cutoff);   // folder = newest file mtime
    }

    [Fact]
    public async Task Archives_only_folder_is_still_captured()
    {
        // Archives are content, not an exclusion trigger — an archives-only folder must be kept.
        File_(@"Backups\a.zip");
        File_(@"Backups\b.7z");
        var folder = (await Scan()).Items.Single(i => i.Name == "Backups");
        Assert.Equal(DocCategory.Archives, folder.Categories);
    }

    [Fact]
    public async Task Empty_or_junk_folder_is_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Empty"));
        File_(@"Junk\desktop.ini");
        File_(@"Junk\shortcut.lnk");
        var names = Names(await Scan());
        Assert.DoesNotContain("Empty", names);
        Assert.DoesNotContain("Junk", names);
    }

    [Fact]
    public async Task Loose_personal_files_at_the_root_are_captured_but_not_junk()
    {
        File_("resume.pdf");
        File_("holiday.png");
        File_("notes.lnk");     // not personal content
        var names = Names(await Scan());
        Assert.Contains("resume.pdf", names);
        Assert.Contains("holiday.png", names);
        Assert.DoesNotContain("notes.lnk", names);
    }

    [Fact]
    public async Task Media_and_archives_count_as_content()
    {
        File_(@"Clips\family.mp4");
        File_(@"Backups\old.zip");
        var names = Names(await Scan());
        Assert.Contains("Clips", names);
        Assert.Contains("Backups", names);
    }

    [Fact]
    public async Task Captured_items_are_marked_folder_vs_file_correctly()
    {
        File_(@"Photos\a.jpg");
        File_("lonely.pdf");
        var r = await Scan();
        Assert.True(r.Items.Single(i => i.Name == "Photos").IsFolder);
        Assert.False(r.Items.Single(i => i.Name == "lonely.pdf").IsFolder);
    }

    [Fact]
    public void Default_roots_include_the_users_personal_folders()
    {
        var roots = DocumentScanner.DefaultRoots();
        // The bug this fixes: the Documents folder was never scanned. It (and the other known folders) must
        // now be roots.
        Assert.Contains(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), roots);
        Assert.Contains(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), roots);
        Assert.Contains(System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory), roots);
    }

    [Fact]
    public void Overlapping_roots_never_list_the_same_files_twice()
    {
        // A picked folder inside an area that's already scanned: its children are covered by the parent's folder
        // item, and an exact duplicate path is kept once.
        DocItem I(string p, bool folder) => new(p, System.IO.Path.GetFileName(p), folder, 1, 1, DocCategory.Documents, System.DateTime.MinValue);
        var items = new System.Collections.Generic.List<DocItem>
        {
            I(@"C:\Users\me\Documents\Work", true),
            I(@"C:\Users\me\Documents\Work\a.pdf", false),        // from the picked folder Work
            I(@"C:\Users\me\Documents\Work\Sub", true),           // from the picked folder Work
            I(@"C:\Users\me\Documents\Workbook.xlsx", false),     // NOT inside Work (prefix only)
            I(@"D:\Stuff\b.pdf", false),
            I(@"D:\Stuff\b.pdf", false),                          // exact duplicate
        };
        var kept = DocumentScanner.DropCovered(items).Select(i => i.FullPath).ToList();
        Assert.Equal(new[] { @"C:\Users\me\Documents\Work", @"C:\Users\me\Documents\Workbook.xlsx", @"D:\Stuff\b.pdf" }, kept);
    }

    [Fact]
    public async Task A_picked_folder_is_scanned_on_its_own()
    {
        File_(@"Projects\plan.docx");
        File_(@"Projects\Old\notes.txt");
        File_("disk.iso");
        var r = await DocumentScanner.ScanAsync(new[] { _root });
        Assert.Contains("Projects", Names(r));
        Assert.Contains("disk.iso", Names(r));
        Assert.Equal(DocCategory.DiskImages, r.Items.Single(i => i.Name == "disk.iso").Categories);
    }

    [Fact]
    public void Nothing_picked_means_nothing_scanned()
    {
        // C: is never scanned implicitly, the user has to pick a drive.
        Assert.Empty(DocumentScanner.RootsFor(System.Array.Empty<string>()));
    }

    [Fact]
    public void Picking_the_windows_drive_scans_personal_folders_and_its_root_once()
    {
        var os = System.IO.Path.GetPathRoot(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows))!;
        var roots = DocumentScanner.RootsFor(new[] { os, os.TrimEnd('\\') });   // "C:\" and "C:" picked twice
        Assert.Contains(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), roots);
        Assert.Contains(System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory), roots);
        Assert.Single(roots, r => string.Equals(r, os, System.StringComparison.OrdinalIgnoreCase));
        Assert.Equal(roots.Count, roots.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Picking_another_drive_never_adds_the_windows_drive()
    {
        var os = System.IO.Path.GetPathRoot(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows))!;
        var other = string.Equals(os, @"Z:\", System.StringComparison.OrdinalIgnoreCase) ? @"Y:\" : @"Z:\";
        var roots = DocumentScanner.RootsFor(new[] { other });
        Assert.Equal(new[] { other }, roots);
    }

    [Fact]
    public async Task Venquery_own_backup_folders_are_not_re_offered()
    {
        // A re-scan must not offer to back up Venquery's own output folders.
        File_(@"Portable Documents\copied.pdf");
        File_(@"Venquery Backup - My PC\copied.docx");
        File_(@"SystemReports\report.txt");
        File_(@"Work\real.docx");     // a genuine folder still shows
        var names = Names(await Scan());
        Assert.DoesNotContain("Portable Documents", names);
        Assert.DoesNotContain("Venquery Backup - My PC", names);
        Assert.DoesNotContain("SystemReports", names);
        Assert.Contains("Work", names);
    }
}
