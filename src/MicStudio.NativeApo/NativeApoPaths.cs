using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MicStudio.NativeApo;

public static class NativeApoPaths
{
    public static string DefaultParameterRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MicStudio", "APO", "Parameters");
    public static string DefaultInstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "MicStudio", "APO");
    public static string DefaultReportRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MicStudio", "APO", "Reports");
    public static string OperationReport(Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("An explicit operation request GUID is required.", nameof(requestId));
        var root = Path.GetFullPath(DefaultReportRoot); RejectReparseParents(root);
        return Path.Combine(root, requestId.ToString("D") + ".json");
    }
    public static string ParameterFile(Guid endpoint, string? root = null)
    {
        if (endpoint == Guid.Empty) throw new ArgumentException("An explicit endpoint GUID is required.", nameof(endpoint));
        var directory = Path.GetFullPath(root ?? DefaultParameterRoot);
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("A local absolute directory is required.");
        RejectReparseParents(directory);
        var path = Path.GetFullPath(Path.Combine(directory, endpoint.ToString("D") + ".bin"));
        if (!path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid parameter path.");
        return path;
    }

    public static void RejectReparseParents(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("APO files cannot use a symbolic link or junction: " + current);
            current = Path.GetDirectoryName(current);
        }
    }

    internal static FileStream OpenParameterFile(string path)
        => OpenExistingFileNoReparse(path, writable: true);

    internal static FileStream OpenExistingFileNoReparse(string path, bool writable)
    {
        RejectReparseParents(path);
        var access = writable ? 0x80000000u | 0x40000000u : 0x80000000u;
        // OPEN_REPARSE_POINT prevents following a last-component link introduced
        // between the metadata check and opening. Parents are provisioned admin-only.
        var handle = CreateFileW(path, access, writable ? 3u : 1u, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException(writable ? "APO parameter file is not provisioned or writable." : "APO operation report is unavailable or not readable.", new System.ComponentModel.Win32Exception(error)); }
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0)
        { handle.Dispose(); throw new IOException("APO parameter file is a reparse point or cannot be inspected."); }
        return new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
    [StructLayout(LayoutKind.Sequential)] private struct ByHandleFileInformation
    { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
}
