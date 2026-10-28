using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MicStudio.Core;

namespace MicStudio.Apo;

public sealed class ApoChangePlan
{
    internal ApoChangePlan() { }
    public bool CanApply { get; internal init; }
    public string Message { get; internal init; } = "";
    public string PreviewText { get; internal init; } = "";
    public string FragmentText { get; internal init; } = "";
    public string ExpectedMainHash { get; internal init; } = "";
    public string ExpectedFragmentHash { get; internal init; } = "";
    public IReadOnlyList<string> UnsupportedEffects { get; internal init; } = [];
    internal byte[] NewMainBytes { get; init; } = [];
    internal string FragmentName { get; init; } = "";
    internal string CurrentFragmentName { get; init; } = "";
    internal string ConfigDirectory { get; init; } = "";
}

public sealed record ApoOperationResult(bool Success, bool NeedsElevation, string Message);

/// <summary>
/// A narrow adapter: no arbitrary APO text can be supplied to Apply. Plans are generated internally,
/// content-addressed fragments are immutable, and config.txt is the single atomic commit point.
/// Unknown includes/conditionals and Peace ownership deliberately require the official editor.
/// </summary>
public sealed partial class ApoConfigService
{
    public const string BeginMarker = "# >>> Mic Studio managed hook v1 >>>";
    public const string EndMarker = "# <<< Mic Studio managed hook v1 <<<";
    private const int MaximumConfigBytes = 1024 * 1024;
    private readonly ApoInstallation installation;
    private readonly string configDirectory;
    private readonly string mainPath;
    private static readonly object OperationLock = new();

    public ApoConfigService(ApoInstallation installation)
    {
        this.installation = installation;
        configDirectory = string.IsNullOrWhiteSpace(installation.ConfigDirectory) ? "" : Path.GetFullPath(installation.ConfigDirectory);
        mainPath = configDirectory.Length == 0 ? "" : Path.Combine(configDirectory, "config.txt");
    }

    public ApoChangePlan Preview(AudioSettings settings, string captureEndpointId, int sampleRate = 48000)
    {
        IReadOnlyList<string> unsupported = [];
        if (!installation.Found)
            return Rejected("未检测到 Equalizer APO；当前设置仅保存为草稿。", unsupported);
        try
        {
            ArgumentNullException.ThrowIfNull(settings);
            settings.Validate(sampleRate);
            unsupported = ApoConfigGenerator.GetUnsupportedEffects(settings);
            var fragment = ApoConfigGenerator.Generate(settings.Clone(), captureEndpointId, sampleRate);
            var document = ReadMain();
            var owned = ParseOwnedBlock(document.Text);
            CheckExternalConfiguration(document.Text, owned);
            var oldFragmentHash = "";
            if (owned is not null)
                oldFragmentHash = CheckManagedFragment(owned.FragmentName);
            var fragmentHash = Hash(Encoding.UTF8.GetBytes(fragment));
            var relativeName = "MicStudio/eq-" + fragmentHash.ToLowerInvariant() + ".txt";
            var hook = BuildHook(relativeName, document.NewLine);
            var newText = owned is null ? document.Text + hook : document.Text[..owned.Start] + hook + document.Text[owned.End..];
            var scope = "仅目标录音端点；Stage: capture；Channel: all。";
            return new ApoChangePlan
            {
                CanApply = true,
                Message = "可以应用。" + scope + "配置写入后仍需验证实际音频与 Discord。",
                FragmentText = fragment,
                ExpectedMainHash = document.Hash,
                ExpectedFragmentHash = oldFragmentHash,
                CurrentFragmentName = owned?.FragmentName ?? "",
                FragmentName = relativeName,
                NewMainBytes = document.Encode(newText),
                ConfigDirectory = configDirectory,
                UnsupportedEffects = unsupported,
                PreviewText = "主配置：保留原有内容，仅" + (owned is null ? "追加" : "更新") + " Mic Studio 自有挂接。\n" +
                    scope + "\n\n+ " + hook.Replace(document.NewLine, "\n+ ") + "\n独立片段：\n" + fragment +
                    "\n注意：APO 本配置只包含线性 EQ 与增益，不包含原生线路的动态效果。\n" +
                    "关闭 Mic Studio 不会自动撤销系统 APO；恢复按钮仅移除本软件挂接。"
            };
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Rejected(ex.Message, unsupported);
        }
    }

