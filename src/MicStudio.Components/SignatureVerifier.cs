using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MicStudio.Components;

internal static class SignatureVerifier
{
    internal sealed record Result(bool Valid, string Signer, string StatusCode);
    public static Result Verify(string path, bool requireVendor)
    {
        string signer = "未知";
        IntPtr filePointer = IntPtr.Zero;
        try
        {
            var file = new WintrustFileInfo { Size = (uint)Marshal.SizeOf<WintrustFileInfo>(), FilePath = path };
            filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>()); Marshal.StructureToPtr(file, filePointer, false);
            var data = new WintrustData { Size = (uint)Marshal.SizeOf<WintrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = filePointer, StateAction = 0, ProviderFlags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            int code = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
#pragma warning disable SYSLIB0057 // Authenticode signer metadata; Windows WinVerifyTrust above decides trust, not this certificate constructor.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            signer = certificate.GetNameInfo(X509NameType.SimpleName, false);
            string[] knownPublishers = ["BUREL VINCENT", "BUREL VINCENT Entrepreneur individuel", "Vincent Burel", "VB-Audio Software"];
            bool vendor = knownPublishers.Contains(signer, StringComparer.OrdinalIgnoreCase);
            return new(code == 0 && (!requireVendor || vendor), signer, code == 0 && requireVendor && !vendor ? "发行者不是已适配的 VB-Audio/Vincent Burel" : "0x" + unchecked((uint)code).ToString("X8"));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException or System.IO.IOException)
        { return new(false, signer, ex.GetType().Name); }
        finally { if (filePointer != IntPtr.Zero) { Marshal.DestroyStructure<WintrustFileInfo>(filePointer); Marshal.FreeHGlobal(filePointer); } }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string FilePath; public IntPtr FileHandle; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustData
    {
        public uint Size; public IntPtr PolicyCallback; public IntPtr SipClient; public uint UiChoice; public uint RevocationChecks; public uint UnionChoice;
        public IntPtr FileInfo; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);
}
