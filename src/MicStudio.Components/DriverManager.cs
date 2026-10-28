using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MicStudio.Components;

public sealed record DriverStatus(bool ServicePresent, string DriverVersion, string SignatureStatus, bool InputEndpointPresent, bool OutputEndpointPresent, string Message);
public sealed record DriverPackage(string FilePath, int PackNumber, string Sha256, string Source);
public sealed record DriverUpdateInfo(int AvailablePackNumber, string DownloadUrl, int? LocalPackagePackNumber, bool PackageUpdateAvailable, string Message);
public sealed record DriverInstallerInfo(string ExecutablePath, bool SignatureValid, string Signer, string Message);
public sealed record DriverInstallResult(bool Success, bool Cancelled, string Message);
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes, int? Percent);

/// <summary>Explicit user actions only. Never installs, elevates or restarts as a side effect of detecting/checking/downloading.</summary>
public sealed partial class DriverManager : IDisposable
{
    public const string OfficialPage = "https://vb-audio.com/Cable/";
    public const string AttributionUrl = "https://vb-cable.com";
    public const string DonationUrl = "https://vb-audio.com/Services/licensing.htm";
    private const long MaximumZipBytes = 20 * 1024 * 1024;
    private const long MaximumExtractedBytes = 100 * 1024 * 1024;
    private readonly string bundledDirectory;
    private readonly string cacheRoot;
    private readonly HttpClient http;
    private readonly SemaphoreSlim downloadLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private int disposed;
    public event Action<DownloadProgress>? DownloadProgressChanged;

    public DriverManager(string? bundledComponentsDirectory = null)
    {
        bundledDirectory = Path.GetFullPath(bundledComponentsDirectory ?? Path.Combine(AppContext.BaseDirectory, "components"));
        cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicStudio.Next", "Components");
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        lifetimeToken = lifetime.Token;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MicStudio/0.1.0 (+https://github.com/haiyang172/Mic-studio)");
    }

    public DriverStatus Detect(bool cableInputPresent, bool cableOutputPresent)
    {
        bool servicePresent = false; string version = "未知", signature = "未检查";
        var details = new List<string>();
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var name in services?.GetSubKeyNames().Where(n => n.StartsWith("vbaudio_cable", StringComparison.OrdinalIgnoreCase) || n.Equals("VBAudioVACMME", StringComparison.OrdinalIgnoreCase) || n.Equals("VBAudioVACWDM", StringComparison.OrdinalIgnoreCase)) ?? [])
            {
                servicePresent = true;
                using var service = services!.OpenSubKey(name);
                var image = service?.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(image)) continue;
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var path = Environment.ExpandEnvironmentVariables(image.Trim('"'));
                if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(windows, path[12..]);
                else if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
                else if (!Path.IsPathRooted(path)) path = Path.Combine(windows, path);
                if (!File.Exists(path)) { details.Add("驱动服务存在，但对应文件缺失。"); continue; }
                version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "未知";
                var trust = SignatureVerifier.Verify(path, requireVendor: false);
                signature = trust.Valid ? "Windows 信任检查通过（驱动文件）" : "驱动文件信任状态未知：" + trust.StatusCode;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { details.Add("驱动注册检测受限：" + ex.GetType().Name); }
        string message = cableInputPresent && cableOutputPresent ? "已发现 CABLE Input / Output 两个端点；实际线路仍需测试。" : "VB-CABLE 端点不完整；安装后可能需要重启或刷新。";
        message += " 安装包编号不能与驱动文件版本直接比较，不据此声称驱动已是最新。";
        return new(servicePresent, version, signature, cableInputPresent, cableOutputPresent, message + " " + string.Join(" ", details));
    }