    public ApoOperationResult Apply(ApoChangePlan plan)
    {
        lock (OperationLock)
        {
            try
            {
                using var writer = EnterCrossProcessWriter();
                if (!plan.CanApply || plan.ConfigDirectory != configDirectory || !installation.Found)
                    return new(false, false, "此预览不能应用。请重新生成预览。");
                ValidateNoReparsePoints();
                var current = ReadMain();
                if (current.Hash != plan.ExpectedMainHash)
                    return new(false, false, "配置冲突：预览后其他程序修改了 config.txt。已停止写入，请重新预览。");
                var owned = ParseOwnedBlock(current.Text);
                CheckExternalConfiguration(current.Text, owned);
                if ((owned?.FragmentName ?? "") != plan.CurrentFragmentName ||
                    (owned is not null && CheckManagedFragment(owned.FragmentName) != plan.ExpectedFragmentHash))
                    return new(false, false, "配置冲突：Mic Studio 片段已被外部修改。已停止写入。");

                var fragmentBytes = new UTF8Encoding(false).GetBytes(plan.FragmentText);
                if (!IsManagedFragmentName(plan.FragmentName) ||
                    plan.FragmentName != "MicStudio/eq-" + Hash(fragmentBytes).ToLowerInvariant() + ".txt" ||
                    !plan.FragmentText.StartsWith(ApoConfigGenerator.FragmentOwner + Environment.NewLine, StringComparison.Ordinal))
                    return new(false, false, "片段校验失败，请重新预览。");
                var managedDirectory = Path.Combine(configDirectory, "MicStudio");
                Directory.CreateDirectory(managedDirectory);
                var fragmentPath = ResolveFragmentPath(plan.FragmentName);
                var createdFragment = false;
                if (File.Exists(fragmentPath))
                {
                    if (Hash(ReadBoundedFile(fragmentPath)) != Hash(fragmentBytes))
                        return new(false, false, "内容寻址片段发生冲突；没有更改配置。");
                }
                else
                {
                    using var stream = new FileStream(fragmentPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    stream.Write(fragmentBytes);
                    stream.Flush(true);
                    createdFragment = true;
                }
                try
                {
                    // Backups are for inspection only. Restore never replays an old whole-file backup.
                    var snapshots = Path.Combine(managedDirectory, "snapshots");
                    Directory.CreateDirectory(snapshots);
                    File.WriteAllBytes(Path.Combine(snapshots, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N") + ".bak"), current.Bytes);
                    if (ReadMain().Hash != plan.ExpectedMainHash)
                        throw new InvalidOperationException("配置冲突：写入前检测到外部变更。原配置未被覆盖。");
                    WriteAtomic(mainPath, plan.NewMainBytes);
                }
                catch
                {
                    if (createdFragment)
                    {
                        // It was not referenced before the commit; remove only this newly-created immutable fragment.
                        try { File.Delete(fragmentPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                    throw;
                }
                return new(true, false, "配置已保存；目标设备绑定、是否需要重启及实际音频路径仍待验证。Discord 待验证。");
            }
            catch (UnauthorizedAccessException)
            {
                return new(false, true, "受保护的 APO 配置需要管理员权限。可主动请求 UAC 后重新预览并应用；取消不会影响草稿。");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                return new(false, false, "未应用：" + ex.Message);
            }
        }
    }

    /// <summary>Removes just our marker block from the current config, preserving later external edits.</summary>
    public ApoOperationResult RemoveOwnedConfiguration()
    {
        lock (OperationLock)
        {
            try
            {
                using var writer = EnterCrossProcessWriter();
                if (!installation.Found) return new(false, false, "未检测到 Equalizer APO。");
                ValidateNoReparsePoints();
                var document = ReadMain();
                var owned = ParseOwnedBlock(document.Text);
                if (owned is null) return new(true, false, "没有 Mic Studio 挂接需要移除。");
                // Never delete the independent APO install, user files or historical fragments.
                var bytes = document.Encode(document.Text[..owned.Start] + document.Text[owned.End..]);
                if (ReadMain().Hash != document.Hash)
                    return new(false, false, "配置冲突：恢复前主配置已变更，请重试。");
                WriteAtomic(mainPath, bytes);
                return new(true, false, "已移除 Mic Studio 自有挂接，保留其他工具的配置。音频重载结果仍待验证。");
            }
            catch (UnauthorizedAccessException)
            { return new(false, true, "移除受保护的 APO 挂接需要管理员权限；可以请求 UAC 后重试。"); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            { return new(false, false, "未恢复：" + ex.Message); }
        }
    }

    private void ValidateNoReparsePoints()
    {
        if (configDirectory.Length == 0 || !Directory.Exists(configDirectory))
            throw new InvalidOperationException("APO 配置目录不存在。请使用官方安装程序完成安装。");
        for (DirectoryInfo? directory = new(configDirectory); directory is not null; directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("配置目录包含重解析点；为避免写入错误位置，请使用官方编辑器。");
        foreach (var path in new[] { mainPath, Path.Combine(configDirectory, "MicStudio"), Path.Combine(configDirectory, "MicStudio", "snapshots") })
            if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("配置文件或自有目录为重解析点，已暂停写入。");
    }

    private ConfigDocument ReadMain()
    {
        if (mainPath.Length == 0) throw new InvalidOperationException("APO 配置路径不可用。");
        if (!Directory.Exists(configDirectory)) throw new InvalidOperationException("APO 配置目录不存在。");
        if (!File.Exists(mainPath)) return ConfigDocument.Decode([]);
        return ConfigDocument.Decode(ReadBoundedFile(mainPath));
    }

    private string ResolveFragmentPath(string name)
    {
        if (!IsManagedFragmentName(name)) throw new InvalidOperationException("自有片段路径不合法。");
        var path = Path.GetFullPath(Path.Combine(configDirectory, name.Replace('/', Path.DirectorySeparatorChar)));
        var expectedParent = Path.Combine(configDirectory, "MicStudio");
        if (!string.Equals(Path.GetDirectoryName(path), expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("片段路径越界。");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("片段文件是重解析点，已暂停写入。");
        return path;
    }

    private string CheckManagedFragment(string name)
    {
        var path = ResolveFragmentPath(name);
        if (!File.Exists(path)) throw new InvalidOperationException("自有片段缺失；请先移除旧挂接再重新应用。");
        var bytes = ReadBoundedFile(path);
        var contentHash = Hash(bytes);
        if (!Encoding.UTF8.GetString(bytes).StartsWith(ApoConfigGenerator.FragmentOwner + Environment.NewLine, StringComparison.Ordinal) ||
            name != "MicStudio/eq-" + contentHash.ToLowerInvariant() + ".txt")
            throw new InvalidOperationException("配置冲突：自有片段已被其他程序修改，已暂停写入。");
        return contentHash;
    }

    private static void CheckExternalConfiguration(string text, OwnedBlock? owned)
    {
        var external = owned is null ? text : text[..owned.Start] + text[owned.End..];
        if (external.Contains("Peace", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("检测到 Peace 管理标记。请先在官方编辑器明确配置归属，Mic Studio 暂停接管。");
        if (owned is not null)
            foreach (var line in Lines(text[owned.End..]))
                if (!IsCommentOrEmpty(line))
                    throw new InvalidOperationException("自有挂接后出现其他音频命令，无法确保作用域。请先移除挂接并检查配置。");
        foreach (var line in Lines(external))
        {
            if (IsCommentOrEmpty(line)) continue;
            if (!KnownSimpleCommand().IsMatch(line) || line.Contains('`'))
                throw new InvalidOperationException("现有配置包含 Include、条件、插件或未识别命令；请用官方编辑器检查并导出差异，自动写入已暂停。");
        }
    }

    [GeneratedRegex(@"^\s*(?:Device\s*:|Stage\s*:|Channel\s*:|Preamp\s*:|Filter(?:\s+\d+)?\s*:|GraphicEQ\s*:|Delay\s*:|Copy\s*:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownSimpleCommand();
    [GeneratedRegex(@"^MicStudio/eq-[0-9a-f]{64}\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex ManagedFragmentPattern();
    private static bool IsManagedFragmentName(string name) => ManagedFragmentPattern().IsMatch(name);
    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');
    private static bool IsCommentOrEmpty(string line) => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    private static OwnedBlock? ParseOwnedBlock(string text)
    {
        var start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 && end < 0) return null;
        if (start < 0 || end < start || text.IndexOf(BeginMarker, start + BeginMarker.Length, StringComparison.Ordinal) >= 0 ||
            text.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("Mic Studio 挂接标记损坏或重复；请使用官方编辑器检查。");
        if (start > 0 && text[start - 1] != '\n') throw new InvalidOperationException("挂接标记必须独占一行。");
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var raw = text[start..(end + EndMarker.Length)];
        var lines = Lines(raw);
        if (lines.Length != 9 || lines[0] != BeginMarker || lines[1] != "Device: all" ||
            lines[2] != "Stage: capture" || lines[3] != "Channel: all" ||
            !lines[4].StartsWith("Include: ", StringComparison.Ordinal) ||
            lines[5] != "Device: all" || lines[6] != "Stage: post-mix capture" ||
            lines[7] != "Channel: all" || lines[8] != EndMarker)
            throw new InvalidOperationException("Mic Studio 挂接内容被修改；为保护外部配置，已暂停自动写入。");
        var name = lines[4]["Include: ".Length..];
        if (!IsManagedFragmentName(name)) throw new InvalidOperationException("Mic Studio 挂接片段路径不合法。");
        // BuildHook always owns exactly one preceding line break and its terminal break.
        var blockStart = start >= 2 && text.AsSpan(start - 2, 2).SequenceEqual("\r\n") ? start - 2 : start >= 1 && text[start - 1] == '\n' ? start - 1 : start;
        var blockEnd = end + EndMarker.Length;
        if (text.AsSpan(blockEnd).StartsWith("\r\n")) blockEnd += 2;
        else if (blockEnd < text.Length && text[blockEnd] == '\n') blockEnd++;
        else if (blockEnd < text.Length) throw new InvalidOperationException("挂接结束标记必须独占一行。");
        return new(blockStart, blockEnd, name);
    }

    private static string BuildHook(string relativeName, string nl) => nl + string.Join(nl,
        BeginMarker, "Device: all", "Stage: capture", "Channel: all", "Include: " + relativeName,
        "Device: all", "Stage: post-mix capture", "Channel: all", EndMarker) + nl;

    private static void WriteAtomic(string target, byte[] bytes)
    {
        var temp = target + ".micstudio-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static byte[] ReadBoundedFile(string path)
    {
        // Keep allocation bounded even if another process grows the file during inspection.
        // A read-share handle also prevents in-place writers from modifying bytes while this snapshot is read.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumConfigBytes)
            throw new InvalidOperationException("APO 配置超过 1 MiB，需使用官方编辑器检查。");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidOperationException("读取时配置大小发生变化，请重新预览。");
        return bytes;
    }

    private IDisposable EnterCrossProcessWriter()
    {
        // Cooperating Mic Studio instances share this lock, including an elevated restart in the same session.
        // Other editors do not share it; hashes are still checked immediately before the atomic commit.
        var mutex = new Mutex(false, @"Local\MicStudio.Apo." + Hash(Encoding.UTF8.GetBytes(configDirectory.ToUpperInvariant())));
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("另一个 Mic Studio 正在更改 APO 配置，请稍后重新预览。");
            return new WriterLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class WriterLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static ApoChangePlan Rejected(string message, IReadOnlyList<string> unsupported) => new()
    { Message = message, PreviewText = message, UnsupportedEffects = unsupported };
    private sealed record OwnedBlock(int Start, int End, string FragmentName);

    private sealed record ConfigDocument(byte[] Bytes, string Text, Encoding Encoding, byte[] Preamble)
    {
        public string Hash => ApoConfigService.Hash(Bytes);
        public string NewLine => Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        public byte[] Encode(string text) => [.. Preamble, .. Encoding.GetBytes(text)];
        public static ConfigDocument Decode(byte[] bytes)
        {
            Encoding encoding;
            var prefix = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); prefix = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); prefix = 2; }
            else { encoding = new UTF8Encoding(false, true); if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) prefix = 3; }
            try { return new(bytes, encoding.GetString(bytes, prefix, bytes.Length - prefix), encoding, bytes[..prefix]); }
            catch (DecoderFallbackException) { throw new InvalidOperationException("配置不是受支持的 UTF-8/UTF-16 编码；已暂停写入以保留原始内容。"); }
        }
    }
}
