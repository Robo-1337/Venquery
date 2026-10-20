using System.IO;
using Venquery.Services;
using Xunit;

namespace Venquery.Tests;

/// <summary>Covers the background-image import rules — importantly the regression where picking a
/// file that already lives in the Themes folder threw "being used by another process" (a file being
/// copied onto itself).</summary>
public class ThemeAssetsTests
{
    [Fact]
    public void Import_file_already_in_themes_folder_uses_it_in_place()
    {
        var folder = ThemeAssets.Folder();
        var existing = Path.Combine(folder, "already-here.png");
        File.WriteAllBytes(existing, new byte[] { 1, 2, 3, 4 });
        try
        {
            // Must not throw (the bug) and must return the same file name, unchanged.
            var name = ThemeAssets.Import(existing);
            Assert.Equal("already-here.png", name);
            Assert.True(File.Exists(Path.Combine(folder, name)));
        }
        finally { File.Delete(existing); }
    }

    [Fact]
    public void Import_file_from_elsewhere_copies_into_themes_folder()
    {
        var temp = Path.Combine(Path.GetTempPath(), "vq-import-src.png");
        File.WriteAllBytes(temp, new byte[] { 9, 8, 7 });
        var dest = Path.Combine(ThemeAssets.Folder(), "vq-import-src.png");
        try
        {
            var name = ThemeAssets.Import(temp);
            Assert.Equal("vq-import-src.png", name);
            Assert.True(File.Exists(dest));
            Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(dest));
        }
        finally
        {
            File.Delete(temp);
            if (File.Exists(dest)) File.Delete(dest);
        }
    }

    [Fact]
    public void Resolve_ignores_directory_traversal_in_stored_name()
    {
        // A hand-edited settings value can't escape the Themes folder.
        Assert.Null(ThemeAssets.Resolve(@"..\..\Windows\System32\notepad.exe"));
    }
}