    public DriverPackage? GetLocalPackage()
    {
        var packages = new List<DriverPackage>();
        foreach (var directory in new[] { bundledDirectory, Path.Combine(cacheRoot, "Downloads") })
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                EnsureNoLinks(directory);
                foreach (var file in Directory.EnumerateFiles(directory, "VBCABLE_Driver_Pack*.zip", SearchOption.TopDirectoryOnly))
                    try { packages.Add(InspectPackage(file)); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return packages.OrderByDescending(p => p.PackNumber).ThenByDescending(p => p.Source == "本地下载缓存").FirstOrDefault();
    }

    public async Task<DriverUpdateInfo> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        cancellationToken = operation.Token;
        using var response = await GetOfficialAsync(new Uri(OfficialPage), false, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > 2 * 1024 * 1024) throw new InvalidDataException("官方页面大小异常。");
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new MemoryStream();
        await CopyBoundedAsync(content, limited, 2 * 1024 * 1024, null, cancellationToken).ConfigureAwait(false);
        var available = ParseOfficialDownload(System.Text.Encoding.UTF8.GetString(limited.ToArray()));
        var local = GetLocalPackage();
        bool newer = local is null || available.Pack > local.PackNumber;
        return new(available.Pack, available.Uri.AbsoluteUri, local?.PackNumber, newer,
            newer ? "官方网站提供较新的安装包；下载完成后仍需主动打开安装器并确认 UAC。" : "本地安装包与官网公布的包编号一致或更新；这不证明已安装驱动的版本。可重新下载以刷新同编号包。");
    }

    public async Task<DriverPackage> DownloadLatestAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        cancellationToken = operation.Token;
        await downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var update = await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            var downloads = Path.Combine(cacheRoot, "Downloads"); EnsureNoLinks(downloads); Directory.CreateDirectory(downloads);
            var destination = Path.Combine(downloads, "VBCABLE_Driver_Pack" + update.AvailablePackNumber + ".zip");
            if (!force && File.Exists(destination)) return InspectPackage(destination);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using var response = await GetOfficialAsync(new Uri(update.DownloadUrl), true, cancellationToken).ConfigureAwait(false);
                var total = response.Content.Headers.ContentLength;
                if (total is > MaximumZipBytes) throw new InvalidDataException("驱动下载超过 20 MiB 上限。");
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await CopyBoundedAsync(source, target, MaximumZipBytes, count => DownloadProgressChanged?.Invoke(new(count, total, total is > 0 ? (int)(count * 100 / total.Value) : null)), cancellationToken).ConfigureAwait(false);
                ValidatePackageArchive(temporary);
                EnsureNoLinks(destination);
                if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
                return InspectPackage(destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { downloadLock.Release(); }
    }

