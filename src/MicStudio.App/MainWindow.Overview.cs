using MicStudio.Core;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private bool stopAfterCalibration;
    private OfflineComparisonResult? comparison;

    private void BuildOverview()
    {
        Header("", "麦克风", "");
        var volume = new StackPanel();
        if (SelectedInput is { IsActive: true } input)
        {
            try
            {
                var systemVolume = devices.GetInputVolume(input.Id);
                volume.Children.Add(Parameter("系统音量", 0, 100, systemVolume.Scalar * 100, v => { devices.SetInputVolume(input.Id, v / 100); CancelCalibration(); InvalidateDiscord(); }, "%", 1));
                volume.Children.Add(ButtonSwitch("麦克风静音", systemVolume.Muted, value => { devices.SetInputMute(input.Id, value); CancelCalibration(); InvalidateDiscord(); }));
            }
            catch (Exception ex) { volume.Children.Add(T("系统音量不可调", 12, Amber)); volume.ToolTip = ex.Message; }
        }
        else volume.Children.Add(T("麦克风未连接", 14, Amber));
        volume.Children.Add(Parameter("输入增益", -24, 30, Draft.InputGainDb, v => vm.Change(s => s.InputGainDb = v, true), "dB"));
        playerButton = B("开始处理", () => { if (engine.IsRunning) StopAudio(); else StartAudio(LocalMode); }, true);
        volume.Children.Add(Buttons(playerButton));
        AdaptiveCards(Card("输入", "系统音量和静音会立即作用于所选麦克风，包括其他应用。", volume), Card("电平", "开始后显示真实峰值；采集输入可能包含 Windows 系统效果。", MeterBlock(false), LocalMode ? MeterBlock(true) : T("APO 音频生效待验证", 12, Amber)));
        var quick = new WrapPanel();
        foreach (var button in new[] { B("均衡器", () => ShowPage(1)), B("效果", () => ShowPage(2)), B("另存预设", SavePreset), B("连接", () => ShowPage(4)) }) quick.Children.Add(button);
        var tone = Card("音色", null, BuildPresetSelector(false), quick, T(LocalMode ? "其他软件的输入选：CABLE Output" : "系统 APO 需在设备页更新配置", 13, Accent));
        Add(tone);
        Add(new Expander { Header = "增益校准", Foreground = Ink, Content = CalibrationControls(), Margin = new(0, 4, 0, 6) });
        Add(new Expander { Header = "录音与比较", Foreground = Ink, Content = RecordingControls(), Margin = new(0, 6, 0, 6) });
    }

    private void AdaptiveCards(params Border[] cards)
    {
        var panel = new WrapPanel { Margin = new(0, 0, -14, 0) };
        foreach (var card in cards) { card.Margin = new(0, 0, 14, 14); panel.Children.Add(card); }
        void Resize() { var w = Math.Max(100, panel.ActualWidth); foreach (var c in cards) c.Width = w >= 730 ? (w - 28) / 2 : w - 14; }
        panel.SizeChanged += (_, _) => Resize(); Add(panel);
    }

    private StackPanel MeterBlock(bool output)
    {
        var p = new StackPanel { Margin = new(0, 4, 0, 10) };
        p.Children.Add(T(output ? "处理后" : "采集输入", 12, Muted));
        var value = T("—", 27, Accent, FontWeights.Light);
        var bar = new ProgressBar { Minimum = -60, Maximum = 0, Value = -60, Height = 6, Foreground = Accent, Background = new SolidColorBrush(Color.FromRgb(46, 57, 59)), BorderThickness = new(0), IsIndeterminate = false };
        p.Children.Add(value); p.Children.Add(bar);
        meters.Add((value, bar, output)); return p;
    }
    private void ClearMeters()
    {
        foreach (var m in meters) { m.Text.Text = "—"; m.Text.Foreground = Accent; m.Bar.Value = -60; }
    }
    private void UpdateMeters(AudioMeter m)
    {
        lastMeter = m;
        foreach (var meter in meters)
        {
            var peak = meter.Output ? m.OutputPeakDb : m.InputPeakDb;
            var rms = meter.Output ? m.OutputRmsDb : m.InputRmsDb;
            meter.Text.Text = (peak <= -150 ? "−∞" : peak.ToString("0.0")) + " dBFS";
            meter.Text.ToolTip = $"峰值 {peak:0.0} dBFS / RMS {rms:0.0} dBFS；{m.SampleRate} Hz / {m.Channels}ch";
            meter.Text.Foreground = peak > -1 ? Amber : Accent;
            meter.Bar.Value = Math.Clamp(peak, -60, 0);
        }
        if (calibrating && m.InputPeakDb > -65 && double.IsFinite(m.InputPeakDb)) calibrationPeaks.Add(LocalMode ? m.InputPeakDb : m.OutputPeakDb);
        CaptureFooter.Text = (LocalMode ? "●  处理中" : "●  测量中") + (m.ClippedSamples > 0 ? " · 检测到削波" : "");
    }
    private UIElement SessionControls()
    {
        var label = T("", 11, Muted); liveLabels.Add(label);
        var p = new StackPanel(); p.Children.Add(label);
        p.Children.Add(Buttons(B("开始电平测试", () => StartAudio(), true), B("停止所有本地音频", StopAudio), B("开始发送线路", () => StartAudio(true))));
        p.Children.Add(T("“开始电平测试”只采集，不自动发送线路；独立耳机监听默认关闭。", 11, Muted)); return p;
    }
    private void UpdateLiveLabels()
    {
        foreach (var label in liveLabels) label.Text = engine.IsRunning ? engine.IsRecording ? "正在录音" : "运行中" : "未运行";
    }
    private UIElement CalibrationControls()
    {
        var p = new StackPanel { Margin = new(0, 10, 0, 10) };
        p.Children.Add(Buttons(B("校准 · 6 秒", BeginCalibration), B("采用建议", () =>
        {
            if (suggestedGain is null) { Notice("请先正常讲话校准。噪声或静音不足以确定合适的增益。"); return; }
            vm.Change(s => s.InputGainDb = suggestedGain.Value); ShowPage(currentPage); Notice(engine.IsRunning ? "已采用建议增益，声音实时更新。" : "已采用建议增益，下次开始时使用。");
        })));
        if (suggestedGain is { } suggested) p.Children.Add(T($"建议：{suggested:+0.0;-0.0;0} dB", 13, Accent));
        return p;
    }
    private void BeginCalibration()
    {
        if (calibrating) return;
        if (!LocalMode) { Notice("系统 APO 路径可能已经包含增益，无法把采集值当作原声。请切换到本地 DSP，并移除重复 APO 后再校准。"); return; }
        stopAfterCalibration = !engine.IsRunning;
        if (!engine.IsRunning) StartAudio();
        if (!engine.IsRunning) return;
        calibrationPeaks.Clear(); calibrating = true; suggestedGain = null;
        calibrationTimer.Start(); Notice("正在校准，请用日常音量连续讲话 6 秒。此过程不保存录音。");
    }
    private void CancelCalibration()
    {
        calibrating = false; calibrationTimer.Stop(); calibrationPeaks.Clear(); suggestedGain = null;
    }
    private void CompleteCalibration()
    {
        calibrationTimer.Stop(); calibrating = false;
        if (stopAfterCalibration) StopAudio();
        if (calibrationPeaks.Count < 15) { Notice("有效讲话电平不足，未生成增益建议。请确认设备、距离并重新讲话测试。"); return; }
        calibrationPeaks.Sort();
        var speechPeak = calibrationPeaks[(int)Math.Floor((calibrationPeaks.Count - 1) * .9)];
        suggestedGain = Math.Round(Math.Clamp(-12 - speechPeak, -24, 30), 1);
        ShowPage(currentPage);
        Notice($"测得较响讲话峰值约 {speechPeak:0.0} dBFS；建议输入增益 {suggestedGain:+0.0;-0.0;0} dB。点击“采用建议”使用，真实听感仍需试听。");
    }
    private UIElement RecordingControls()
    {
        var p = new StackPanel();
        p.Children.Add(Buttons(B("录制短试听 · 30 秒", () =>
        {
            if (!engine.IsRunning) { Notice("请先开始电平测试或发送线路。"); return; }
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "保存本地短录音", Filter = "WAV 音频|*.wav", FileName = "MicStudio-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav" };
            if (dialog.ShowDialog(this) == true) { if (File.Exists(dialog.FileName)) { Notice("请使用新的文件名，防止覆盖已有录音。"); return; } engine.StartRecording(dialog.FileName, 30); Notice("正在录制当前采集路径，最长 30 秒。录音仅保存本机。"); UpdateLiveLabels(); }
        }), B("结束录音", engine.StopRecording), B("用耳机回放", () =>
        {
            if (engine.LastRecordingPath is null) { Notice("尚无本次会话的试听录音。"); return; }
            if (State.PlaybackOutputId is null || !headphones) { Notice("请在连接页明确选择物理输出，并确认使用耳机，再回放。"); return; }
            engine.PlayRecording(engine.LastRecordingPath, State.PlaybackOutputId);
        }), B("停止回放", engine.StopPlayback)));
        p.Children.Add(Buttons(B("录制 A/B 源 · 30 秒", () =>
        {
            if (!engine.IsRunning) { Notice("先开始处理或测量。"); return; }
            var d = new Microsoft.Win32.SaveFileDialog { Title = "保存 DSP 前的比较源", Filter = "WAV|*.wav", FileName = "MicStudio-source-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav" };
            if (d.ShowDialog(this) != true) return;
            comparison = null; engine.StartRecording(d.FileName, 30, recordSource: true); Notice("正在录制比较源，最长 30 秒。");
        }), AsyncB("生成同源 A/B", async () =>
        {
            if (engine.IsRunning) { Notice("先停止采集。"); return; }
            var source = engine.LastRecordingPath;
            if (source is null || !engine.LastRecordingCapturedBeforeLocalDsp) { Notice("先录制 A/B 源音频；已处理的录音不重复处理。"); return; }
            var d = new Microsoft.Win32.SaveFileDialog { Title = "保存 A/B 处理后音频", Filter = "WAV|*.wav", FileName = "MicStudio-comparison.wav" };
            if (d.ShowDialog(this) != true) return;
            var result = await OfflineAudioProcessor.ProcessFileAsync(source, d.FileName, Draft.Clone(), true, cancellationToken: lifetime.Token);
            if (closed) return;
            comparison = result;
            Notice((result.LoudnessMatched ? "A/B 已生成：同一次录音，整段 RMS 匹配（非 LUFS）。" : "A/B 已生成；接近静音，未作 RMS 匹配。") + result.Warning);
        }), B("A · 参考", () => PlayComparison(false)), B("B · 效果", () => PlayComparison(true))));
        return p;
    }
    private void PlayComparison(bool processed)
    {
        if (comparison is null) { Notice("先生成同源 A/B。"); return; }
        if (State.PlaybackOutputId is null || !headphones) { Notice("先在设备页选择并确认耳机。"); return; }
        engine.PlayRecording(processed ? comparison.ProcessedPath : comparison.ReferencePath, State.PlaybackOutputId);
    }
}
