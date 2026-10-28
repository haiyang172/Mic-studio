using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MicStudio.Core;

namespace MicStudio.Apo;

public static partial class ApoConfigGenerator
{
    public const string FragmentOwner = "# Mic Studio managed EQ v1";

    [GeneratedRegex(@"^\{0\.0\.1\.00000000\}\.\{(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}$", RegexOptions.CultureInvariant)]
    private static partial Regex CaptureEndpointPattern();

    public static Guid ParseCaptureGuid(string endpointId)
    {
        var match = CaptureEndpointPattern().Match(endpointId ?? "");
        if (!match.Success) throw new ArgumentException("必须选择真实的 Windows 录音端点 ID；不接受设备名、播放端点或 APO 指令。", nameof(endpointId));
        return Guid.Parse(match.Groups["id"].Value);
    }

    public static IReadOnlyList<string> GetUnsupportedEffects(AudioSettings settings)
    {
        var names = new List<string>();
        if (settings.Denoise.Enabled) names.Add("频谱降噪");
        if (settings.Compressor.Enabled) names.Add("压缩器");
        if (settings.Gate.Enabled) names.Add("扩展器/噪声门");
        if (settings.Limiter.Enabled) names.Add("采样峰值限幅");
        if (settings.Creative.SaturationEnabled) names.Add("饱和");
        if (settings.Creative.RobotEnabled) names.Add("机器人环形调制");
        if (settings.Creative.DelayEnabled) names.Add("回声");
        if (settings.Creative.ReverbEnabled) names.Add("混响");
        return names;
    }

    /// <summary>Only whitelisted numeric EQ commands are emitted. Does not host or pretend to configure VST effects.</summary>
    public static string Generate(AudioSettings settings, string captureEndpointId, int sampleRate = 48000)
    {
        settings.Validate(sampleRate);
        var guid = ParseCaptureGuid(captureEndpointId);
        var sb = new StringBuilder();
        sb.AppendLine(FragmentOwner);
        sb.AppendLine("# Theoretical EQ and digital gains only. Audio path requires separate verification.");
        sb.AppendLine("Device: " + guid.ToString("B"));
        sb.AppendLine("Stage: capture");
        sb.AppendLine("Channel: all");
        if (settings.Bypass)
        {
            sb.AppendLine("# Mic Studio EQ bypassed; other software effects may still apply.");
            return sb.ToString();
        }
        if (GetUnsupportedEffects(settings).Count > 0)
            throw new ArgumentException("APO 原生配置仅支持增益与 EQ。高级效果须使用原生线路，或先在预览中明确排除这些效果。");
        sb.AppendLine("Preamp: " + F(settings.InputGainDb + settings.OutputGainDb) + " dB");
        for (var index = 0; index < settings.Bands.Count; index++)
        {
            var band = settings.Bands[index];
            if (!settings.EqEnabled || !band.Enabled) continue;
            var type = band.Type switch
            {
                EqFilterType.HighPass => "HPQ", EqFilterType.LowPass => "LPQ",
                EqFilterType.LowShelf => "LSC", EqFilterType.HighShelf => "HSC",
                EqFilterType.Peak => "PK", _ => throw new ArgumentException("不支持的滤波类型。")
            };
            sb.Append("Filter ").Append(index + 1).Append(": ON ").Append(type)
                .Append(" Fc ").Append(F(band.FrequencyHz)).Append(" Hz");
            if (band.Type is EqFilterType.Peak or EqFilterType.LowShelf or EqFilterType.HighShelf)
                sb.Append(" Gain ").Append(F(band.GainDb)).Append(" dB");
            sb.Append(" Q ").Append(F(band.Q)).AppendLine();
        }
        return sb.ToString();
    }

    private static string F(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("APO 参数必须为有限数值。");
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }
}
