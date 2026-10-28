namespace MicStudio.App;

public partial class MainWindow
{
    private void BuildEffects()
    {
        Header("CRAFT YOUR VOICE / 03", "一条完整的人声效果链。", "每个已开放的参数都接入真实处理器。轻声、尾音和自然音色，请用真实录音确认。");
        if (!LocalMode) Add(Card("完整效果需要虚拟输入", "APO 原生路线只应用增益与 EQ。", B("使用虚拟输入", () => ModePicker.SelectedIndex = 1, true)));
        var enabled = LocalMode;
        Add(Card("01  频谱降噪", "经典 STFT Wiener 频谱衰减，适合稳定底噪，非 RNNoise。参考电平是手动参数，不提供虚假的底噪学习。切换开关建议先停止采集。",
            Toggle("启用频谱降噪", Draft.Denoise.Enabled, v => vm.Change(s => s.Denoise.Enabled = v), enabled),
            Parameter("衰减强度", 0, 1, Draft.Denoise.Strength, v => vm.Change(s => s.Denoise.Strength = v, true), "0–1", .01, enabled),
            Parameter("底噪参考", -90, -20, Draft.Denoise.NoiseFloorDb, v => vm.Change(s => s.Denoise.NoiseFloorDb = v, true), "dBFS", .5, enabled)));
        Add(Card("02  高通与参数均衡器", "减少低频震动、调整音色与清晰度。", B("编辑八段均衡器", () => ShowPage(1))));
        Add(Card("03  压缩器", "压低超过门限的响声，让讲话音量更稳定；补偿过高仍可能增加底噪。",
            Toggle("启用压缩", Draft.Compressor.Enabled, v => vm.Change(s => s.Compressor.Enabled = v), enabled),
            Parameter("压缩门限", -60, 0, Draft.Compressor.ThresholdDb, v => vm.Change(s => s.Compressor.ThresholdDb = v, true), "dB", .5, enabled),
            Parameter("压缩比例", 1, 20, Draft.Compressor.Ratio, v => vm.Change(s => s.Compressor.Ratio = v, true), ":1", .1, enabled),
            Parameter("启动时间", .1, 200, Draft.Compressor.AttackMs, v => vm.Change(s => s.Compressor.AttackMs = v, true), "ms", .1, enabled),
            Parameter("释放时间", 10, 2000, Draft.Compressor.ReleaseMs, v => vm.Change(s => s.Compressor.ReleaseMs = v, true), "ms", 1, enabled),
            Parameter("压缩补偿", 0, 18, Draft.Compressor.MakeupDb, v => vm.Change(s => s.Compressor.MakeupDb = v, true), "dB", .1, enabled)));
        Add(Card("04  柔和扩展器 / 噪声门", "压低停顿中的声音；它与频谱降噪不同。保持与较长释放有助于保留句尾，默认关闭。",
            Toggle("启用扩展器", Draft.Gate.Enabled, v => vm.Change(s => s.Gate.Enabled = v), enabled),
            Parameter("扩展门限", -90, -10, Draft.Gate.ThresholdDb, v => vm.Change(s => s.Gate.ThresholdDb = v, true), "dB", .5, enabled),
            Parameter("扩展比例", 1, 10, Draft.Gate.Ratio, v => vm.Change(s => s.Gate.Ratio = v, true), ":1", .1, enabled),
            Parameter("启动", .1, 100, Draft.Gate.AttackMs, v => vm.Change(s => s.Gate.AttackMs = v, true), "ms", .1, enabled),
            Parameter("保持", 0, 1000, Draft.Gate.HoldMs, v => vm.Change(s => s.Gate.HoldMs = v, true), "ms", 1, enabled),
            Parameter("释放", 20, 2000, Draft.Gate.ReleaseMs, v => vm.Change(s => s.Gate.ReleaseMs = v, true), "ms", 1, enabled),
            Parameter("最大衰减", 0, 60, Draft.Gate.RangeDb, v => vm.Change(s => s.Gate.RangeDb = v, true), "dB", .5, enabled)));
        Add(Card("05  创意声音", "通话默认关闭回声和混响。机器人采用环形调制，不是自然音高移调。",
            Toggle("软饱和", Draft.Creative.SaturationEnabled, v => vm.Change(s => s.Creative.SaturationEnabled = v), enabled),
            Parameter("饱和驱动", 0, 24, Draft.Creative.DriveDb, v => vm.Change(s => s.Creative.DriveDb = v, true), "dB", .1, enabled),
            Toggle("机器人 · 环形调制", Draft.Creative.RobotEnabled, v => vm.Change(s => s.Creative.RobotEnabled = v), enabled),
            Parameter("调制频率", 20, 300, Draft.Creative.RobotFrequencyHz, v => vm.Change(s => s.Creative.RobotFrequencyHz = v, true), "Hz", 1, enabled),
            Toggle("创意回声", Draft.Creative.DelayEnabled, v => vm.Change(s => s.Creative.DelayEnabled = v), enabled),
            Parameter("回声间隔", 20, 1000, Draft.Creative.DelayMs, v => vm.Change(s => s.Creative.DelayMs = v, true), "ms", 1, enabled),
            Parameter("回声混合", 0, .6, Draft.Creative.DelayMix, v => vm.Change(s => s.Creative.DelayMix = v, true), "0–0.6", .01, enabled),
            Parameter("回声反馈", 0, .75, Draft.Creative.DelayFeedback, v => vm.Change(s => s.Creative.DelayFeedback = v, true), "0–0.75", .01, enabled),
            Toggle("混响", Draft.Creative.ReverbEnabled, v => vm.Change(s => s.Creative.ReverbEnabled = v), enabled),
            Parameter("混响混合", 0, .6, Draft.Creative.ReverbMix, v => vm.Change(s => s.Creative.ReverbMix = v, true), "0–0.6", .01, enabled)));
        Add(Card("06  最终采样峰值限幅", "防止本地输出突然过响；不是重建真峰值限幅，不能恢复输入设备已削波的音频。",
            Toggle("启用最终限幅（推荐）", Draft.Limiter.Enabled, v => vm.Change(s => s.Limiter.Enabled = v), enabled),
            Parameter("输出上限", -12, -.1, Draft.Limiter.CeilingDb, v => vm.Change(s => s.Limiter.CeilingDb = v, true), "dBFS", .1, enabled),
            Parameter("限幅释放", 10, 1000, Draft.Limiter.ReleaseMs, v => vm.Change(s => s.Limiter.ReleaseMs = v, true), "ms", 1, enabled)));
    }
}
