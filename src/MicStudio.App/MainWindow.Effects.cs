using MicStudio.Core;

namespace MicStudio.App;

public partial class MainWindow
{
    private void BuildEffects()
    {
        Header("", "效果", "调整后实时生效，详细参数在高级功能中。");
        if (!LocalMode) Add(Card("本地效果", "当前系统路线支持增益与 EQ；完整效果需要虚拟输入。", B("使用虚拟输入", () => ModePicker.SelectedIndex = 1, true)));
        var enabled = LocalMode;
        Add(Card("降噪", "适合稳定底噪。",
            ButtonSwitch("降噪", Draft.Denoise.Enabled, v => vm.Change(s => s.Denoise.Enabled = v), enabled, readValue: () => Draft.Denoise.Enabled),
            Parameter("强度", 0, 100, Draft.Denoise.Strength * 100, v => vm.Change(s => s.Denoise.Strength = v / 100, true), "%", 1, enabled, readValue: () => Draft.Denoise.Strength * 100)));
        Add(Card("均衡器", "调整低频、清晰度与音色。", B("调整 EQ", () => ShowPage(1))));
        Add(Card("音量稳定", "压低过响的讲话。",
            ButtonSwitch("压缩", Draft.Compressor.Enabled, v => vm.Change(s => s.Compressor.Enabled = v), enabled, readValue: () => Draft.Compressor.Enabled),
            Parameter("强度", 0, 100, (Draft.Compressor.Ratio - 1) / 19 * 100, v => vm.Change(s => s.Compressor.Ratio = 1 + v / 100 * 19, true), "%", 1, enabled, readValue: () => (Draft.Compressor.Ratio - 1) / 19 * 100)));
        Add(Card("停顿降噪", "压低停顿中的声音；不会消除回声。",
            ButtonSwitch("柔和噪声门", Draft.Gate.Enabled, v => vm.Change(s => s.Gate.Enabled = v), enabled, readValue: () => Draft.Gate.Enabled),
            Parameter("强度", 0, 100, Draft.Gate.RangeDb / 60 * 100, v => vm.Change(s => s.Gate.RangeDb = v / 100 * 60, true), "%", 1, enabled, readValue: () => Draft.Gate.RangeDb / 60 * 100)));
        Add(BuildCreativeCard(enabled));
        Add(Card("输出保护", "控制处理后的采样峰值。",
            ButtonSwitch("限幅保护", Draft.Limiter.Enabled, v => vm.Change(s => s.Limiter.Enabled = v), enabled, readValue: () => Draft.Limiter.Enabled),
            Parameter("输出上限", -12, -.1, Draft.Limiter.CeilingDb, v => vm.Change(s => s.Limiter.CeilingDb = v, true), "dBFS", .1, enabled)));
        var details = new StackPanel();
        details.Children.Add(Card("降噪", "经典频谱衰减，参考电平为手动设置。",
            Parameter("底噪参考", -90, -20, Draft.Denoise.NoiseFloorDb, v => vm.Change(s => s.Denoise.NoiseFloorDb = v, true), "dBFS", .5, enabled)));
        details.Children.Add(Card("压缩器", "补偿增益过高会增加底噪。",
            Parameter("门限", -60, 0, Draft.Compressor.ThresholdDb, v => vm.Change(s => s.Compressor.ThresholdDb = v, true), "dB", .5, enabled),
            Parameter("比例", 1, 20, Draft.Compressor.Ratio, v => vm.Change(s => s.Compressor.Ratio = v, true), ":1", .1, enabled, readValue: () => Draft.Compressor.Ratio),
            Parameter("启动", .1, 200, Draft.Compressor.AttackMs, v => vm.Change(s => s.Compressor.AttackMs = v, true), "ms", .1, enabled),
            Parameter("释放", 10, 2000, Draft.Compressor.ReleaseMs, v => vm.Change(s => s.Compressor.ReleaseMs = v, true), "ms", 1, enabled),
            Parameter("补偿", 0, 18, Draft.Compressor.MakeupDb, v => vm.Change(s => s.Compressor.MakeupDb = v, true), "dB", .1, enabled)));
        details.Children.Add(Card("柔和扩展器 / 噪声门", "保持与较长释放有助于保留句尾。",
            Parameter("门限", -90, -10, Draft.Gate.ThresholdDb, v => vm.Change(s => s.Gate.ThresholdDb = v, true), "dB", .5, enabled),
            Parameter("比例", 1, 10, Draft.Gate.Ratio, v => vm.Change(s => s.Gate.Ratio = v, true), ":1", .1, enabled),
            Parameter("启动", .1, 100, Draft.Gate.AttackMs, v => vm.Change(s => s.Gate.AttackMs = v, true), "ms", .1, enabled),
            Parameter("保持", 0, 1000, Draft.Gate.HoldMs, v => vm.Change(s => s.Gate.HoldMs = v, true), "ms", 1, enabled),
            Parameter("释放", 20, 2000, Draft.Gate.ReleaseMs, v => vm.Change(s => s.Gate.ReleaseMs = v, true), "ms", 1, enabled),
            Parameter("最大衰减", 0, 60, Draft.Gate.RangeDb, v => vm.Change(s => s.Gate.RangeDb = v, true), "dB", .5, enabled, readValue: () => Draft.Gate.RangeDb)));
        details.Children.Add(BuildCreativeDetails(enabled));
        details.Children.Add(Card("限幅保护", "采样峰值保护，不能恢复麦克风硬件已经削波的声音。",
            Parameter("释放", 10, 1000, Draft.Limiter.ReleaseMs, v => vm.Change(s => s.Limiter.ReleaseMs = v, true), "ms", 1, enabled)));
        Add(new Expander { Header = Loc.T("高级功能", "Advanced"), Foreground = Ink, Content = details, Margin = new Thickness(0, 8, 0, 14) });
    }