    public DriverPackage InspectPackage(string path)
    {
        path = Path.GetFullPath(path); EnsureNoLinks(path);
        var match = PackageFileName().Match(Path.GetFileName(path));
        if (!match.Success || !int.TryParse(match.Groups["pack"].Value, out var pack) || pack is < 1 or > 100000)
            throw new ArgumentException("仅接受标准 VB-CABLE 官方安装包名称。");
        ValidatePackageArchive(path);
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var source = path.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "本地下载缓存" :
            path.StartsWith(bundledDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "随软件捆绑的官方包" : "本地文件（来源待核对）";
        return new(path, pack, hash, source);
    }

    public DriverInstallerInfo PrepareInstaller(DriverPackage package)
    {
        var current = InspectPackage(package.FilePath);
        if (!string.Equals(current.Sha256, package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("驱动安装包在检查后发生变化，请重新选择。");
        var extractionRoot = Path.Combine(cacheRoot, "Extracted"); EnsureNoLinks(extractionRoot); Directory.CreateDirectory(extractionRoot);
        var target = Path.Combine(extractionRoot, current.Sha256); EnsureNoLinks(target);
        var installer = Path.Combine(target, "VBCABLE_Setup_x64.exe");
        if (!Directory.Exists(target))
        {
            var stage = Path.Combine(extractionRoot, current.Sha256 + ".stage-" + Guid.NewGuid().ToString("N"));
            EnsureNoLinks(stage); Directory.CreateDirectory(stage);
            try
            {
                using var archive = ZipFile.OpenRead(current.FilePath);
                foreach (var entry in archive.Entries)
                {
                    var output = SafeEntryPath(stage, entry.FullName);
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(output); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using var source = entry.Open(); using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    CopyBounded(source, destination, entry.Length);
                }
                // No directory overwrite: existing hash directory is verified against the archive below.
                try { Directory.Move(stage, target); }
                catch (IOException) when (Directory.Exists(target)) { DeleteStage(stage, extractionRoot, current.Sha256); }
            }
            catch { if (Directory.Exists(stage)) DeleteStage(stage, extractionRoot, current.Sha256); throw; }
        }
        if (InspectPackage(current.FilePath).Sha256 != current.Sha256) throw new InvalidDataException("解压期间安装包发生变化，已禁止启动。");
        VerifyExtractedFiles(current, target);
        var trust = SignatureVerifier.Verify(installer, true);
        return new(installer, trust.Valid, trust.Signer, trust.Valid ? "VB-Audio 安装器签名与 Windows 信任检查通过。打开后由官方界面选择安装/更新，可能需要重启。" : "未通过安装器信任检查，已禁止启动：" + trust.StatusCode);
    }

    public DriverInstallResult LaunchInstaller(DriverPackage package)
    {
        try
        {
            var installer = PrepareInstaller(package);
            if (!installer.SignatureValid) return new(false, false, installer.Message);
            // Prepare verifies every companion byte. Recheck the exact signed executable immediately before launch.
            var trust = SignatureVerifier.Verify(installer.ExecutablePath, true);
            if (!trust.Valid) return new(false, false, "启动前签名验证失败，未请求提权。");
            Process.Start(new ProcessStartInfo(installer.ExecutablePath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(installer.ExecutablePath)! });
            return new(true, false, "已打开 VB-Audio 官方驱动安装器；安装状态尚未确认。请按其提示操作，必要时重启后刷新设备。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return new(false, true, "已取消 UAC；驱动和当前设置未由 Mic Studio 更改。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException)
        { return new(false, false, "未打开驱动安装器：" + ex.Message); }
    }

    private async Task<HttpResponseMessage> GetOfficialAsync(Uri uri, bool download, CancellationToken ct)
    {
        for (int redirect = 0; redirect <= 4; redirect++)
        {
            ValidateOfficialUri(uri, download);
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new HttpRequestException("官方响应缺少重定向目标。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("官方站点重定向过多。");
    }

    public static (int Pack, Uri Uri) ParseOfficialDownload(string html)
    {
        var links = OfficialLink().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups["url"].Value))
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null).Where(u => u is not null)
            .Select(u => { ValidateOfficialUri(u!, true); var m = PackageFileName().Match(Path.GetFileName(u!.AbsolutePath)); return (Pack: int.Parse(m.Groups["pack"].Value, System.Globalization.CultureInfo.InvariantCulture), Uri: u); })
            .Where(x => x.Pack is >= 1 and <= 100000).OrderByDescending(x => x.Pack).ToList();
        return links.Count == 0 ? throw new InvalidDataException("官网未提供可识别的标准 VB-CABLE HTTPS 下载链接；不猜测下载地址。") : links[0]!;
    }

    public static void ValidateOfficialUri(Uri uri, bool download)
    {
        if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("仅允许官方 HTTPS 地址。");
        bool allowed = download ? uri.Host.Equals("download.vb-audio.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith("/Download_CABLE/", StringComparison.Ordinal) && PackageFileName().IsMatch(uri.AbsolutePath["/Download_CABLE/".Length..])
            : (uri.Host.Equals("vb-audio.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.vb-audio.com", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.StartsWith("/Cable/", StringComparison.Ordinal);
        if (!allowed) throw new InvalidDataException("下载或重定向目标不在固定官方站点白名单。");
    }

    [GeneratedRegex(@"^VBCABLE_Driver_Pack(?<pack>[0-9]{1,6})\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageFileName();
    [GeneratedRegex("(?i)href\\s*=\\s*[\"'](?<url>https://download\\.vb-audio\\.com/Download_CABLE/VBCABLE_Driver_Pack[0-9]{1,6}\\.zip)[\"']", RegexOptions.CultureInvariant)]
    private static partial Regex OfficialLink();

    private static void ValidatePackageArchive(string path)
    {
        if (new FileInfo(path).Length > MaximumZipBytes) throw new InvalidDataException("驱动 ZIP 超过 20 MiB 上限。");
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 512) throw new InvalidDataException("驱动包文件数量异常。");
        long total = 0; var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool installer = false;
        foreach (var entry in archive.Entries)
        {
            var safe = SafeEntryPath(Path.GetTempPath(), entry.FullName);
            if (!paths.Add(safe) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("驱动包包含重复文件或符号链接。");
            total = checked(total + entry.Length);
            if (total > MaximumExtractedBytes) throw new InvalidDataException("驱动包解压大小超过 100 MiB。");
            if (entry.FullName == "VBCABLE_Setup_x64.exe") installer = true;
        }
        if (!installer) throw new InvalidDataException("官方包缺少根目录 VBCABLE_Setup_x64.exe，不会执行其他文件。");
    }

    private static string SafeEntryPath(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || Path.IsPathRooted(name) || name.Contains(':') || name.Any(char.IsControl)) throw new InvalidDataException("驱动包路径非法。");
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')) throw new InvalidDataException("驱动包路径越界或包含 Windows 保留名称。");
        }
        if (parts.Length == 0) throw new InvalidDataException("驱动包路径为空。");
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(Path.Combine(parent, Path.Combine(parts)));
        if (!output.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("驱动包路径越界。");
        return output;
    }

    private static void EnsureNoLinks(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("组件文件是重解析点，已停止操作。");
        for (DirectoryInfo? dir = new(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!); dir is not null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("组件目录包含重解析点，已停止操作。");
    }

    private static void VerifyExtractedFiles(DriverPackage package, string target)
    {
        using var archive = ZipFile.OpenRead(package.FilePath);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = SafeEntryPath(target, entry.FullName); EnsureNoLinks(path);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            allowed.Add(path);
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Length) throw new InvalidDataException("解压组件缺失或被修改。请清理对应组件缓存后重新下载。");
            using var zipped = entry.Open(); using var extracted = File.OpenRead(path);
            if (!SHA256.HashData(zipped).SequenceEqual(SHA256.HashData(extracted))) throw new InvalidDataException("解压组件校验失败，不会启动安装器。");
        }
        // Unexpected companion files (including DLL side-loading candidates) are forbidden.
        var pending = new Stack<string>(); pending.Push(target);
        while (pending.TryPop(out var directory))
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureNoLinks(item);
                if (Directory.Exists(item)) pending.Push(item); else if (!allowed.Contains(item)) throw new InvalidDataException("解压目录存在额外文件，已禁止启动。");
            }
    }

    private static void DeleteStage(string stage, string parent, string hash)
    {
        var resolved = Path.GetFullPath(stage);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(parent) || !Path.GetFileName(resolved).StartsWith(hash + ".stage-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(resolved)[(hash.Length + 7)..], "N", out _)) throw new InvalidOperationException("组件暂存目录校验失败。");
        EnsureNoLinks(resolved);
        var pending = new Stack<string>(); pending.Push(resolved);
        while (pending.TryPop(out var directory)) foreach (var item in Directory.EnumerateFileSystemEntries(directory)) { EnsureNoLinks(item); if (Directory.Exists(item)) pending.Push(item); }
        Directory.Delete(resolved, true);
    }

    private static void CopyBounded(Stream source, Stream target, long expected)
    {
        var buffer = new byte[65536]; long total = 0; int read;
        while ((read = source.Read(buffer)) > 0) { total += read; if (total > expected || total > MaximumExtractedBytes) throw new InvalidDataException("实际解压大小不符。"); target.Write(buffer, 0, read); }
        if (total != expected) throw new InvalidDataException("驱动包文件不完整。");
    }
    private static async Task CopyBoundedAsync(Stream source, Stream target, long max, Action<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[65536]; long total = 0; int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        { total += read; if (total > max) throw new InvalidDataException("下载或页面超过大小上限。"); await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); progress?.Invoke(total); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        http.Dispose();
        lifetime.Dispose();
        // Do not dispose the managed-only semaphore while an in-flight operation can still Release it.
        // No AvailableWaitHandle is created, so leaving it for collection does not retain a native handle.
    }
}
