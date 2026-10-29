using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace MicStudio.Setup;

public sealed record InstallProgress(int Percent, string Message);
public sealed record PayloadInspection(bool PayloadPresent, int Files, long UncompressedBytes, bool AppExecutableFound);

public static class InstallerEngine
{
    public static string ProductVersion => typeof(InstallerEngine).Assembly.GetName().Version?.ToString(3) ?? "0.4.0";
    private const string Product = "MicStudio.Next";
    private const string Marker = ".micstudio-install.json";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MicStudio.Next";
    private const long MaximumPayloadBytes = 1024L * 1024 * 1024;
    public static string ProgramsDirectory => Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
    public static string InstallDirectory => Path.Combine(ProgramsDirectory, "MicStudio");
    private static string StartMenuDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mic Studio Next");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Mic Studio Next.lnk");
    private static string MenuLink => Path.Combine(StartMenuDirectory, "Mic Studio.lnk");
    private static string UninstallLink => Path.Combine(StartMenuDirectory, "卸载 Mic Studio.lnk");
    private static string AppExecutable => Path.Combine(InstallDirectory, "MicStudio.exe");
    private static string SetupExecutable => Path.Combine(InstallDirectory, "MicStudio.Setup.exe");
    private sealed record InstalledFile(string RelativePath, string Sha256);
    private sealed record Ownership(string Product, int SchemaVersion, string Version, bool DesktopShortcut, DateTime InstalledUtc, List<InstalledFile>? Files = null);
    private sealed record CleanupResult(bool DirectoryRetained, string Message);

    public static bool IsInsideInstall(string path) => Path.GetFullPath(path).StartsWith(InstallDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static PayloadInspection InspectEmbeddedPayload()
    {
        using var stream = OpenPayload();
        if (stream is null) return new(false, 0, 0, false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return ValidateArchive(archive);
    }

    private static Stream? OpenPayload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("MicStudio.Setup.Payload.zip");

    public static string Install(bool desktopShortcut, IProgress<InstallProgress>? progress = null)
    {
        using var writer = AcquireInstallLock();
        EnsureSafeTarget(InstallDirectory, "MicStudio", requireOwnership: Directory.Exists(InstallDirectory));
        EnsureNotRunning();
        using var payload = OpenPayload() ?? throw new InvalidOperationException("安装器没有嵌入程序文件。请使用完整发行版安装器。");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        var inspection = ValidateArchive(archive);
        if (!inspection.AppExecutableFound) throw new InvalidDataException("安装包缺少 MicStudio.exe。");
        progress?.Report(new(3, "校验安装包与当前安装位置…"));
        Directory.CreateDirectory(ProgramsDirectory);
        var stage = Path.Combine(ProgramsDirectory, "MicStudio.stage-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(ProgramsDirectory, "MicStudio.backup-" + Guid.NewGuid().ToString("N"));
        EnsureSafeTarget(stage, "MicStudio.stage-", false);
        EnsureSafeTarget(backup, "MicStudio.backup-", false);
        if (Environment.ProcessPath is not { } setupSource || !File.Exists(setupSource))
            throw new InvalidOperationException("无法取得当前安装器的可执行文件。");
        var manifest = BuildManifest(archive, setupSource);
        Directory.CreateDirectory(stage);
        WriteOwnership(stage, desktopShortcut, manifest);
        bool committed = false;
        try
        {
            int files = 0;
            foreach (var entry in archive.Entries)
            {
                var destination = SafeEntryPath(stage, entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = entry.Open();
                using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyEntryBounded(source, target, entry.Length); target.Flush(true);
                files++;
                progress?.Report(new(5 + (int)(75.0 * files / Math.Max(1, inspection.Files)), "解压程序文件… " + files + "/" + inspection.Files));
            }
            File.Copy(setupSource, Path.Combine(stage, "MicStudio.Setup.exe"), false);
            EnsureNotRunning();
            if (Directory.Exists(InstallDirectory))
            {
                EnsureSafeTarget(InstallDirectory, "MicStudio", true);
                Directory.Move(InstallDirectory, backup);
            }
            try { Directory.Move(stage, InstallDirectory); committed = true; }
            catch { if (Directory.Exists(backup) && !Directory.Exists(InstallDirectory)) Directory.Move(backup, InstallDirectory); throw; }
            progress?.Report(new(88, "创建当前用户的程序入口…"));
            var warnings = new List<string>();
            try { RegisterUninstall(); } catch (Exception ex) { warnings.Add("卸载入口创建失败：" + ex.Message); }
            try
            {
                Directory.CreateDirectory(StartMenuDirectory);
                CreateShortcut(MenuLink, AppExecutable, "");
                CreateShortcut(UninstallLink, SetupExecutable, "--uninstall");
                if (desktopShortcut) CreateShortcut(DesktopLink, AppExecutable, "");
                else DeleteOwnedShortcut(DesktopLink, AppExecutable);
            }
            catch (Exception ex) { warnings.Add("快捷方式创建失败：" + ex.Message); }
            if (Directory.Exists(backup))
            {
                try { var cleanup = DeleteOwnedTree(backup, "MicStudio.backup-"); if (cleanup.DirectoryRetained) warnings.Add(cleanup.Message); }
                catch (Exception ex) { warnings.Add("旧版备份已保留：" + ex.Message); }
            }
            progress?.Report(new(100, "安装完成。"));
            return "安装完成。可以打开 Mic Studio，选择麦克风并主动开始测试。" + (warnings.Count == 0 ? "" : "\n" + string.Join("\n", warnings));
        }
        finally
        {
            if (!committed && Directory.Exists(stage))
            {
                try { DeleteOwnedTree(stage, "MicStudio.stage-"); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (InvalidOperationException) { }
            }
        }
    }

    public static string Uninstall(IProgress<InstallProgress>? progress = null)
    {
        using var writer = AcquireInstallLock();
        if (!Directory.Exists(InstallDirectory)) return "当前用户没有 Mic Studio 安装。个人数据未更改。";
        EnsureSafeTarget(InstallDirectory, "MicStudio", true);
        EnsureNotRunning();
        if (IsInsideInstall(Environment.ProcessPath ?? "")) throw new InvalidOperationException("卸载器仍在安装目录中运行，请从开始菜单重新打开卸载入口。");
        progress?.Report(new(15, "检查当前用户安装与运行状态…"));
        var cleanup = DeleteOwnedTree(InstallDirectory, "MicStudio");
        progress?.Report(new(75, "移除本安装器创建的程序入口…"));
        var warnings = new List<string>();
        if (cleanup.DirectoryRetained) warnings.Add(cleanup.Message);
        try
        {
            DeleteOwnedShortcut(MenuLink, AppExecutable); DeleteOwnedShortcut(UninstallLink, SetupExecutable); DeleteOwnedShortcut(DesktopLink, AppExecutable);
            if (Directory.Exists(StartMenuDirectory) && !Directory.EnumerateFileSystemEntries(StartMenuDirectory).Any()) Directory.Delete(StartMenuDirectory);
        }
        catch (Exception ex) { warnings.Add("部分快捷方式未移除：" + ex.Message); }
        try
        {
            // Only remove the per-user startup entry created by Mic Studio; another
            // product using a different Run value is left untouched.
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (run?.GetValue("MicStudio.Next") is string command &&
                command.Contains(AppExecutable, StringComparison.OrdinalIgnoreCase) &&
                command.Contains("--minimized", StringComparison.OrdinalIgnoreCase))
                run.DeleteValue("MicStudio.Next", false);
        }
        catch (Exception ex) { warnings.Add("启动项未移除：" + ex.Message); }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var own = key?.GetValue("MicStudioProduct") as string;
            key?.Close();
            if (own == Product) Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false);
        }
        catch (Exception ex) { warnings.Add("卸载登记未移除：" + ex.Message); }
        progress?.Report(new(100, "卸载完成，个人数据已保留。"));
        return "卸载完成。已保留个人设置、预设、录音、旧版程序和独立音频组件。系统 APO 配置不会自动撤销。" + (warnings.Count == 0 ? "" : "\n" + string.Join("\n", warnings));
    }

    private static void RegisterUninstall()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        if (key.GetValue("MicStudioProduct") is string product && product != Product) throw new InvalidOperationException("卸载登记已由其他产品占用。");
        key.SetValue("MicStudioProduct", Product);
        key.SetValue("DisplayName", "Mic Studio"); key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("Publisher", "Mic Studio"); key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("DisplayIcon", AppExecutable);
        key.SetValue("UninstallString", "\"" + SetupExecutable + "\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void WriteOwnership(string directory, bool desktop, List<InstalledFile> files) => File.WriteAllText(Path.Combine(directory, Marker),
        JsonSerializer.Serialize(new Ownership(Product, 2, ProductVersion, desktop, DateTime.UtcNow, files), new JsonSerializerOptions { WriteIndented = true }));

    private static List<InstalledFile> BuildManifest(ZipArchive archive, string setupSource)
    {
        var files = new List<InstalledFile>();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            using var stream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long readTotal = 0; int read;
            while ((read = stream.Read(buffer)) > 0)
            { readTotal += read; if (readTotal > entry.Length || readTotal > MaximumPayloadBytes) throw new InvalidDataException("清单校验时解压大小异常。"); hash.AppendData(buffer, 0, read); }
            if (readTotal != entry.Length) throw new InvalidDataException("清单校验时安装包不完整。");
            files.Add(new(CanonicalManifestPath(entry.FullName.Replace('\\', '/')), Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
        }
        using (var stream = File.OpenRead(setupSource)) files.Add(new("MicStudio.Setup.exe", Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()));
        ValidateManifest(files);
        return files;
    }

    private static string CanonicalManifestPath(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "MicStudio-Manifest-Validation");
        var resolved = SafeEntryPath(root, relative);
        var canonical = Path.GetRelativePath(root, resolved).Replace('\\', '/');
        if (relative != canonical || relative.EndsWith('/') || relative.Contains('\\') ||
            Path.GetFileName(resolved).Equals(Marker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装清单包含非规范路径或所有权标记。");
        return canonical;
    }

    private static void ValidateManifest(List<InstalledFile>? files)
    {
        if (files is null || files.Count is < 1 or > 10001) throw new InvalidDataException("安装清单缺失或大小异常；保留目录并停止清理。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || string.IsNullOrEmpty(file.RelativePath) || string.IsNullOrEmpty(file.Sha256) ||
                file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) || !paths.Add(CanonicalManifestPath(file.RelativePath)))
                throw new InvalidDataException("安装清单含重复路径、无效路径或摘要，已停止清理。");
        }
        foreach (var file in files)
        {
            var parent = file.RelativePath;
            while (parent.LastIndexOf('/') is var slash && slash >= 0)
            { parent = parent[..slash]; if (paths.Contains(parent)) throw new InvalidDataException("安装清单存在文件与目录路径冲突。"); }
        }
        if (!paths.Contains("MicStudio.exe") || !paths.Contains("MicStudio.Setup.exe"))
            throw new InvalidDataException("安装清单缺少主程序或安装器；保留目录并停止清理。");
    }

    private static Ownership ReadOwnership(string directory)
    {
        var marker = Path.Combine(directory, Marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4 * 1024 * 1024 || File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("安装目录缺少有效的 Mic Studio 所有权标记；不会覆盖或删除该目录。");
        try
        {
            var data = JsonSerializer.Deserialize<Ownership>(File.ReadAllText(marker));
            if (data?.Product != Product || data.SchemaVersion is < 1 or > 2) throw new InvalidOperationException("安装目录不属于此安装器，或清单版本尚不支持；保留目录。");
            if (data.SchemaVersion == 2) ValidateManifest(data.Files);
            return data;
        }
        catch (JsonException) { throw new InvalidOperationException("安装目录标记损坏；请保留文件并人工检查。"); }
    }

    private static void EnsureSafeTarget(string target, string expectedName, bool requireOwnership)
    {
        var full = Path.GetFullPath(target);
        if (!string.Equals(Path.GetDirectoryName(full), ProgramsDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安装目标不在固定的当前用户程序目录。");
        var name = Path.GetFileName(full);
        bool expected = expectedName.EndsWith('-') ? name.StartsWith(expectedName, StringComparison.Ordinal) && Guid.TryParseExact(name[expectedName.Length..], "N", out _) : name == expectedName;
        if (!expected) throw new InvalidOperationException("安装目标名称校验失败。");
        for (DirectoryInfo? directory = new(full); directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("安装路径包含链接或重解析点，已停止操作。");
        if (File.Exists(full)) throw new InvalidOperationException("安装位置已被文件占用。");
        if (requireOwnership) ReadOwnership(full);
    }

    private static CleanupResult DeleteOwnedTree(string target, string expectedName)
    {
        EnsureSafeTarget(target, expectedName, true);
        return RemoveManifestFiles(target);
    }

    private static CleanupResult RemoveManifestFiles(string target)
    {
        EnsureNoReparsePath(target);
        var ownership = ReadOwnership(target);
        if (ownership.SchemaVersion != 2 || ownership.Files is null)
            throw new InvalidOperationException("旧版安装标记没有文件清单。为保护录音及其他文件，保留整个目录，不自动清理：" + target);
        ValidateManifest(ownership.Files);
        var markerPath = Path.Combine(target, Marker);
        var markerHash = FileHash(markerPath);
        // Inspect without following links before deleting any listed file. There is no recursive delete.
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(target));
        while (pending.TryPop(out var current))
            foreach (var item in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(item);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("程序目录含有外部链接，已停止删除以保护用户文件。");
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(item);
            }
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ownership.Files)
        {
            var path = SafeEntryPath(target, file.RelativePath);
            var parent = Path.GetDirectoryName(path);
            while (parent is not null && !parent.Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            { directories.Add(parent); parent = Path.GetDirectoryName(parent); }
            if (!File.Exists(path)) continue;
            EnsureNoReparsePath(path);
            // A user may replace any installed file. Changed bytes belong to the user and are retained.
            if (FileHash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        }
        foreach (var directory in directories.OrderByDescending(p => p.Length))
            if (Directory.Exists(directory))
            { EnsureNoReparsePath(directory); if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false); }
        if (Directory.EnumerateFileSystemEntries(target).Any(p => !p.Equals(markerPath, StringComparison.OrdinalIgnoreCase)) || FileHash(markerPath) != markerHash)
            return new(true, "已保留额外或改动的文件及其目录标记：" + target);
        File.Delete(markerPath);
        Directory.Delete(Path.GetFullPath(target), false);
        return new(false, "已移除安装清单内未改动的程序文件。");
    }

    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    private static void EnsureNoReparsePath(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("安装文件为链接，停止清理。");
        for (DirectoryInfo? directory = new(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("安装路径包含链接，停止清理。");
    }

    private static PayloadInspection ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > 10000) throw new InvalidDataException("安装包文件过多。");
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int files = 0; long bytes = 0; bool app = false;
        var fakeRoot = Path.Combine(Path.GetTempPath(), "MicStudio-Payload-Validation");
        foreach (var entry in archive.Entries)
        {
            var destination = SafeEntryPath(fakeRoot, entry.FullName);
            if (!destinations.Add(destination)) throw new InvalidDataException("安装包存在重复文件名。");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("安装包不允许符号链接。");
            if (Path.GetFileName(destination).Equals(Marker, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(destination).Equals("MicStudio.Setup.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包包含保留文件名。");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            files++; bytes = checked(bytes + entry.Length);
            if (bytes > MaximumPayloadBytes) throw new InvalidDataException("安装包解压大小超过 1 GiB。");
            if (entry.FullName == "MicStudio.exe") app = true;
        }
        if (app)
        {
            using var executable = archive.GetEntry("MicStudio.exe")!.Open();
            if (executable.ReadByte() != 'M' || executable.ReadByte() != 'Z') throw new InvalidDataException("安装包中的 MicStudio.exe 不是 Windows 可执行文件。");
        }
        return new(true, files, bytes, app);
    }

    private static string SafeEntryPath(string root, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || entry.Length > 240 || Path.IsPathRooted(entry) || entry.Contains(':') || entry.Any(char.IsControl))
            throw new InvalidDataException("安装包文件路径不合法。");
        var parts = entry.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedDeviceName(p)))
            throw new InvalidDataException("安装包路径包含越界或无效名称。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(parts)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包文件路径越界。");
        return destination;
    }

    private static bool IsReservedDeviceName(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL") return true;
        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    private static void CopyEntryBounded(Stream source, Stream target, long expectedLength)
    {
        var buffer = new byte[65536]; long written = 0;
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            written = checked(written + count);
            if (written > expectedLength || written > MaximumPayloadBytes) throw new InvalidDataException("安装包的实际解压大小与声明不符。");
            target.Write(buffer, 0, count);
        }
        if (written != expectedLength) throw new InvalidDataException("安装包文件不完整。");
    }

    private static void EnsureNotRunning()
    {
        foreach (var process in Process.GetProcessesByName("MicStudio"))
        {
            using (process)
            {
                string? path;
                try { path = process.MainModule?.FileName; }
                catch { throw new InvalidOperationException("存在无法核对位置的 Mic Studio 进程，请关闭后重试。"); }
                if (path is not null && IsInsideInstall(path)) throw new InvalidOperationException("已安装的 Mic Studio 正在运行。请先退出程序（包括托盘）再重试。");
            }
        }
    }

    private static void CreateShortcut(string path, string target, string arguments)
    {
        if (File.Exists(path) && !ShortcutTargets(path, target)) throw new InvalidOperationException("已有同名快捷方式属于其他程序，已保留。");
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows 快捷方式组件不可用。"));
            dynamic dynamicShell = shell!; shortcut = dynamicShell.CreateShortcut(path); dynamic link = shortcut;
            link.TargetPath = target; link.Arguments = arguments; link.WorkingDirectory = InstallDirectory; link.Description = "Mic Studio"; link.Save();
        }
        finally { if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
    }

    private static bool ShortcutTargets(string path, string target)
    {
        object? shell = null, shortcut = null;
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return false;
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic dynamicShell = shell!; shortcut = dynamicShell.CreateShortcut(path); dynamic link = shortcut;
            string existingTarget = link.TargetPath;
            return string.Equals(Path.GetFullPath(existingTarget), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally { if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
    }

    private static void DeleteOwnedShortcut(string path, string target)
    { if (File.Exists(path) && ShortcutTargets(path, target)) File.Delete(path); }

    private static IDisposable AcquireInstallLock()
    {
        var mutex = new Mutex(false, @"Local\MicStudio.Setup." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(InstallDirectory.ToUpperInvariant()))));
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("另一个 Mic Studio 安装器正在运行。");
            return new InstallLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class InstallLease(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }

    public static object SelfTest()
    {
        var checks = new List<string>();
        string root = Path.Combine(Path.GetTempPath(), "MicStudio-Synthetic-Root");
        foreach (var bad in new[] { "../outside.exe", "a/../../outside.exe", "C:/outside.exe", "/outside.exe", "a:stream", "foo. /bar", "folder/./x", "folder/../x", "x\n.exe", "NUL", "CON.txt", "COM1.dll", "sub/LPT³.log" })
        {
            try { SafeEntryPath(root, bad); throw new Exception("Unsafe path was accepted: " + bad); }
            catch (InvalidDataException) { checks.Add("Rejected unsafe path: " + bad.Replace("\n", "\\n")); }
        }
        if (SafeEntryPath(root, "sub/file.dll") != Path.Combine(root, "sub", "file.dll")) throw new Exception("Normal path failed.");
        checks.Add("Normal nested DLL path accepted");
        foreach (var name in new[] { "MicStudio", "MicStudio.backup-" + Guid.NewGuid().ToString("N"), "MicStudio.stage-" + Guid.NewGuid().ToString("N") })
        {
            EnsureSafeTarget(Path.Combine(ProgramsDirectory, name), name.StartsWith("MicStudio.backup-") ? "MicStudio.backup-" : name.StartsWith("MicStudio.stage-") ? "MicStudio.stage-" : "MicStudio", false);
            checks.Add("Fixed target verified: " + name.Split('-')[0]);
        }
        try { EnsureSafeTarget(Path.GetTempPath(), "MicStudio", false); throw new Exception("Unexpected deletion target accepted."); }
        catch (InvalidOperationException) { checks.Add("Unrelated target refused"); }
        void ArchiveCheck(string name, Action<ZipArchive> create, bool shouldReject)
        {
            using var bytes = new MemoryStream();
            using (var writing = new ZipArchive(bytes, ZipArchiveMode.Create, true)) create(writing);
            bytes.Position = 0;
            using var reading = new ZipArchive(bytes, ZipArchiveMode.Read, true);
            try
            {
                ValidateArchive(reading);
                if (shouldReject) throw new Exception("Unsafe archive accepted: " + name);
            }
            catch (InvalidDataException) { if (!shouldReject) throw; }
            checks.Add(name);
        }
        static void Entry(ZipArchive archive, string name, string value)
        { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(value); }
        ArchiveCheck("Valid nested payload accepted", a => { Entry(a, "MicStudio.exe", "MZtest"); Entry(a, "sub/a.dll", "test"); }, false);
        ArchiveCheck("ZIP traversal rejected", a => Entry(a, "../outside.txt", "test"), true);
        ArchiveCheck("Case-folded ZIP duplicate rejected", a => { Entry(a, "file.txt", "1"); Entry(a, "FILE.TXT", "2"); }, true);
        ArchiveCheck("ZIP symlink rejected", a => { var e = a.CreateEntry("symlink"); e.ExternalAttributes = unchecked((int)0xA1FF0000); }, true);
        ArchiveCheck("Reserved ownership marker rejected", a => Entry(a, Marker, "fake"), true);
        ArchiveCheck("Non-PE application rejected", a => Entry(a, "MicStudio.exe", "not executable"), true);
        try { using var source = new MemoryStream([1, 2]); using var target = new MemoryStream(); CopyEntryBounded(source, target, 1); throw new Exception("Oversized stream accepted."); }
        catch (InvalidDataException) { checks.Add("Actual decompression size mismatch rejected"); }
        RunManifestChecks(checks);
        return new { passed = checks.Count, checks, changedSystemFiles = false, note = "Synthetic ZIP/path/manifest cleanup checks use temporary fixtures only; installation, upgrade, shortcuts and uninstall still require manual validation." };
    }

    private static void RunManifestChecks(List<string> checks)
    {
        const string dummyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        List<InstalledFile> Basic() => [new("MicStudio.exe", dummyHash), new("MicStudio.Setup.exe", dummyHash)];
        foreach (var invalid in new[] { "../recording.wav", "sub/../../recording.wav", Marker, "sub/" + Marker, "sub//file.dll", "sub\\file.dll" })
        {
            var list = Basic(); list.Add(new(invalid, dummyHash));
            try { ValidateManifest(list); throw new Exception("Unsafe manifest accepted: " + invalid); }
            catch (InvalidDataException) { checks.Add("Manifest rejects unsafe path: " + invalid); }
        }
        {
            var list = Basic(); list.Add(new("MICSTUDIO.EXE", dummyHash));
            try { ValidateManifest(list); throw new Exception("Duplicate manifest accepted."); }
            catch (InvalidDataException) { checks.Add("Manifest rejects case-folded duplicates"); }
        }
        {
            var list = Basic(); list.Add(new("sub", dummyHash)); list.Add(new("sub/file.dll", dummyHash));
            try { ValidateManifest(list); throw new Exception("Manifest file/directory collision accepted."); }
            catch (InvalidDataException) { checks.Add("Manifest rejects file/directory aliases"); }
        }
        {
            var list = Basic(); list[0] = new("MicStudio.exe", "not-a-hash");
            try { ValidateManifest(list); throw new Exception("Invalid manifest hash accepted."); }
            catch (InvalidDataException) { checks.Add("Manifest rejects invalid file hashes"); }
        }
        var fixture = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MicStudio-ManifestTests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(fixture);
        (string Directory, List<InstalledFile> Files) NewOwnedFixture()
        {
            var directory = Path.Combine(fixture, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var paths = new[] { "MicStudio.exe", "MicStudio.Setup.exe", "sub/runtime.dll" };
            var files = new List<InstalledFile>();
            foreach (var relative in paths)
            {
                var file = SafeEntryPath(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "synthetic-installed-" + relative);
                files.Add(new(relative, FileHash(file)));
            }
            WriteOwnership(directory, false, files);
            return (directory, files);
        }
        try
        {
            {
                var owned = NewOwnedFixture();
                var result = RemoveManifestFiles(owned.Directory);
                if (result.DirectoryRetained || Directory.Exists(owned.Directory)) throw new Exception("Clean manifest-owned fixture was retained.");
                checks.Add("Manifest cleanup removes only unchanged installed files and empty owned directories");
            }
            {
                var owned = NewOwnedFixture();
                var recordings = Path.Combine(owned.Directory, "recordings"); Directory.CreateDirectory(recordings);
                var recording = Path.Combine(recordings, "personal-take.wav"); File.WriteAllText(recording, "synthetic-private-audio");
                File.WriteAllText(Path.Combine(owned.Directory, "sub", "runtime.dll"), "synthetic-user-replacement");
                var empty = Path.Combine(owned.Directory, "personal-empty-directory"); Directory.CreateDirectory(empty);
                var result = RemoveManifestFiles(owned.Directory);
                if (!result.DirectoryRetained || File.ReadAllText(recording) != "synthetic-private-audio" ||
                    File.ReadAllText(Path.Combine(owned.Directory, "sub", "runtime.dll")) != "synthetic-user-replacement" ||
                    !File.Exists(Path.Combine(owned.Directory, Marker)) || !Directory.Exists(empty) || File.Exists(Path.Combine(owned.Directory, "MicStudio.exe")))
                    throw new Exception("Cleanup lost unknown/changed data or kept the original application.");
                checks.Add("Unknown recordings, modified installed files, private directories and marker are preserved");
                RemoveManifestFiles(owned.Directory);
                if (!File.Exists(recording)) throw new Exception("Repeated cleanup lost a retained recording.");
                checks.Add("Repeated cleanup still preserves retained user files");
            }
            {
                var owned = NewOwnedFixture();
                File.WriteAllText(Path.Combine(owned.Directory, Marker), JsonSerializer.Serialize(new Ownership(Product, 1, "old", false, DateTime.UtcNow)));
                try { RemoveManifestFiles(owned.Directory); throw new Exception("Legacy manifest-free install was deleted."); }
                catch (InvalidOperationException) { }
                if (!File.Exists(Path.Combine(owned.Directory, "MicStudio.exe"))) throw new Exception("Legacy install was modified.");
                checks.Add("Legacy marker without manifest refuses cleanup and preserves entire directory");
            }
            {
                var owned = NewOwnedFixture();
                File.WriteAllText(Path.Combine(owned.Directory, Marker), JsonSerializer.Serialize(new Ownership(Product, 99, "future", false, DateTime.UtcNow, owned.Files)));
                try { RemoveManifestFiles(owned.Directory); throw new Exception("Future manifest was accepted."); }
                catch (InvalidOperationException) { }
                if (!File.Exists(Path.Combine(owned.Directory, "MicStudio.exe"))) throw new Exception("Future-schema install was modified.");
                checks.Add("Future manifest schema is refused before deleting files");
            }
            {
                var owned = NewOwnedFixture(); var malicious = owned.Files.ToList(); malicious.Add(new("../outside.wav", dummyHash));
                File.WriteAllText(Path.Combine(owned.Directory, Marker), JsonSerializer.Serialize(new Ownership(Product, 2, "bad", false, DateTime.UtcNow, malicious)));
                try { RemoveManifestFiles(owned.Directory); throw new Exception("Tampered manifest was accepted."); }
                catch (InvalidDataException) { }
                if (!File.Exists(Path.Combine(owned.Directory, "MicStudio.exe"))) throw new Exception("Validation was not completed before deletion.");
                checks.Add("Whole manifest is validated before any file is deleted");
            }
        }
        finally
        {
            // The only wholesale cleanup is for freshly-created synthetic fixtures, never a real install path.
            if (Path.GetDirectoryName(fixture)?.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true &&
                Path.GetFileName(fixture).StartsWith("MicStudio-ManifestTests-", StringComparison.Ordinal))
            {
                var directories = new List<string>(); var pending = new Stack<string>(); pending.Push(fixture);
                while (pending.TryPop(out var directory))
                {
                    EnsureNoReparsePath(directory); directories.Add(directory);
                    foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                    { EnsureNoReparsePath(item); if (Directory.Exists(item)) pending.Push(item); else File.Delete(item); }
                }
                foreach (var directory in directories.OrderByDescending(p => p.Length)) Directory.Delete(directory, false);
            }
        }
    }
}
