using System.Text;
using System.Text.Json;

namespace MicStudio.Core;

public sealed record SettingsLoadResult<T>(T Value, string? Warning);

public static class SettingsStore
{
    private const int MaxSettingsBytes = 1024 * 1024;

    public static SettingsLoadResult<T> Load<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new(new T(), null);
        try { return new(Read<T>(path), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            string preserved = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            try { File.Copy(path, preserved, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try
            {
                if (File.Exists(path + ".bak")) return new(Read<T>(path + ".bak"), UiText.T("设置文件损坏，已读取上次备份；损坏文件已尽可能保留。请检查当前参数。", "The settings file was damaged, so the previous backup was loaded. The damaged file was preserved where possible. Review the current parameters."));
            }
            catch (Exception backupEx) when (backupEx is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
            return new(new T(), UiText.T("设置文件无法读取，已载入默认草稿；损坏文件已尽可能保留。不会自动应用音频配置。", "The settings file could not be read, so a default draft was loaded. The damaged file was preserved where possible. No audio configuration is applied automatically."));
        }
    }

    private static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > MaxSettingsBytes) throw new ArgumentException("设置文件过大。");
        var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonDefaults.Options);
        if (value is null) throw new ArgumentException("设置文件为空。");
        if (value is AudioSettings audio) audio.Validate();
        return value;
    }

    public static void Save<T>(string path, T value)
    {
        if (value is AudioSettings audio) audio.Validate();
        WriteTextAtomic(path, JsonSerializer.Serialize(value, JsonDefaults.Options));
    }

    public static void WriteTextAtomic(string path, string text)
    {
        string absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        string temp = absolute + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes); stream.Flush(true);
            }
            if (File.Exists(absolute)) File.Replace(temp, absolute, absolute + ".bak", true);
            else File.Move(temp, absolute);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
