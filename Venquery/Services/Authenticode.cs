using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Venquery.Services;

/// <summary>
/// Verifies a downloaded file's Authenticode signature, proving it came intact from
/// the vendor. Uses the OS trust engine (WinVerifyTrust), the same check Windows runs
/// before a signed installer executes. Read-only; never runs the file.
/// </summary>
public static class Authenticode
{
    public sealed record Result(bool Valid, string Signer, bool HasSignature);

    public static Result Verify(string path)
    {
        var signer = "";
        var hasSig = false;
        try
        {
            // Extracts the embedded signer certificate (throws if the file is unsigned).
            // Dispose the intermediate X509Certificate too, this runs in a tight loop
            // over every .exe during a portable-app scan, so a leaked native handle per
            // file adds up.
            using var raw = X509Certificate.CreateFromSignedFile(path);
            using var cert = new X509Certificate2(raw);
            signer = cert.GetNameInfo(X509NameType.SimpleName, false);
            hasSig = true;
        }
        catch { /* unsigned or unreadable */ }

        var valid = hasSig && WinVerifyTrustFile(path);
        return new Result(valid, signer, hasSig);
    }

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static bool WinVerifyTrustFile(string path)
    {
        var file = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };
        var pFile = Marshal.AllocHGlobal((int)file.cbStruct);
        Marshal.StructureToPtr(file, pFile, false);

        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = 2,            // WTD_UI_NONE
            fdwRevocationChecks = 0,   // WTD_REVOKE_NONE (no network stall)
            dwUnionChoice = 1,         // WTD_CHOICE_FILE
            pFile = pFile,
            dwStateAction = 0,         // WTD_STATEACTION_IGNORE (one-shot, self-cleans)
        };
        var pData = Marshal.AllocHGlobal((int)data.cbStruct);
        Marshal.StructureToPtr(data, pData, false);

        try
        {
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            return WinVerifyTrust(IntPtr.Zero, ref action, pData) == 0; // 0 == trusted
        }
        catch { return false; }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_DATA>(pData);
            Marshal.FreeHGlobal(pData);
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    // Pin the DLL search to System32 (wintrust.dll is a system library) so a planted
    // wintrust.dll next to the exe can't hijack the trust check, important for a tool
    // whose whole job is verifying signatures.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid action, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;          // union member for WTD_CHOICE_FILE
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }
}
