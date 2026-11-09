using System.Threading;
using System.Windows.Automation;
using MicStudio.Core;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private CancellationTokenSource? loopPreparation;
    private AudioDeviceInfo? loopOutput;
    private bool loopProcessed = true;
    private bool loopMatchLoudness;
    private double? loopSuggestedGain;
    private double loopDraftGain;

    private void InitializeLoopTuning()
    {
        LoopStopButton.Click += (_, _) => Safe(StopLoopTuningPreview);
        LoopCompareButton.Click += (_, _) => Safe(() =>
        {
            if (engine.LoopTuningSessionId is not { } session) return;
            if (engine.TrySetLoopTuningProcessed(!loopProcessed, session)) loopProcessed = !loopProcessed;
            UpdateLoopTuningChrome();
        });
        LoopMatchButton.Click += (_, _) => Safe(() =>
        {
            if (engine.LoopTuningSessionId is not { } session) return;
            if (engine.TrySetLoopTuningLoudnessMatching(!loopMatchLoudness, session))
                loopMatchLoudness = !loopMatchLoudness;
            UpdateLoopTuningChrome();
        });
        engine.LoopTuningStopped += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (closed || engine.IsLoopTuning || loopPreparation != null) return;
            loopOutput = null;
            loopSuggestedGain = null;
            UpdateLoopTuningChrome();
        });
    }

    private UIElement LoopTuningControls()
    {
        var start = AsyncB(Loc.T("循环调音", "Tune in a loop"), () => StartLoopTuningPreviewAsync());
        AutomationProperties.SetAutomationId(start, "loop-tuning-start");
        start.IsEnabled = LocalMode;
        start.ToolTip = Loc.T("录好 A/B 源并停止处理，再用耳机试听。播放时可在任意页面调音。",
            "Record an A/B source and stop processing, then listen through headphones. Adjust your sound on any page during playback.");
        return Buttons(start);
    }

    private async Task StartLoopTuningPreviewAsync(double? suggestedGainDb = null)
    {
        if (closed || loopPreparation != null) return;
        if (!LocalMode)
        {
            Notice(Loc.T("请在设备页选择虚拟输入，再进行循环调音。", "Choose Virtual input on Devices to tune in a loop."));
            return;
        }
        if (ProcessingActive || engine.IsRecording)
        {
            Notice(Loc.T("请先结束录音并停止处理，再试听。", "Finish recording and stop processing before listening."));
            return;
        }
        if (engine.LastRecordingPath == null || !engine.LastRecordingCapturedBeforeLocalDsp)
        {
            Notice(Loc.T("请先录制一段 A/B 源。", "Record an A/B source first."));
            return;
        }
        if (!headphones || outputs.FirstOrDefault(IsLoopOutput) is not { } output)
        {
            Notice(Loc.T("请在设备页选择耳机并确认佩戴。", "Choose headphones on Devices and confirm you are wearing them."));
            return;
        }
        if (suggestedGainDb != null && Draft.Bypass)
        {
            Notice(Loc.T("请开启效果开关，再试听建议增益。", "Turn on Effects to hear the suggested gain."));
            return;
        }
        if (UiSmoke) return;

        CancelDeviceRecovery("playback_started");
        StopLoopTuningPreview();
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loopPreparation = preparation;
        loopOutput = output;
        loopProcessed = true;
        loopMatchLoudness = suggestedGainDb == null;
        loopSuggestedGain = suggestedGainDb;
        loopDraftGain = Draft.InputGainDb;
        UpdateLoopTuningChrome();
        try
        {
            var startedSession = await engine.StartLoopTuningAsync(output.Id, true, LoopTuningSettings(),
                matchLoudness: loopMatchLoudness, cancellationToken: preparation.Token);
            if (closed || loopPreparation != preparation) return;
            if (startedSession is { } session && engine.LoopTuningSessionId == session) FlushLoopTuningSettings();
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested) { }
        finally
        {
            if (loopPreparation == preparation)
            {
                loopPreparation = null;
                if (!engine.IsLoopTuning)
                {
                    loopOutput = null;
                    loopSuggestedGain = null;
                }
                if (!closed) UpdateLoopTuningChrome();
            }
        }
    }

    private bool IsLoopOutput(AudioDeviceInfo device) => device.Id == State.PlaybackOutputId && device.IsActive &&
        !AudioDeviceService.IsVirtualCableEndpoint(device.Name);

    private void CheckLoopTuningOutput()
    {
        if (loopOutput is not { } output) return;
        if (headphones && outputs.Any(d => IsLoopOutput(d) && d.SampleRate == output.SampleRate && d.Channels == output.Channels)) return;
        StopLoopTuningPreview();
        Notice(Loc.T("耳机已断开或格式改变，循环试听已停止。", "Headphones disconnected or changed format. Loop playback stopped."));
    }

    private AudioSettings LoopTuningSettings()
    {
        var settings = Draft.Clone();
        if (loopSuggestedGain is { } gain)
        {
            if (settings.InputGainDb == loopDraftGain) settings.InputGainDb = gain;
            else loopSuggestedGain = null;
        }
        return settings;
    }

    private void FlushLoopTuningSettings()
    {
        if (engine.LoopTuningSessionId is not { } session) return;
        try { engine.TryUpdateLoopTuningSettings(LoopTuningSettings(), session); }
        catch (Exception ex)
        {
            Notice(Loc.T("循环试听保留上一次有效设置。", "Loop playback is using the last valid settings.") + " " + ex.Message);
        }
        UpdateLoopTuningChrome();
    }

    private void CancelLoopTuningPreparation()
    {
        var preparation = loopPreparation;
        loopPreparation = null;
        preparation?.Cancel();
        loopOutput = null;
        loopSuggestedGain = null;
    }

    private void StopLoopTuningPreview()
    {
        var shouldStop = loopPreparation != null || engine.IsLoopTuning;
        CancelLoopTuningPreparation();
        try { if (shouldStop) engine.StopPlayback(); }
        finally { if (!closed) UpdateLoopTuningChrome(); }
    }

    private void UpdateLoopTuningChrome()
    {
        var active = engine.IsLoopTuning;
        LoopTuningBar.Visibility = active || loopPreparation != null ? Visibility.Visible : Visibility.Collapsed;
        LoopTuningStatus.Text = !active ? Loc.T("正在准备试听…", "Preparing preview…") : !loopProcessed
            ? Loc.T("循环试听 · A 原声", "Loop preview · A Original")
            : loopSuggestedGain is { } gain
                ? Loc.F("试听建议 · {0:+0.0;-0.0;0} dB", "Suggested gain preview · {0:+0.0;-0.0;0} dB", gain)
                : Loc.T("循环试听 · B 效果", "Loop preview · B Effects");
        LoopCompareButton.Content = loopProcessed ? Loc.T("切到 A 原声", "Switch to A Original") : Loc.T("切到 B 效果", "Switch to B Effects");
        AutomationProperties.SetName(LoopCompareButton, (string)LoopCompareButton.Content);
        LoopCompareButton.IsEnabled = active;
        LoopMatchButton.Content = loopMatchLoudness ? Loc.T("配平：开", "Match volume: On") : Loc.T("配平：关", "Match volume: Off");
        AutomationProperties.SetName(LoopMatchButton, (string)LoopMatchButton.Content);
        LoopMatchButton.ToolTip = Loc.T("关闭配平可听出增益带来的音量差。", "Turn off matching to hear the change in volume from gain.");
        LoopMatchButton.IsEnabled = active;
        LoopStopButton.Content = active ? Loc.T("停止试听", "Stop preview") : Loc.T("取消", "Cancel");
        AutomationProperties.SetName(LoopStopButton, (string)LoopStopButton.Content);
    }
}
