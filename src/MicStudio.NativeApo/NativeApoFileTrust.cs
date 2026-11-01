using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MicStudio.NativeApo;

public sealed record NativeApoFileIdentity(bool Present, bool NativeX64, bool Trusted, string Sha256, string Message, bool ContractExport = false);

public static class NativeApoFileTrust
{
    public static NativeApoFileIdentity Inspect(string path)
    {
        if (!File.Exists(path)) return new(false, false, false, "", "Native APO DLL is missing.");
        try
        {
            NativeApoPaths.RejectReparseParents(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 256 or > 32 * 1024 * 1024) return new(true, false, false, "", "Native APO DLL size is invalid.");
            var bytes = new byte[file.Length]; file.ReadExactly(bytes);
            var native = IsNativeX64Dll(bytes);
            var exports = native && HasContractExports(bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var trusted = exports && VerifySignature(Path.GetFullPath(path));
            return new(true, native, trusted, hash, !native ? "This file is not a native Windows x64 DLL." : !exports ? "The DLL is missing the Mic Studio APO contract/COM exports." : trusted ? "Windows signature trust passed; component contract requires explicit preview validation." : "Native DLL is unsigned or Windows signature trust did not pass; system installation is blocked.", exports);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(true, false, false, "", "Cannot inspect native APO DLL: " + ex.Message); }
    }

    public static bool IsNativeX64Dll(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 256 || bytes[0] != 'M' || bytes[1] != 'Z') return false;
        var pe = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(60, 4));
        if (pe < 64 || pe > bytes.Length - 264) return false;
        var header = bytes[pe..];
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x00004550 || U16(header, 4) != 0x8664 || U16(header, 20) < 240 || (U16(header, 22) & 0x2000) == 0 || U16(header, 24) != 0x20b) return false;
        // A CLR directory identifies a managed assembly, not the native APO.
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24 + 112 + 14 * 8, 4)) == 0;
    }
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));

    public static bool HasContractExports(ReadOnlySpan<byte> image)
    {
        if (!IsNativeX64Dll(image)) return false;
        var bytes = image.ToArray();
        uint U(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        var pe = (int)U(60); var sections = U16(bytes, pe + 6); var optionalSize = U16(bytes, pe + 20);
        var table = pe + 24 + optionalSize;
        if (sections is 0 or > 96 || table > bytes.Length - sections * 40) return false;
        int Offset(uint rva, int length)
        {
            if (length < 0) return -1;
            for (var index = 0; index < sections; index++)
            {
                var section = table + index * 40; var address = U(section + 12); var rawSize = U(section + 16); var rawOffset = U(section + 20);
                if (rva < address || (ulong)rva + (uint)length > (ulong)address + rawSize) continue;
                var offset = (ulong)rawOffset + rva - address;
                if (offset + (uint)length <= (ulong)bytes.Length) return (int)offset;
            }
            return -1;
        }
        var exports = Offset(U(pe + 24 + 112), 40); if (exports < 0) return false;
        var count = U(exports + 24); if (count is 0 or > 256) return false;
        var names = Offset(U(exports + 32), checked((int)count * 4)); if (names < 0) return false;
        var required = new HashSet<string>(["DllGetClassObject", "DllCanUnloadNow", "MicStudioApoGetContract"], StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var name = Offset(U(names + index * 4), 1); if (name < 0) return false;
            var end = Array.IndexOf(bytes, (byte)0, name, Math.Min(128, bytes.Length - name)); if (end < 0) return false;
            required.Remove(System.Text.Encoding.ASCII.GetString(bytes, name, end - name));
        }
        return required.Count == 0;
    }

    /// <summary>Explicit preview only: unsigned or unrelated DLLs are never loaded.</summary>
    public static bool VerifyContract(string path, string expectedSha256, out string message)
    {
        message = "Native component contract could not be verified.";
        try
        {
            NativeApoPaths.RejectReparseParents(path);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var identity = Inspect(path);
            if (!identity.Trusted || !identity.ContractExport || identity.Sha256 != expectedSha256) { message = identity.Message; return false; }
            var module = LoadLibraryExW(Path.GetFullPath(path), IntPtr.Zero, 0x800); // Dependencies only from System32.
            if (module == IntPtr.Zero) { message = "The trusted APO DLL could not be loaded for contract inspection."; return false; }
            try
            {
                var address = GetProcAddress(module, "MicStudioApoGetContract");
                if (address == IntPtr.Zero) { message = "The native APO contract export is missing."; return false; }
                var method = Marshal.GetDelegateForFunctionPointer<Contract>(address);
                var result = method(out var version, out var size, out var classId);
                if (result != 0 || version != NativeApoParameterCodec.Version || size != NativeApoParameterCodec.Size || classId != NativeApoDeploymentService.ClassId)
                { message = "The DLL is not the expected Mic Studio APO ABI/CLSID."; return false; }
                message = "Native APO contract verified: ABI 2 / 224 bytes / expected CLSID."; return true;
            }
            finally { FreeLibrary(module); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or BadImageFormatException)
        { message = "Native APO contract inspection failed: " + ex.Message; return false; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Contract(out uint version, out uint size, out Guid classId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);

    private static bool VerifySignature(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer, StateAction = 1, ProviderFlags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result == 0;
        }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile
    { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    { public uint Size; public IntPtr Policy, Sip; public uint UiChoice, Revocation, UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData, Url; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings; }
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
