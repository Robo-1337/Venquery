using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Venquery.Services;

/// <summary>A "pick a folder" dialog that works in every edition. The .NET 8 builds use WPF's own
/// <c>OpenFolderDialog</c>; the .NET Framework 4.8 Legacy build has no such class, so it drives the same
/// Windows (Vista+) common item dialog directly with FOS_PICKFOLDERS. Returns null when cancelled.</summary>
public static class FolderPicker
{
    public static string? Pick(Window? owner, string title, string? initialFolder)
    {
        var initial = !string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder) ? initialFolder : null;
#if LEGACY
        return PickNative(owner, title, initial);
#else
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (initial is not null) dlg.InitialDirectory = initial;
        return dlg.ShowDialog(owner) == true ? dlg.FolderName : null;
#endif
    }

#if LEGACY
    private static string? PickNative(Window? owner, string title, string? initial)
    {
        IFileOpenDialog? dlg = null;
        try
        {
            dlg = (IFileOpenDialog)new FileOpenDialogRcw();
            dlg.GetOptions(out var opts);
            dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            dlg.SetTitle(title);
            if (initial is not null && SHCreateItemFromParsingName(initial, IntPtr.Zero, typeof(IShellItem).GUID, out var folder) == 0)
                dlg.SetFolder(folder);
            var hwnd = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
            if (dlg.Show(hwnd) != 0) return null;          // cancelled (HRESULT_FROM_WIN32(ERROR_CANCELLED)) or failed
            dlg.GetResult(out var item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch { return null; }
        finally { if (dlg is not null) Marshal.ReleaseComObject(dlg); }
    }

    private const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRcw { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        // (remaining IFileDialog/IFileOpenDialog members are not called, so they need not be declared)
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
#endif
}
