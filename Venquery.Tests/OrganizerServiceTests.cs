using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Guards the File Organizer engine: what it classifies, what it refuses to touch, that a run moves
/// exactly what the plan says without ever overwriting, and that Undo puts everything back.</summary>
public class OrganizerServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _history;

    public OrganizerServiceTests()
    {
        var b = Path.Combine(Path.GetTempPath(), "venq-org-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(b, "Downloads");
        _history = Path.Combine(b, "history");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, true); } catch { }
    }

    private string F(string rel, string content = "x", DateTime? modified = null)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        if (modified is not null) File.SetLastWriteTime(p, modified.Value);
        return p;
    }

    private static readonly ISet<OrgKind> All = new HashSet<OrgKind>(OrganizerService.AllKinds);

    [Theory]
    [InlineData("holiday.JPG", OrgKind.Pictures)]
    [InlineData("clip.mkv", OrgKind.Videos)]
    [InlineData("song.mp3", OrgKind.Music)]
    [InlineData("invoice.pdf", OrgKind.Documents)]
    [InlineData("pack.zip", OrgKind.Archives)]
    [InlineData("win11.iso", OrgKind.DiskImages)]
    [InlineData("setup.exe", OrgKind.Installers)]
    [InlineData("App.msix", OrgKind.Installers)]
    [InlineData("script.py", OrgKind.Code)]
    [InlineData("fix.ps1", OrgKind.Code)]
    [InlineData("Inter.ttf", OrgKind.Fonts)]
    [InlineData("thing.xyz", OrgKind.Other)]
    public void Files_are_classified_by_extension(string name, OrgKind kind)
        => Assert.Equal(kind, OrganizerService.KindOf(name));

    [Fact]
    public async Task Scan_leaves_downloads_in_progress_shortcuts_and_hidden_files_alone()
    {
        F("photo.png"); F("big.iso.crdownload"); F("movie.mp4.part"); F("App.lnk"); F("desktop.ini"); F("~$report.docx");
        var hidden = F("secret.txt"); File.SetAttributes(hidden, FileAttributes.Hidden);
        F(@"Sub\inside.pdf");

        var r = await OrganizerService.ScanAsync(_root, includeSubfolders: false);

        Assert.Equal(new[] { "photo.png" }, r.Files.Select(f => f.Name).ToArray());   // sub folder not walked either
        Assert.True(r.SkippedFiles >= 6);
    }

    [Fact]
    public async Task Scan_with_subfolders_never_breaks_up_apps_or_code_projects()
    {
        F(@"Trip\beach.jpg");
        F(@"PortableApp\tool.exe"); F(@"PortableApp\engine.dll"); F(@"PortableApp\readme.txt");
        F(@"MyProject\src\main.py"); Directory.CreateDirectory(Path.Combine(_root, "MyProject", ".git"));
        F(@"WebApp\package.json", "{}"); F(@"WebApp\index.js");
        F(@"Pictures\already.png");                       // the organizer's own output folder

        var r = await OrganizerService.ScanAsync(_root, includeSubfolders: true);

        Assert.Equal(new[] { "beach.jpg" }, r.Files.Select(f => f.Name).ToArray());
        Assert.Equal("Trip", r.Files[0].RelativeFolder);
        Assert.True(r.SkippedFolders >= 4);
    }

    [Fact]
    public void Refuses_drive_roots_system_folders_and_the_profile_itself()
    {
        Assert.NotNull(OrganizerService.WhyNot(@"C:\"));
        Assert.NotNull(OrganizerService.WhyNot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.NotNull(OrganizerService.WhyNot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files")));
        Assert.NotNull(OrganizerService.WhyNot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.NotNull(OrganizerService.WhyNot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        Assert.NotNull(OrganizerService.WhyNot(Path.Combine(_root, "does-not-exist")));
        Assert.NotNull(OrganizerService.WhyNot(""));
        // A normal folder on a non-system path is allowed (Documents always exists).
        Assert.Null(OrganizerService.WhyNot(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
    }

    [Fact]
    public async Task Plan_by_type_sorts_into_type_folders_with_optional_date_level()
    {
        F("a.png", modified: new DateTime(2024, 3, 9)); F("b.mp4", modified: new DateTime(2025, 11, 1)); F("c.exe");
        F(@"Pictures\done.png");                           // already where it belongs
        var files = (await OrganizerService.ScanAsync(_root, false)).Files;
        var withAlready = files.Concat(new[] { new OrgFile { FullPath = Path.Combine(_root, "Pictures", "done.png"), Name = "done.png", Kind = OrgKind.Pictures } });

        var plain = OrganizerService.PlanByType(_root, withAlready, All, OrgDestination.SubfoldersHere, OrgDateSplit.None);
        Assert.Equal(3, plain.Moves.Count);
        Assert.Equal(1, plain.AlreadyInPlace);
        Assert.Contains(plain.Moves, m => m.Source.EndsWith("a.png") && m.DestFolder == Path.Combine(_root, "Pictures"));
        Assert.Contains(plain.Moves, m => m.Source.EndsWith("c.exe") && m.DestFolder == Path.Combine(_root, "Installers"));

        var dated = OrganizerService.PlanByType(_root, files, All, OrgDestination.SubfoldersHere, OrgDateSplit.YearMonth);
        Assert.Contains(dated.Moves, m => m.Source.EndsWith("a.png") && m.DestFolder == Path.Combine(_root, "Pictures", "2024", "2024-03"));
        Assert.Contains(dated.Moves, m => m.Source.EndsWith("b.mp4") && m.DestFolder == Path.Combine(_root, "Videos", "2025", "2025-11"));

        var onlyPics = OrganizerService.PlanByType(_root, files, new HashSet<OrgKind> { OrgKind.Pictures }, OrgDestination.SubfoldersHere, OrgDateSplit.None);
        Assert.Single(onlyPics.Moves);

        var libs = OrganizerService.PlanByType(_root, files, All, OrgDestination.WindowsLibraries, OrgDateSplit.None);
        Assert.Contains(libs.Moves, m => m.Source.EndsWith("a.png") && m.DestFolder == Environment.GetFolderPath(Environment.SpecialFolder.MyPictures).TrimEnd('\\'));
        Assert.Contains(libs.Moves, m => m.Source.EndsWith("c.exe") && m.DestFolder == Path.Combine(_root, "Installers"));   // no library for installers
    }

    [Fact]
    public async Task Run_moves_files_never_overwrites_and_undo_restores_everything()
    {
        var a = F("report.pdf", "new");
        var b = F("photo.jpg");
        var existing = F(@"Documents\report.pdf", "OLD");   // a clash in the destination
        var files = (await OrganizerService.ScanAsync(_root, false)).Files;
        var plan = OrganizerService.PlanByType(_root, files, All, OrgDestination.SubfoldersHere, OrgDateSplit.None);

        var run = await OrganizerService.ExecuteAsync(plan, historyDir: _history);

        Assert.Equal(2, run.Moved);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.Equal("OLD", File.ReadAllText(existing));                                      // untouched
        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "Documents", "report (2).pdf")));
        Assert.True(File.Exists(Path.Combine(_root, "Pictures", "photo.jpg")));
        Assert.True(File.Exists(run.LogPath));
        Assert.Single(OrganizerService.History(_history));

        var undo = await OrganizerService.UndoAsync(run.LogPath);

        Assert.Equal(2, undo.Moved);
        Assert.Equal("new", File.ReadAllText(a));
        Assert.True(File.Exists(b));
        Assert.False(Directory.Exists(Path.Combine(_root, "Pictures")));                        // created by the run, now empty, removed
        Assert.True(Directory.Exists(Path.Combine(_root, "Documents")));                        // existed before, kept
        Assert.Equal("OLD", File.ReadAllText(existing));
        Assert.True(OrganizerService.History(_history).Single().Undone);

        var again = await OrganizerService.UndoAsync(run.LogPath);
        Assert.Equal(0, again.Moved);
    }

    [Fact]
    public async Task Undo_never_overwrites_and_can_retry_the_leftovers()
    {
        var a = F("notes.txt", "mine");
        var files = (await OrganizerService.ScanAsync(_root, false)).Files;
        var run = await OrganizerService.ExecuteAsync(OrganizerService.PlanByType(_root, files, All, OrgDestination.SubfoldersHere, OrgDateSplit.None), historyDir: _history);
        File.WriteAllText(a, "someone else");                // the original name is taken again

        var undo = await OrganizerService.UndoAsync(run.LogPath);
        Assert.Equal(0, undo.Moved);
        Assert.Equal(1, undo.Skipped);
        Assert.Equal("someone else", File.ReadAllText(a));    // not overwritten
        Assert.False(OrganizerService.History(_history).Single().Undone);

        File.Delete(a);
        var retry = await OrganizerService.UndoAsync(run.LogPath);
        Assert.Equal(1, retry.Moved);
        Assert.Equal("mine", File.ReadAllText(a));
        Assert.True(OrganizerService.History(_history).Single().Undone);
    }

    [Fact]
    public async Task A_kept_run_is_final_and_cannot_be_undone()
    {
        var a = F("notes.txt", "mine");
        var files = (await OrganizerService.ScanAsync(_root, false)).Files;
        var run = await OrganizerService.ExecuteAsync(OrganizerService.PlanByType(_root, files, All, OrgDestination.SubfoldersHere, OrgDateSplit.None), historyDir: _history);

        Assert.True(OrganizerService.KeepRun(run.LogPath));
        var h = OrganizerService.History(_history).Single();
        Assert.True(h.Kept);
        Assert.False(h.CanUndo);
        Assert.Equal("", OrganizerService.UndoHint(h, 2));

        var undo = await OrganizerService.UndoAsync(run.LogPath);
        Assert.Equal(0, undo.Moved);
        Assert.False(File.Exists(a));                                                          // stayed organized
        Assert.True(File.Exists(Path.Combine(_root, "Documents", "notes.txt")));
        Assert.False(OrganizerService.KeepRun(Path.Combine(_history, "missing.json")));
    }

    [Fact]
    public async Task Retention_removes_only_expired_undo_records_and_never_touches_files()
    {
        F("notes.txt");
        var files = (await OrganizerService.ScanAsync(_root, false)).Files;
        var run = await OrganizerService.ExecuteAsync(OrganizerService.PlanByType(_root, files, All, OrgDestination.SubfoldersHere, OrgDateSplit.None), historyDir: _history);
        var when = OrganizerService.History(_history).Single().When;
        var moved = Path.Combine(_root, "Documents", "notes.txt");

        Assert.Equal(0, OrganizerService.PruneHistory(2, _history, when.AddDays(2).AddMinutes(-1)));   // still inside 2 days
        Assert.Equal(0, OrganizerService.PruneHistory(0, _history, when.AddYears(5)));                 // 0 = never expire
        Assert.True(File.Exists(run.LogPath));

        Assert.Equal(1, OrganizerService.PruneHistory(2, _history, when.AddDays(2).AddMinutes(1)));
        Assert.False(File.Exists(run.LogPath));
        Assert.Empty(OrganizerService.History(_history));
        Assert.True(File.Exists(moved));                                                       // the user's file is untouched
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(7, 7)]
    [InlineData(30, 30)]
    [InlineData(0, 0)]
    [InlineData(-5, 2)]     // junk in settings.json falls back to the default
    [InlineData(13, 2)]
    public void Undo_window_setting_is_normalized(int stored, int expected)
        => Assert.Equal(expected, OrganizerService.NormalizeUndoDays(stored));

    [Fact]
    public void Undo_hint_names_the_expiry_or_says_any_time()
    {
        var when = new DateTime(2026, 10, 18, 9, 30, 0);
        var h = new OrgHistoryEntry("x.json", when, @"C:\x", "By type", 3, Undone: false);
        Assert.Equal($"Can be undone until {when.AddDays(2):g}", OrganizerService.UndoHint(h, 2));
        Assert.Equal("Can be undone any time", OrganizerService.UndoHint(h, 0));
        Assert.Equal("", OrganizerService.UndoHint(h with { Undone = true }, 2));
        Assert.Null(OrganizerService.UndoExpires(when, 0));
    }

    [Fact]
    public async Task A_file_open_in_another_app_is_skipped_not_forced()
    {
        var locked = F("busy.pdf");
        F("free.pdf");
        var files = (await OrganizerService.ScanAsync(_root, false)).Files;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var run = await OrganizerService.ExecuteAsync(OrganizerService.PlanByType(_root, files, All, OrgDestination.SubfoldersHere, OrgDateSplit.None), historyDir: _history);
            Assert.Equal(1, run.Moved);
            Assert.Equal(1, run.Skipped);
        }
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public async Task Move_ticked_files_to_a_chosen_folder()
    {
        F("one.txt"); F("two.png");
        var target = Path.Combine(Path.GetDirectoryName(_root)!, "Elsewhere");
        var files = (await OrganizerService.ScanAsync(_root, false)).Files;
        var plan = OrganizerService.PlanMoveTo(_root, files.Where(f => f.Name == "two.png"), target);
        var run = await OrganizerService.ExecuteAsync(plan, historyDir: _history);
        Assert.Equal(1, run.Moved);
        Assert.True(File.Exists(Path.Combine(target, "two.png")));
        Assert.True(File.Exists(Path.Combine(_root, "one.txt")));
    }

    [Fact]
    public async Task Duplicates_are_found_by_content_and_the_original_is_kept()
    {
        F("setup.exe", "SAME-BYTES", new DateTime(2024, 1, 1));
        F("setup (1).exe", "SAME-BYTES", new DateTime(2024, 1, 2));
        F("other.exe", "SAME-SIZE!");                          // same length, different content
        F("empty1.txt", ""); F("empty2.txt", "");             // empty files are never "duplicates"
        var files = (await OrganizerService.ScanAsync(_root, false)).Files;

        var groups = await OrganizerService.FindDuplicatesAsync(files);

        var g = Assert.Single(groups);
        Assert.Equal("setup.exe", g[0].Name);                  // kept
        Assert.Equal("setup (1).exe", g[1].Name);

        var plan = OrganizerService.PlanDuplicates(_root, groups);
        var run = await OrganizerService.ExecuteAsync(plan, historyDir: _history);
        Assert.Equal(1, run.Moved);
        Assert.True(File.Exists(Path.Combine(_root, "setup.exe")));
        Assert.True(File.Exists(Path.Combine(_root, "Duplicates", "setup (1).exe")));
    }

    [Fact]
    public async Task Custom_rules_send_an_extension_or_a_renamed_type_to_your_folder()
    {
        F("art.psd"); F("photo.png"); F("setup.exe");
        var rules = OrgRules.From(new[] { (".psd", @"Design\Photoshop"), ("Installers", "Setup files"), ("png", "../escape") });
        var files = (await OrganizerService.ScanAsync(_root, false)).Files;

        var plan = OrganizerService.PlanByType(_root, files, All, OrgDestination.SubfoldersHere, OrgDateSplit.None, rules);
        Assert.Contains(plan.Moves, m => m.Source.EndsWith("art.psd") && m.DestFolder == Path.Combine(_root, "Design", "Photoshop"));
        Assert.Contains(plan.Moves, m => m.Source.EndsWith("setup.exe") && m.DestFolder == Path.Combine(_root, "Setup files"));
        Assert.Contains(plan.Moves, m => m.Source.EndsWith("photo.png") && m.DestFolder == Path.Combine(_root, "Pictures"));   // the bad rule was ignored

        // An extension rule wins even when pictures go to the Windows library.
        var libs = OrganizerService.PlanByType(_root, files, All, OrgDestination.WindowsLibraries, OrgDateSplit.None,
            OrgRules.From(new[] { (".png", "Screens") }));
        Assert.Contains(libs.Moves, m => m.Source.EndsWith("photo.png") && m.DestFolder == Path.Combine(_root, "Screens"));

        // After organizing, a subfolder scan doesn't re-sort what's already in a rule folder.
        await OrganizerService.ExecuteAsync(plan, historyDir: _history);
        var again = await OrganizerService.ScanAsync(_root, includeSubfolders: true, rules: rules);
        Assert.Empty(again.Files);
    }

    [Theory]
    [InlineData("Design", true)]
    [InlineData(@"Design\Photoshop", true)]
    [InlineData("Design/Raw", true)]
    [InlineData(@"a\b\c\d", false)]       // too deep
    [InlineData(@"..\Elsewhere", false)]
    [InlineData(@"C:\Temp", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData("CON", false)]
    [InlineData("bad|name", false)]
    [InlineData("ends.", false)]
    [InlineData("  ", false)]
    public void Rule_folders_must_be_simple_relative_names(string folder, bool ok)
        => Assert.Equal(ok, OrganizerService.WhyBadFolder(folder) is null);

    [Theory]
    [InlineData("psd", ".psd")]
    [InlineData(" .PSD ", ".psd")]
    [InlineData("*.blend", ".blend")]
    [InlineData("bad ext", null)]
    [InlineData("", null)]
    public void Rule_extensions_are_normalised(string input, string? expected)
        => Assert.Equal(expected, OrganizerService.CleanExtension(input));

    [Theory]
    [InlineData("setup (1).exe", true)]
    [InlineData("Report - Copy.docx", true)]
    [InlineData("Report - Copy (2).docx", true)]
    [InlineData("photo_copy.jpg", true)]
    [InlineData("setup.exe", false)]
    [InlineData("Copy of plan.pdf", false)]
    public void Recognises_copy_style_names(string name, bool copy)
        => Assert.Equal(copy, OrganizerService.LooksLikeCopy(name));
}
