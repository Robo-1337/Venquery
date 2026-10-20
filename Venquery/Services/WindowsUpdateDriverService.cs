using System.Reflection;
using System.Runtime.InteropServices;

namespace Venquery.Services;

/// <summary>A driver update Windows Update offers for this PC (not installed yet).</summary>
public sealed record WuDriver(string Title, string Manufacturer, string Model, string DriverClass, string HardwareId,
    DateTime? Date, long SizeBytes);

/// <summary>
/// Asks Windows Update, through its own built-in API (the "Microsoft.Update.Session" COM object, the same engine
/// Settings > Windows Update uses), which driver updates it has for this PC. READ-ONLY: it searches, it never
/// downloads or installs; the user installs from Windows' own Optional updates page if they want one.
/// Venquery opens no network connection itself; Windows Update does its usual check. If the PC is managed by
/// WSUS/Intune the search goes wherever Windows Update is configured to go.
/// </summary>
public static class WindowsUpdateDriverService
{
    public static Task<(IReadOnlyList<WuDriver> Drivers, string Error)> SearchAsync()
        => Task.Run(Search);

    private static (IReadOnlyList<WuDriver>, string) Search()
    {
        object? session = null;
        try
        {
            var t = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (t is null) return (Array.Empty<WuDriver>(), "Windows Update isn't available on this PC.");
            session = Activator.CreateInstance(t)!;
            try { Set(session, "ClientApplicationID", "Venquery"); } catch { }
            var searcher = Call(session, "CreateUpdateSearcher")!;
            var result = Call(searcher, "Search", "IsInstalled=0 and Type='Driver' and IsHidden=0")!;
            var updates = Get(result, "Updates")!;
            var count = Convert.ToInt32(Get(updates, "Count"));
            var list = new List<WuDriver>();
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var u = updates.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, updates, new object[] { i })!;
                    DateTime? date = null;
                    try { if (Get(u, "DriverVerDate") is DateTime d && d.Year > 1990) date = d; } catch { }
                    long size = 0;
                    try { size = Convert.ToInt64(Get(u, "MaxDownloadSize")); } catch { }
                    list.Add(new WuDriver(
                        Str(u, "Title"), Str(u, "DriverManufacturer"), Str(u, "DriverModel"),
                        Str(u, "DriverClass"), Str(u, "DriverHardwareID"), date, size));
                }
                catch { /* one unreadable entry: skip it */ }
            }
            return (list.OrderBy(d => d.DriverClass).ThenBy(d => d.Title).ToList(), "");
        }
        catch (Exception ex)
        {
            return (Array.Empty<WuDriver>(), Friendly(ex));
        }
        finally
        {
            try { if (session is not null && Marshal.IsComObject(session)) Marshal.ReleaseComObject(session); } catch { }
        }
    }

    /// <summary>Plain-language versions of the common Windows Update error codes.</summary>
    internal static string Friendly(Exception ex)
    {
        var e = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        var hr = (uint)e.HResult;
        return hr switch
        {
            0x8024402C or 0x80072EE7 or 0x80072EFD or 0x80072EFE or 0x8024401C or 0x80244022
                => "Windows Update couldn't be reached. Check the internet connection and try again.",
            0x8024002E => "Windows Update is turned off on this PC by a policy (common on managed or LTSC machines).",
            0x80240438 or 0x8024500C => "This PC is set not to use Windows Update for drivers (a policy or a WSUS setting).",
            0x80070422 => "The Windows Update service is disabled. Turn it back on to check for drivers.",
            0x8024001E => "The check was interrupted. Try again.",
            _ => $"Windows Update reported a problem (0x{hr:X8}): {e.Message}",
        };
    }

    /// <summary>Opens Windows' own Optional updates page (where driver updates are installed). Fixed target.</summary>
    public static void OpenWindowsUpdate()
    {
        try
        {
#if LEGACY
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SystemTools.Exe("control.exe"), "/name Microsoft.WindowsUpdate") { UseShellExecute = true });
#else
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:windowsupdate-optionalupdates") { UseShellExecute = true });
#endif
        }
        catch
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true }); } catch { }
        }
    }

    private static object? Call(object o, string name, params object[] args) => o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
    private static object? Get(object o, string name) => o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
    private static void Set(object o, string name, object value) => o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value });
    private static string Str(object o, string name) { try { return Get(o, name) as string ?? ""; } catch { return ""; } }
}
