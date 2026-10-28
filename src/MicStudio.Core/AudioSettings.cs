using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicStudio.Core;

public enum EqFilterType { HighPass, LowShelf, Peak, HighShelf, LowPass }

public sealed class EqBand
{
    public string Name { get; set; } = "频段";
    public bool Enabled { get; set; }
    public EqFilterType Type { get; set; } = EqFilterType.Peak;
    public double FrequencyHz { get; set; } = 1000;
    public double GainDb { get; set; }
    public double Q { get; set; } = 0.7071067811865476;
    public EqBand Clone() => (EqBand)MemberwiseClone();
}

public sealed class DenoiseSettings
{
    public bool Enabled { get; set; }
    public double Strength { get; set; } = 0.55;
    public double NoiseFloorDb { get; set; } = -55;
}

public sealed class CompressorSettings
{
    public bool Enabled { get; set; }
    public double ThresholdDb { get; set; } = -18;
    public double Ratio { get; set; } = 3;
    public double AttackMs { get; set; } = 10;
    public double ReleaseMs { get; set; } = 160;
    public double MakeupDb { get; set; }
}

public sealed class GateSettings
{
    public bool Enabled { get; set; }
    public double ThresholdDb { get; set; } = -52;
    public double Ratio { get; set; } = 2;
    public double AttackMs { get; set; } = 5;
    public double HoldMs { get; set; } = 150;
    public double ReleaseMs { get; set; } = 300;
    public double RangeDb { get; set; } = 18;
}

public sealed class LimiterSettings
{
    public bool Enabled { get; set; } = true;
    public double CeilingDb { get; set; } = -1;
    public double ReleaseMs { get; set; } = 80;
}

public sealed class CreativeSettings
{
    public bool SaturationEnabled { get; set; }
    public double DriveDb { get; set; } = 6;
    public bool RobotEnabled { get; set; }
    public double RobotFrequencyHz { get; set; } = 65;
    public bool DelayEnabled { get; set; }
    public double DelayMs { get; set; } = 180;
    public double DelayMix { get; set; } = 0.15;
    public double DelayFeedback { get; set; } = 0.2;
    public bool ReverbEnabled { get; set; }
    public double ReverbMix { get; set; } = 0.12;
}

public sealed class AudioSettings
{
    public int SchemaVersion { get; set; } = 1;
    public double InputGainDb { get; set; }
    public double OutputGainDb { get; set; }
    public bool Bypass { get; set; }
    public bool EqEnabled { get; set; } = true;
    public List<EqBand> Bands { get; set; } = CreateDefaultBands();
    public DenoiseSettings Denoise { get; set; } = new();
    public CompressorSettings Compressor { get; set; } = new();
    public GateSettings Gate { get; set; } = new();
    public LimiterSettings Limiter { get; set; } = new();
    public CreativeSettings Creative { get; set; } = new();

    public static List<EqBand> CreateDefaultBands(int sampleRate = 48000)
    {
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        double upper = Math.Min(20000, sampleRate * 0.48);
        return
    [
        new() { Name = "高通", Enabled = true, Type = EqFilterType.HighPass, FrequencyHz = 20 },
        new() { Name = "低架", Enabled = true, Type = EqFilterType.LowShelf, FrequencyHz = 120 },
        new() { Name = "低频", Enabled = true, Type = EqFilterType.Peak, FrequencyHz = 250 },
        new() { Name = "中低频", Enabled = true, Type = EqFilterType.Peak, FrequencyHz = 500 },
        new() { Name = "中频", Enabled = true, Type = EqFilterType.Peak, FrequencyHz = 1500 },
        new() { Name = "清晰度", Enabled = true, Type = EqFilterType.Peak, FrequencyHz = Math.Min(3500, upper) },
        new() { Name = "高架", Enabled = true, Type = EqFilterType.HighShelf, FrequencyHz = Math.Min(7000, upper) },
        new() { Name = "低通", Enabled = true, Type = EqFilterType.LowPass, FrequencyHz = upper }
    ];
    }

    public static AudioSettings CreateDefaultForSampleRate(int sampleRate) => new() { Bands = CreateDefaultBands(sampleRate) };

