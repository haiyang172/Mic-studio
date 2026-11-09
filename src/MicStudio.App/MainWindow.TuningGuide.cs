using System.Windows.Automation;

namespace MicStudio.App;

public partial class MainWindow
{
    private bool stopAfterCalibration;
    private int calibrationSession;

    private UIElement TuningGuideControls()
    {
        var panel = new StackPanel();
        var prompt = T("", 13, Muted);
        var result = T("", 14, Accent);
        var measure = B(Loc.T("测量讲话 · 6 秒", "Measure speech · 6 s"), BeginCalibration);
        var audition = AsyncB(Loc.T("试听建议", "Audition suggestion"), async () =>
        {
            if (LocalMode && !calibrating && suggestedGain is { } gain)
                await StartLoopTuningPreviewAsync(gain);
        });
        var apply = B(Loc.T("采用建议", "Use suggestion"), () =>
        {
            if (!LocalMode || calibrating || suggestedGain is not { } gain) return;
            vm.Change(settings => settings.InputGainDb = gain);
            Notice(Loc.F("已采用输入增益 {0:+0.0;-0.0;0} dB。", "Input gain set to {0:+0.0;-0.0;0} dB.", gain));
        });
        AutomationProperties.SetAutomationId(panel, "tuning-guide");
        AutomationProperties.SetAutomationId(result, "tuning-guide-result");
        AutomationProperties.SetAutomationId(measure, "tuning-guide-measure");
        AutomationProperties.SetAutomationId(audition, "tuning-guide-audition");
        AutomationProperties.SetAutomationId(apply, "tuning-guide-apply");
        var actions = Buttons(audition, apply);
        panel.Children.Add(result);
        panel.Children.Add(prompt);
        panel.Children.Add(Buttons(measure));
        panel.Children.Add(actions);

        void Refresh()
        {
            var suggestion = LocalMode && !calibrating ? suggestedGain : null;
            var hasSuggestion = suggestion.HasValue;
            result.Visibility = actions.Visibility = hasSuggestion ? Visibility.Visible : Visibility.Collapsed;
            result.Text = suggestion is { } gain
                ? Loc.F("建议 {0:+0.0;-0.0;0} dB（当前 {1:+0.0;-0.0;0} dB）", "Suggested {0:+0.0;-0.0;0} dB (current {1:+0.0;-0.0;0} dB)", gain, Draft.InputGainDb)
                : string.Empty;
            measure.IsEnabled = LocalMode && !calibrating && SelectedInput is { IsActive: true };
            apply.IsEnabled = suggestion is { } proposed && Math.Abs(proposed - Draft.InputGainDb) > .001;
            prompt.Text = !LocalMode
                ? Loc.T("请切换到本地处理使用调音引导。", "Switch to local processing to use the tuning guide.")
                : calibrating
                    ? Loc.T("正在测量，请用日常音量继续讲话。", "Measuring. Keep speaking at your usual volume.")
                    : hasSuggestion
                        ? Loc.T("录制 A/B 源并停止处理后，可用耳机试听，再决定是否采用。", "Record an A/B source and stop processing. Listen on headphones before choosing whether to apply the suggestion.")
                        : Loc.T("用日常音量讲话 6 秒，查看建议输入增益。", "Speak at your usual volume for 6 seconds to get an input gain suggestion.");
        }

        Refresh();
        RegisterSoundControl(panel, Refresh);
        return panel;
    }

    private void BeginCalibration()
    {
        if (UiSmoke || !LocalMode || calibrating || SelectedInput is not { IsActive: true }) return;
        if (engine.IsPlaying || engine.IsRecording || loopPreparation != null)
        {
            Notice(Loc.T("请先结束录音或试听，再测量麦克风。", "Finish recording or playback before measuring the microphone."));
            return;
        }
        stopAfterCalibration = !engine.IsRunning;
        if (stopAfterCalibration) StartAudio(captureOnly: true);
        if (!engine.IsRunning) return;
        calibrationSession = engine.SessionId;
        calibrationPeaks.Clear();
        calibrating = true;
        suggestedGain = null;
        calibrationTimer.Start();
        UpdateChrome();
        Notice(Loc.T("请用日常音量连续讲话 6 秒。测量只读取电平。", "Speak at your usual volume for 6 seconds. Measurement reads the audio levels only."));
    }

    private void CancelCalibration()
    {
        var ownsCurrentCapture = calibrating && stopAfterCalibration && engine.IsRunning && engine.SessionId == calibrationSession;
        calibrating = false;
        calibrationTimer.Stop();
        calibrationPeaks.Clear();
        suggestedGain = null;
        if (ownsCurrentCapture) StopAudio();
    }

    private void CompleteCalibration()
    {
        calibrationTimer.Stop();
        if (!calibrating) return;
        if (!engine.IsRunning || engine.SessionId != calibrationSession)
        {
            CancelCalibration();
            UpdateChrome();
            return;
        }
        calibrating = false;
        if (stopAfterCalibration) StopAudio();
        if (calibrationPeaks.Count < 15)
        {
            UpdateChrome();
            Notice(Loc.T("讲话电平不足，请检查麦克风和距离后重新测量。", "Speech level was too low. Check the microphone and distance, then measure again."));
            return;
        }
        calibrationPeaks.Sort();
        var speechPeak = calibrationPeaks[(int)Math.Floor((calibrationPeaks.Count - 1) * .9)];
        suggestedGain = Math.Round(Math.Clamp(-12 - speechPeak, -24, 30), 1);
        UpdateChrome();
        Notice(Loc.F("讲话峰值约 {0:0.0} dBFS，建议输入增益 {1:+0.0;-0.0;0} dB。", "Speech peaked around {0:0.0} dBFS. Suggested input gain is {1:+0.0;-0.0;0} dB.", speechPeak, suggestedGain));
    }
}
