using System.Globalization;
using MicStudio.Core;

namespace MicStudio.App;

/// <summary>
/// Presentation-only localization. Stable identifiers, preset files and audio
/// settings stay language-neutral; this class translates text at the UI boundary.
/// </summary>
internal static class Loc
{
    public static bool English => UiText.IsEnglish;

    public static string T(string chinese, string english) => UiText.IsEnglish ? english : chinese;

    public static string F(string chinese, string english, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, UiText.IsEnglish ? english : chinese, arguments);

    /// <summary>Translates a complete known sentence, leaving unknown text untouched.</summary>
    public static string Tr(string text) =>
        UiText.IsEnglish && EnglishText.Map.TryGetValue(text, out var english) ? english : text;

    public static string PageName(int page) => page switch
    {
        0 => T("麦克风", "Microphone"),
        1 => T("均衡器", "Equalizer"),
        2 => T("效果", "Effects"),
        3 => T("预设", "Presets"),
        4 => T("设备", "Devices"),
        5 => T("诊断", "Diagnostics"),
        6 => T("设置", "Settings"),
        _ => ""
    };

    public static string BandName(int index, string stored) => index switch
    {
        0 => T("高通", "High-pass"),
        1 => T("低架", "Low shelf"),
        2 => T("低频", "Low"),
        3 => T("中低频", "Low-mid"),
        4 => T("中频", "Mid"),
        5 => T("清晰度", "Clarity"),
        6 => T("高架", "High shelf"),
        7 => T("低通", "Low-pass"),
        _ => Tr(stored)
    };

    public static string PresetName(SoundPreset preset) => preset.IsBuiltIn ? preset.Id switch
    {
        "flat" => T("平直", "Flat"),
        "natural" => T("自然", "Natural"),
        "clear" => T("清晰", "Clear"),
        "warm" => T("温暖", "Warm"),
        _ => preset.Name
    } : preset.Name;

    public static string PresetDescription(SoundPreset preset) => preset.IsBuiltIn ? preset.Id switch
    {
        "flat" => T("增益为 0 dB，EQ 总开关关闭；保留最终限幅。", "0 dB gain with the master EQ off; final limiting stays enabled."),
        "natural" => T("轻微去除低频震动与浑浊，保留人声本来的质感。起始参数，需实录试听。", "Gently removes low-frequency rumble and mud while keeping the natural voice. Starting point; verify with a live recording."),
        "clear" => T("减少闷声，适度提升发音清晰度。不会自动提高麦克风输入增益。", "Reduces boxiness and adds modest speech clarity. Does not raise microphone input gain."),
        "warm" => T("增加低频厚度，轻柔收敛高频。屏幕后麦克风仍需重新校准与试听。", "Adds low-end weight and gently tames highs. A monitor-mounted microphone still needs its own calibration and listening test."),
        _ => preset.Description
    } : preset.Description;

    public static string CreativeTheme(string id) => id switch
    {
        "warm" => T("温暖", "Warm"),
        "crunch" => T("爆炸麦", "Boom"),
        "robot" => T("机器人", "Robot"),
        "echo" => T("回声", "Echo"),
        "space" => T("空间", "Space"),
        "phone" => T("电话 / 电台", "Phone / radio"),
        "helium" => T("氢气音", "Helium"),
        "loli" => T("萝莉音", "Higher pitch"),
        "monster" => T("怪兽 / 低沉", "Monster"),
        "custom" => T("自定义", "Custom"),
        _ => id
    };

    public static string Join(IEnumerable<string> values) => string.Join(T("、", ", "), values.Select(Tr));
}