    /// <summary>Fits only the processing copy to the selected device; saved frequencies and disabled bands stay intact.</summary>
    public AudioSettings CreateProcessingSnapshot(int sampleRate, out IReadOnlyList<string> adjustedBands)
    {
        // Validate stored values at the supported maximum rate before adapting to the current endpoint.
        Validate(384000);
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var copy = Clone();
        var adjusted = new List<string>();
        double upper = Math.Min(20000, sampleRate * 0.48);
        foreach (var band in copy.Bands.Where(b => b.Enabled && b.FrequencyHz >= sampleRate * 0.49))
        {
            band.FrequencyHz = upper;
            adjusted.Add(band.Name);
        }
        copy.Validate(sampleRate);
        adjustedBands = adjusted;
        return copy;
    }

    public AudioSettings Clone() => JsonSerializer.Deserialize<AudioSettings>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;

    public void Validate(int sampleRate = 48000)
    {
        if (SchemaVersion != 1) throw new ArgumentException("不支持此设置版本；旧版预设需要显式迁移。");
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Check(InputGainDb, -24, 30, "输入增益"); Check(OutputGainDb, -24, 0, "输出补偿");
        if (Bands is null || Bands.Count != 8 || Bands.Any(b => b is null)) throw new ArgumentException("均衡器必须包含八个频段。");
        EqFilterType[] expected = [EqFilterType.HighPass, EqFilterType.LowShelf, EqFilterType.Peak, EqFilterType.Peak, EqFilterType.Peak, EqFilterType.Peak, EqFilterType.HighShelf, EqFilterType.LowPass];
        for (int i = 0; i < Bands.Count; i++)
        {
            var b = Bands[i];
            if (b.Type != expected[i]) throw new ArgumentException("频段类型或顺序不受支持。");
            if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 60 || b.Name.Any(char.IsControl)) throw new ArgumentException("频段名称无效。");
            Check(b.FrequencyHz, 20, b.Type == EqFilterType.HighPass ? 250 : 20000, "频率");
            if (b.Enabled && b.FrequencyHz >= sampleRate * 0.49) throw new ArgumentException($"{b.Name}频率超过当前采样率的有效范围。");
            Check(b.GainDb, -12, 12, "频段增益"); Check(b.Q, 0.2, 10, "Q");
        }
        if (Denoise is null || Compressor is null || Gate is null || Limiter is null || Creative is null) throw new ArgumentException("效果参数不完整。");
        Check(Denoise.Strength, 0, 1, "降噪强度"); Check(Denoise.NoiseFloorDb, -90, -20, "噪声参考电平");
        Check(Compressor.ThresholdDb, -60, 0, "压缩门限"); Check(Compressor.Ratio, 1, 20, "压缩比"); Check(Compressor.AttackMs, 0.1, 200, "压缩启动"); Check(Compressor.ReleaseMs, 10, 2000, "压缩释放"); Check(Compressor.MakeupDb, 0, 18, "压缩补偿");
        Check(Gate.ThresholdDb, -90, -10, "扩展门限"); Check(Gate.Ratio, 1, 10, "扩展比例"); Check(Gate.AttackMs, 0.1, 100, "扩展启动"); Check(Gate.HoldMs, 0, 1000, "扩展保持"); Check(Gate.ReleaseMs, 20, 2000, "扩展释放"); Check(Gate.RangeDb, 0, 60, "扩展范围");
        Check(Limiter.CeilingDb, -12, -0.1, "限幅上限"); Check(Limiter.ReleaseMs, 10, 1000, "限幅释放");
        Check(Creative.DriveDb, 0, 24, "饱和驱动"); Check(Creative.RobotFrequencyHz, 20, 300, "环形调制频率"); Check(Creative.DelayMs, 20, 1000, "回声间隔"); Check(Creative.DelayMix, 0, 0.6, "回声混合"); Check(Creative.DelayFeedback, 0, 0.75, "回声反馈"); Check(Creative.ReverbMix, 0, 0.6, "混响混合");
    }

    private static void Check(double value, double min, double max, string label)
    {
        if (!double.IsFinite(value) || value < min || value > max) throw new ArgumentException($"{label}必须在 {min} 到 {max} 之间。");
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 20,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
}
