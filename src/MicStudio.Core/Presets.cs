using System.Text;
using System.Text.Json;

namespace MicStudio.Core;

public sealed class SoundPreset
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新预设";
    public string Description { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public bool IsFavorite { get; set; }
    public AudioSettings Settings { get; set; } = new();
    public SoundPreset Clone() => new() { SchemaVersion = SchemaVersion, Id = Id, Name = Name, Description = Description, IsBuiltIn = IsBuiltIn, IsFavorite = IsFavorite, Settings = Settings.Clone() };
    public void Validate()
    {
        if (SchemaVersion != 1) throw new ArgumentException("不支持此预设版本；旧版需要显式迁移。");
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 || Id.Any(char.IsControl)) throw new ArgumentException("预设标识无效。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Name.Any(char.IsControl)) throw new ArgumentException("预设名称应为 1–80 个字符。");
        if (Description is null || Description.Length > 1000 || Description.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')) throw new ArgumentException("预设说明无效。");
        if (Settings is null) throw new ArgumentException("预设没有参数。");
        Settings.Validate();
    }
}

public static class BuiltInPresets
{
    // Return copies so modifying a loaded preset never mutates factory presets.
    public static IReadOnlyList<SoundPreset> All => Create();

    private static List<SoundPreset> Create()
    {
        var flat = new SoundPreset { Id = "flat", Name = "平直", Description = "增益为 0 dB，EQ 总开关关闭；保留最终限幅。", IsBuiltIn = true };
        flat.Settings.EqEnabled = false;
        var natural = new SoundPreset { Id = "natural", Name = "自然", Description = "轻微去除低频震动与浑浊，保留人声本来的质感。起始参数，需实录试听。", IsBuiltIn = true };
        natural.Settings.Bands[0].Enabled = true; natural.Settings.Bands[0].FrequencyHz = 80;
        natural.Settings.Bands[2].Enabled = true; natural.Settings.Bands[2].GainDb = -1.5; natural.Settings.Bands[2].Q = 0.8;
        natural.Settings.Bands[5].Enabled = true; natural.Settings.Bands[5].GainDb = 1.2; natural.Settings.Bands[5].Q = 0.8;
        var clear = new SoundPreset { Id = "clear", Name = "清晰", Description = "减少闷声，适度提升发音清晰度。不会自动提高麦克风输入增益。", IsBuiltIn = true };
        clear.Settings.Bands[0].Enabled = true; clear.Settings.Bands[0].FrequencyHz = 95;
        clear.Settings.Bands[2].Enabled = true; clear.Settings.Bands[2].GainDb = -2.5; clear.Settings.Bands[2].Q = 0.8;
        clear.Settings.Bands[5].Enabled = true; clear.Settings.Bands[5].GainDb = 2.5; clear.Settings.Bands[5].Q = 0.8;
        clear.Settings.Bands[6].Enabled = true; clear.Settings.Bands[6].GainDb = 1;
        var warm = new SoundPreset { Id = "warm", Name = "温暖", Description = "增加低频厚度，轻柔收敛高频。屏幕后麦克风仍需重新校准与试听。", IsBuiltIn = true };
        warm.Settings.Bands[0].Enabled = true; warm.Settings.Bands[0].FrequencyHz = 65;
        warm.Settings.Bands[1].Enabled = true; warm.Settings.Bands[1].GainDb = 2;
        warm.Settings.Bands[6].Enabled = true; warm.Settings.Bands[6].GainDb = -1.5;
        return [flat, natural, clear, warm];
    }
}

public static class PresetStore
{
    public const int MaxImportBytes = 256 * 1024;

    public static SoundPreset ImportFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxImportBytes) throw new ArgumentException("预设文件为空或超过 256 KB。");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxImportBytes) throw new ArgumentException("预设文件超过 256 KB。");
        return ImportJson(Encoding.UTF8.GetString(bytes));
    }

    public static SoundPreset ImportJson(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxImportBytes) throw new ArgumentException("预设文件超过 256 KB。");
        var portable = JsonSerializer.Deserialize<PortablePreset>(json, JsonDefaults.Options) ?? throw new ArgumentException("预设文件无效。");
        var result = new SoundPreset { SchemaVersion = portable.SchemaVersion, Name = portable.Name, Description = portable.Description, Settings = portable.Settings, IsBuiltIn = false };
        result.Validate();
        return result;
    }

    public static string ExportJson(SoundPreset preset)
    {
        preset.Validate();
        // No device IDs, app settings, plugin paths, commands or source preset IDs in portable files.
        return JsonSerializer.Serialize(new PortablePreset { SchemaVersion = 1, Name = preset.Name, Description = preset.Description, Settings = preset.Settings.Clone() }, JsonDefaults.Options);
    }

    public static void ExportFile(string path, SoundPreset preset) => SettingsStore.WriteTextAtomic(path, ExportJson(preset));

    private sealed class PortablePreset
    {
        public required int SchemaVersion { get; set; }
        public required string Name { get; set; }
        public string Description { get; set; } = "";
        public required AudioSettings Settings { get; set; }
    }
}
