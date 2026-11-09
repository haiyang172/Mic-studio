using MicStudio.Core;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private OfflineComparisonResult? comparison;
    private (string Path, int SessionId)? tuningSourceCapture;

    private void BuildOverview()
    {
        Header("", "麦克风", "");
        var volume = new StackPanel();
        UIElement? systemMute = null;
        if (SelectedInput is { IsActive: true } input)
        {
            try
            {
                var systemVolume = UiSmoke ? new VolumeInfo(smokeVolume, smokeInputMuted, -96, 0, 1, 0) : devices.GetInputVolume(input.Id);
                volume.Children.Add(Parameter("系统音量", 0, 100, systemVolume.Scalar * 100, v =>
                {
                    if (State.InputDeviceId != input.Id) return;
                    SetSelectedInputVolume(input.Id, v / 100);
                    CancelCalibration();
                    InvalidateDiscord();
                }, "%", 1));
                systemMute = ButtonSwitch("麦克风静音", systemVolume.Muted, value =>
                {
                    if (State.InputDeviceId != input.Id) return;
                    SetSelectedInputMute(input.Id, value);
                    CancelCalibration();
                    InvalidateDiscord();
                });
            }
            catch (Exception ex) { volume.Children.Add(T("系统音量不可调", 12, Amber)); volume.ToolTip = ex.Message; }
        }
        else volume.Children.Add(T("麦克风未连接", 14, Amber));
        volume.Children.Add(Parameter("输入增益", -24, 30, Draft.InputGainDb, v => vm.Change(s => s.InputGainDb = v, true), "dB", readValue: () => Draft.InputGainDb));
        playerButton = B("开始处理", () => { if (ProcessingActive) StopAudio(); else StartAudio(LocalMode); }, true);
        playerButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        playerButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        playerButton.FontSize = 15;
        playerButton.MinHeight = 46;
        playerButton.Margin = new(0, 5, 0, 10);
        volume.Children.Add(playerButton);
        if (LocalMode) volume.Children.Add(TransmitControls());
        var route = CableRouteResolver.Resolve(inputs, outputs, State.CableOutputId);
        var routeText = LocalMode
            ? route.IsReady
                ? Loc.T("其他软件的输入选：", "In the other app, select input: ") + route.Receive!.Name
                : Loc.T("虚拟线路未就绪，可在设备页检查驱动", "Virtual cable is not ready; check the driver on Devices")
            : NativeMode ? Loc.T("自研 APO 预览：仅增益、EQ、限幅；系统音频待验证", "Native APO preview: gain, EQ and limiter only; system audio unverified") : Loc.T("系统 APO 需在设备页更新配置", "Update the system APO configuration on Devices");
        var sound = new StackPanel();
        sound.Children.Add(BuildPresetSelector(false));
        sound.Children.Add(MeterBlock(false));
        sound.Children.Add(LocalMode ? MeterBlock(true) : T("APO 音频生效待验证", 12, Amber));
        sound.Children.Add(T(routeText, 12, route.IsReady || !LocalMode ? Muted : Amber));
        sound.Children.Add(B(Loc.T("查看线路", "View signal path"), () => ShowPage(4)));
        AdaptiveCards(
            Card("输入", "系统音量和静音会立即作用于所选麦克风，包括其他应用。", volume),
            Card("音色", "开始后显示真实峰值；采集输入可能包含 Windows 系统效果。", sound));

        var advanced = new StackPanel();
        if (systemMute != null)
            advanced.Children.Add(Card(Loc.T("系统麦克风", "System microphone"), null,
                systemMute, T(Loc.T("直接控制所选 Windows 麦克风，影响其他应用。发送静音只控制本软件的虚拟线路。", "Controls the selected Windows microphone for all apps. Mute send controls only this app's virtual cable."), 12, Muted)));
        advanced.Children.Add(Card(Loc.T("调音引导", "Tuning guide"), null, TuningGuideControls()));
        advanced.Children.Add(Card(Loc.T("录音与比较", "Recording and comparison"), null, RecordingControls()));
        advanced.Children.Add(Buttons(B("另存预设", SavePreset)));
        Add(new Expander { Header = Loc.T("高级功能", "Advanced"), Foreground = Ink, Content = advanced, Margin = new(0, 4, 0, 8) });
    }

    private void AdaptiveCards(Border left, Border right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var secondColumn = new ColumnDefinition { Width = new(0) };
        grid.ColumnDefinitions.Add(secondColumn);
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        grid.SizeChanged += (_, size) =>
        {
            if (!size.WidthChanged) return;
            var twoColumns = size.NewSize.Width >= 730;
            secondColumn.Width = twoColumns ? new(1, GridUnitType.Star) : new(0);
            Grid.SetRow(right, twoColumns ? 0 : 1);
            Grid.SetColumn(right, twoColumns ? 1 : 0);
            left.Margin = new(0, 0, twoColumns ? 7 : 0, 14);
            right.Margin = new(twoColumns ? 7 : 0, 0, 0, 14);
        };
        Add(grid);
    }

    private StackPanel MeterBlock(bool output)
    {
        var p = new StackPanel { Margin = new(0, 4, 0, 12) };
        var header = new Grid();
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(T(output ? "处理后" : "采集输入", 12, Muted, margin: new(0)));
        var value = T("—", 17, Accent, FontWeights.Medium, new(10, 0, 0, 0));
        Grid.SetColumn(value, 1); header.Children.Add(value);
        var bar = new ProgressBar { Minimum = -60, Maximum = 0, Value = -60, Height = 6, Margin = new(0, 8, 0, 0), Foreground = Accent, Background = (Brush)FindResource("Line"), BorderThickness = new(0), IsIndeterminate = false };
        p.Children.Add(header); p.Children.Add(bar);
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
            meter.Text.ToolTip = Loc.F("峰值 {0:0.0} dBFS / RMS {1:0.0} dBFS；{2} Hz / {3}ch", "Peak {0:0.0} dBFS / RMS {1:0.0} dBFS; {2} Hz / {3}ch", peak, rms, m.SampleRate, m.Channels);
            meter.Text.Foreground = peak > -1 ? Amber : Accent;
            meter.Bar.Value = Math.Clamp(peak, -60, 0);
        }
        if (calibrating && m.InputPeakDb > -65 && double.IsFinite(m.InputPeakDb)) calibrationPeaks.Add(LocalMode ? m.InputPeakDb : m.OutputPeakDb);
        CaptureFooter.Text = engine.FeedbackProtectionMuted ? Loc.T("啸叫保护：已静音", "Feedback protection: muted") : (LocalMode ? Loc.T("●  处理中", "●  Processing") : Loc.T("●  测量中", "●  Measuring")) + (m.ClippedSamples > 0 ? Loc.T(" · 检测到削波", " · Clipping detected") : "");
        CaptureFooter.Foreground = engine.FeedbackProtectionMuted ? Amber : Accent;
        UpdateTransmitChrome();
        UpdateSignalPath();
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
        foreach (var label in liveLabels) label.Text = engine.IsRunning ? engine.IsRecording ? Loc.T("正在录音", "Recording") : Loc.T("运行中", "Running") : Loc.T("未运行", "Not running");
    }
    private UIElement RecordingControls()
    {
        var p = new StackPanel();
        p.Children.Add(Buttons(B("录制短试听 · 30 秒", () =>
        {
            if (!engine.IsRunning) { Notice("请先开始电平测试或发送线路。"); return; }
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("保存本地短录音", "Save local short recording"), Filter = Loc.T("WAV 音频|*.wav", "WAV audio|*.wav"), FileName = "MicStudio-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav" };
            if (dialog.ShowDialog(this) == true) { if (File.Exists(dialog.FileName)) { Notice("请使用新的文件名，防止覆盖已有录音。"); return; } engine.StartRecording(dialog.FileName, 30); Notice("正在录制当前采集路径，最长 30 秒。录音仅保存本机。"); UpdateLiveLabels(); }
        }), B("结束录音", engine.StopRecording), B("用耳机回放", () =>
        {
            if (engine.LastRecordingPath is null) { Notice("尚无本次会话的试听录音。"); return; }
            if (State.PlaybackOutputId is null || !headphones) { Notice("请在连接页明确选择物理输出，并确认使用耳机，再回放。"); return; }
            CancelDeviceRecovery("playback_started");
            StopLoopTuningPreview();
            engine.PlayRecording(engine.LastRecordingPath, State.PlaybackOutputId);
        }), B("停止回放", () =>
        {
            CancelLoopTuningPreparation();
            engine.StopPlayback();
            UpdateLoopTuningChrome();
        })));
        var recordSource = B("录制 A/B 源 · 30 秒", RecordTuningSource);
        recordSource.ToolTip = Loc.T("确认保存后录制。若处理已停止，麦克风会打开并采集试听素材。", "Recording begins after you confirm the file. If processing is stopped, the microphone opens to capture the sample.");
        p.Children.Add(Buttons(recordSource));
        p.Children.Add(LoopTuningControls());
        p.Children.Add(Buttons(AsyncB("生成同源 A/B", async () =>
        {
            if (engine.IsRunning) { Notice("先停止采集。"); return; }
            var source = engine.LastRecordingPath;
            if (source is null || !engine.LastRecordingCapturedBeforeLocalDsp) { Notice("先录制 A/B 源音频；已处理的录音不重复处理。"); return; }
            var d = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("保存 A/B 处理后音频", "Save the processed A/B audio"), Filter = "WAV|*.wav", FileName = "MicStudio-comparison.wav" };
            if (d.ShowDialog(this) != true) return;
            var result = await OfflineAudioProcessor.ProcessFileAsync(source, d.FileName, Draft.Clone(), true, cancellationToken: lifetime.Token);
            if (closed) return;
            comparison = result;
            Notice((result.LoudnessMatched ? Loc.T("A/B 已生成：同一次录音，整段 RMS 匹配（非 LUFS）。", "A/B created from one recording with whole-file RMS matching (not LUFS).") : Loc.T("A/B 已生成；接近静音，未作 RMS 匹配。", "A/B created; the recording was near silence, so RMS was not matched.")) + result.Warning);
        }), B("A · 参考", () => PlayComparison(false)), B("B · 效果", () => PlayComparison(true))));
        return p;
    }

    private void RecordTuningSource()
    {
        if (UiSmoke) return;
        if (!LocalMode)
        {
            Notice(Loc.T("请切换到本地处理，再录制 A/B 源。", "Switch to local processing before recording an A/B source."));
            return;
        }
        if (SelectedInput is not { IsActive: true })
        {
            Notice(Loc.T("请选择已连接的麦克风。", "Select a connected microphone."));
            return;
        }
        if (calibrating || engine.IsRecording || engine.IsPlaying || loopPreparation != null)
        {
            Notice(Loc.T("请先完成当前测量、录音或试听。", "Finish the current measurement, recording or preview first."));
            return;
        }
        var originalSession = engine.SessionId;
        var inputId = State.InputDeviceId;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = engine.IsRunning
                ? Loc.T("保存 DSP 前的比较源", "Save the pre-DSP comparison source")
                : Loc.T("确认保存后打开麦克风并录音", "Confirm the file to open the microphone and record"),
            Filter = Loc.T("WAV 音频|*.wav", "WAV audio|*.wav"),
            OverwritePrompt = false,
            FileName = "MicStudio-source-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav"
        };
        if (dialog.ShowDialog(this) != true) return;
        if (closed || !LocalMode || originalSession != engine.SessionId || inputId != State.InputDeviceId ||
            calibrating || engine.IsRecording || engine.IsPlaying || loopPreparation != null)
        {
            if (!closed) Notice(Loc.T("音频会话已改变，请重新开始录制。", "The audio session changed. Start the recording again."));
            return;
        }
        var path = Path.GetFullPath(dialog.FileName);
        if (File.Exists(path))
        {
            Notice(Loc.T("请选择新的文件名。", "Choose a new file name."));
            return;
        }

        var ownsCapture = !engine.IsRunning;
        if (ownsCapture) StartAudio(captureOnly: true);
        if (!engine.IsRunning) return;
        var session = engine.SessionId;
        try
        {
            engine.StartRecording(path, 30, recordSource: true);
            tuningSourceCapture = ownsCapture ? (path, session) : null;
            comparison = null;
            Notice(Loc.T("正在录制 A/B 源，最长 30 秒。", "Recording an A/B source for up to 30 seconds."));
            UpdateLiveLabels();
        }
        catch
        {
            if (ownsCapture && engine.IsRunning && engine.SessionId == session) StopAudio();
            throw;
        }
    }

    private void CompleteTuningSourceCapture(RecordingResult recording)
    {
        if (tuningSourceCapture is not { } capture || recording.SessionId != capture.SessionId ||
            !string.Equals(recording.Path, capture.Path, StringComparison.OrdinalIgnoreCase)) return;
        tuningSourceCapture = null;
        if (engine.IsRunning && engine.SessionId == capture.SessionId) StopAudio();
    }

    private void PlayComparison(bool processed)
    {
        if (comparison is null) { Notice("先生成同源 A/B。"); return; }
        if (State.PlaybackOutputId is null || !headphones) { Notice("先在设备页选择并确认耳机。"); return; }
        CancelDeviceRecovery("playback_started");
        StopLoopTuningPreview();
        engine.PlayRecording(processed ? comparison.ProcessedPath : comparison.ReferencePath, State.PlaybackOutputId);
    }
}