    private Border BuildCreativeCard(bool enabled)
    {
        var active = CreativeEnabled(Draft.Creative);
        var themes = new (string Id, string Name)[] { ("warm", Loc.CreativeTheme("warm")), ("crunch", Loc.CreativeTheme("crunch")), ("robot", Loc.CreativeTheme("robot")), ("echo", Loc.CreativeTheme("echo")), ("space", Loc.CreativeTheme("space")), ("custom", Loc.CreativeTheme("custom")) };
        var picker = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 4, 0, 12), IsEnabled = enabled };
        foreach (var theme in themes) picker.Items.Add(new ComboBoxItem { Content = theme.Name, Tag = theme.Id });
        picker.SelectedIndex = Math.Max(0, Array.FindIndex(themes, t => t.Id == State.CreativeThemeId));
        RegisterSoundControl(picker, () => picker.SelectedIndex = Math.Max(0, Array.FindIndex(themes, t => t.Id == State.CreativeThemeId)));
        picker.SelectionChanged += (_, _) =>
        {
            if (building || picker.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
            State.CreativeThemeId = id;
            if (id != "custom") vm.Change(s => SetCreativeTheme(s.Creative, id, .5, CreativeEnabled(s.Creative)));
            else { UpdateChrome(); SaveSoon(); }
        };
        var amount = Parameter("主题调节", 0, 100, CreativeAmount(Draft.Creative, State.CreativeThemeId) * 100, value => vm.Change(s => SetCreativeTheme(s.Creative, State.CreativeThemeId, value / 100, CreativeEnabled(s.Creative)), true), "%", 1, enabled && State.CreativeThemeId != "custom", readValue: () => CreativeAmount(Draft.Creative, State.CreativeThemeId) * 100);
        RegisterSoundControl(amount, () => amount.IsEnabled = enabled && State.CreativeThemeId != "custom");
        return Card("创意声音", "主题使用现有失真、环形调制、回声和混响。",
            picker,
            ButtonSwitch("创意声音", active, value =>
            {
                vm.Change(s =>
                {
                    if (!value) { var mask = CreativeMask(s.Creative); if (mask != 0) State.CreativeEnabledMask = mask; DisableCreative(s.Creative); }
                    else if (State.CreativeThemeId == "custom") EnableCreative(s.Creative, State.CreativeEnabledMask is > 0 and <= 15 ? State.CreativeEnabledMask : 1);
                    else SetCreativeTheme(s.Creative, State.CreativeThemeId, CreativeAmount(s.Creative, State.CreativeThemeId), true);
                });
            }, enabled, readValue: () => CreativeEnabled(Draft.Creative)), amount);
    }

    private Border BuildCreativeDetails(bool enabled)
    {
        void Change(Action<CreativeSettings> change) { State.CreativeThemeId = "custom"; vm.Change(s => change(s.Creative), true); }
        return Card("创意声音", "机器人为环形调制。自然音高与共振峰变声在后续阶段实现。",
            ButtonSwitch("软饱和", Draft.Creative.SaturationEnabled, v => Change(s => s.SaturationEnabled = v), enabled, readValue: () => Draft.Creative.SaturationEnabled),
            Parameter("饱和驱动", 0, 24, Draft.Creative.DriveDb, v => Change(s => s.DriveDb = v), "dB", .1, enabled, readValue: () => Draft.Creative.DriveDb),
            ButtonSwitch("机器人", Draft.Creative.RobotEnabled, v => Change(s => s.RobotEnabled = v), enabled, readValue: () => Draft.Creative.RobotEnabled),
            Parameter("调制频率", 20, 300, Draft.Creative.RobotFrequencyHz, v => Change(s => s.RobotFrequencyHz = v), "Hz", 1, enabled, readValue: () => Draft.Creative.RobotFrequencyHz),
            ButtonSwitch("回声", Draft.Creative.DelayEnabled, v => Change(s => s.DelayEnabled = v), enabled, readValue: () => Draft.Creative.DelayEnabled),
            Parameter("回声间隔", 20, 1000, Draft.Creative.DelayMs, v => Change(s => s.DelayMs = v), "ms", 1, enabled, readValue: () => Draft.Creative.DelayMs),
            Parameter("回声混合", 0, .6, Draft.Creative.DelayMix, v => Change(s => s.DelayMix = v), "", .01, enabled, readValue: () => Draft.Creative.DelayMix),
            Parameter("回声反馈", 0, .75, Draft.Creative.DelayFeedback, v => Change(s => s.DelayFeedback = v), "", .01, enabled, readValue: () => Draft.Creative.DelayFeedback),
            ButtonSwitch("混响", Draft.Creative.ReverbEnabled, v => Change(s => s.ReverbEnabled = v), enabled, readValue: () => Draft.Creative.ReverbEnabled),
            Parameter("混响混合", 0, .6, Draft.Creative.ReverbMix, v => Change(s => s.ReverbMix = v), "", .01, enabled, readValue: () => Draft.Creative.ReverbMix));
    }

    private static bool CreativeEnabled(CreativeSettings c) => c.SaturationEnabled || c.RobotEnabled || c.DelayEnabled || c.ReverbEnabled;
    private static int CreativeMask(CreativeSettings c) => (c.SaturationEnabled ? 1 : 0) | (c.RobotEnabled ? 2 : 0) | (c.DelayEnabled ? 4 : 0) | (c.ReverbEnabled ? 8 : 0);
    private static void EnableCreative(CreativeSettings c, int mask) { c.SaturationEnabled = (mask & 1) != 0; c.RobotEnabled = (mask & 2) != 0; c.DelayEnabled = (mask & 4) != 0; c.ReverbEnabled = (mask & 8) != 0; }
    private static void DisableCreative(CreativeSettings c) { c.SaturationEnabled = false; c.RobotEnabled = false; c.DelayEnabled = false; c.ReverbEnabled = false; }
    private static double CreativeAmount(CreativeSettings c, string theme) => Math.Clamp(theme switch
    {
        "warm" => c.DriveDb / 12, "crunch" => c.DriveDb / 24, "robot" => (c.RobotFrequencyHz - 20) / 280,
        "echo" => c.DelayMix / .6, "space" => c.ReverbMix / .6, _ => .5
    }, 0, 1);
    private static void SetCreativeTheme(CreativeSettings c, string theme, double amount, bool enabled)
    {
        if (theme == "custom") return;
        DisableCreative(c);
        switch (theme)
        {
            case "crunch": c.DriveDb = 24 * amount; c.SaturationEnabled = enabled; break;
            case "robot": c.RobotFrequencyHz = 20 + 280 * amount; c.RobotEnabled = enabled; break;
            case "echo": c.DelayMs = 180; c.DelayMix = .6 * amount; c.DelayFeedback = .2; c.DelayEnabled = enabled; break;
            case "space": c.ReverbMix = .6 * amount; c.ReverbEnabled = enabled; break;
            default: c.DriveDb = 12 * amount; c.SaturationEnabled = enabled; break;
        }
    }
}
