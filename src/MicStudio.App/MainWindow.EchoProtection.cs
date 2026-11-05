using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private sealed record EchoOutputChoice(string? Id, string Name);
    private TextBlock? echoProtectionStatusText;
    private TextBlock? diagnosticSummaryText;

    private void InitializeEchoProtectionNotifications()
    {
        engine.EchoCancellationStatusChanged += (_, status) => Dispatcher.BeginInvoke(() =>
        {
            if (closed || status.SessionId != engine.SessionId || !engine.IsRunning) return;
            UpdateEchoProtectionStatus();
        });
        engine.FeedbackProtectionTriggered += (_, protection) => Dispatcher.BeginInvoke(() =>
        {
            if (closed || protection.SessionId != engine.SessionId || !engine.IsRunning) return;
            UpdateEchoProtectionStatus();
        });
        engine.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() =>
        {
            if (closed || status.SessionId != engine.SessionId || status.IsRunning != engine.IsRunning) return;
            UpdateEchoProtectionStatus();
        });
    }

    private string EchoStatusDescription(EchoCancellationState status)
    {
        if (!LocalMode)
            return Loc.T("外放回声消除和啸叫输出保护作用于虚拟输入线路，不代表当前系统 APO 路径已消除回声。", "Speaker echo cancellation and feedback output protection apply to the virtual-input route; this does not verify echo removal in the current system APO path.");
        if (!engine.IsRunning)
            return State.EchoCancellationEnabled
                ? Loc.T("开始后使用电脑播放声作为本地参考，不保存或上传播放内容。", "Processing uses computer playback as a local reference; playback is never saved or uploaded.")
                : Loc.T("回声消除已关闭；开始处理后啸叫输出保护仍开启。", "Echo cancellation is off; feedback output protection remains enabled when processing starts.");
        if (engine.FeedbackProtectionMuted)
            return Loc.T("啸叫输出保护已锁定静音。先停止外部回放或改用耳机，再停止并重新开始处理。", "Feedback protection has latched the output muted. Stop external playback or use headphones, then stop and restart processing.");
        if (status.IsActive && !status.ReferenceAvailable)
            return Loc.T("等待电脑播放参考；回声环节透传，其他调音与啸叫保护按当前设置运行，尚不能确认回声消除生效。", "Waiting for computer playback reference; the echo stage passes through while other processing and feedback protection follow the current settings. Echo removal is not yet verified.");
        if (status.IsActive)
        {
            var text = Loc.T("播放参考已接入，回声处理运行中；实际效果需试听确认。", "Playback reference connected and echo processing is running; actual results still need listening verification.");
            return status.ReferenceName == null ? text : text + "\n" + Loc.T("播放参考：", "Playback reference: ") + status.ReferenceName;
        }
        if (!State.EchoCancellationEnabled)
            return Loc.T("回声消除已关闭；啸叫输出保护仍开启。", "Echo cancellation is off; feedback output protection stays on.");
        return status.Code switch
        {
            "library_missing" => Loc.T("回声消除组件不可用，回声环节透传；其他调音与啸叫保护按当前设置运行。", "The echo component is unavailable; the echo stage passes through while other processing and feedback protection follow the current settings."),
            "format_unsupported" => Loc.T("当前采样格式不支持回声消除，回声环节透传。", "This audio format does not support echo cancellation; the echo stage passes through."),
            "reference_changed" or "reference_unavailable" => Loc.T("播放参考已改变或不可用，回声消除暂停；请选择设备并重新开始。", "Playback reference changed or is unavailable; echo cancellation is paused. Select a reference and restart processing."),
            "reference_is_virtual" => Loc.T("播放参考不能是虚拟线路，请选择实际扬声器或耳机。", "The playback reference cannot be a virtual cable. Select physical speakers or headphones."),
            "routing_unavailable" or "multiple_references" => Loc.T("无法唯一确认播放参考，请明确选择实际播放设备。", "The playback reference cannot be uniquely identified. Select the actual playback device."),
            "processing_failed" => Loc.T("回声处理返回错误，回声环节透传；其他调音与啸叫保护按当前设置运行。", "Echo processing failed; the echo stage passes through while other processing and feedback protection follow the current settings."),
            _ => Loc.T("回声消除未就绪，回声环节透传；详情见诊断。", "Echo cancellation is not ready; the echo stage passes through. See Diagnostics.")
        };
    }

    private void UpdateEchoProtectionStatus()
    {
        if (echoProtectionStatusText != null)
        {
            var status = engine.EchoCancellationStatus;
            echoProtectionStatusText.Text = EchoStatusDescription(status);
            echoProtectionStatusText.Foreground = engine.FeedbackProtectionMuted || (engine.IsRunning && LocalMode && State.EchoCancellationEnabled && !status.IsActive) ? Amber : Muted;
        }
        if (diagnosticSummaryText != null) diagnosticSummaryText.Text = DiagnosticSummary();
        UpdateSignalPath();
    }

    private string EchoDiagnosticDetails()
    {
        var echo = engine.EchoCancellationStatus;
        var active = echo.IsActive ? Loc.T("运行中", "Running") : Loc.T("未运行", "Not running");
        var reference = echo.ReferenceAvailable ? Loc.T("可用", "Available") : Loc.T("不可用", "Unavailable");
        var mode = echo.Code == "waiting_reference"
            ? Loc.T("等待播放参考，回声环节透传；不代表回声消除已生效", "Waiting for playback reference; the echo stage passes through, not verified echo removal")
            : echo.Code;
        var guard = !engine.IsRunning ? Loc.T("未运行", "Not running") : engine.FeedbackProtectionMuted ? Loc.T("已锁定静音", "Latched muted") : Loc.T("已启用，未触发", "Armed, not triggered");
        return Loc.F("\n\n本地外放保护（仅虚拟输入线路）\n回声处理组件：{0}\n播放参考：{1}\n回声状态：{2}\n回声适配缓冲：{3:0.00} ms（不是端到端延迟）\n参考重置计数：{4}\n啸叫保护输出：{5}",
            "\n\nLocal speaker protection (virtual-input route only)\nEcho processor: {0}\nPlayback reference: {1}\nEcho status: {2}\nEcho adapter buffer: {3:0.00} ms (not end-to-end latency)\nReference resets: {4}\nFeedback output protection: {5}",
            active, reference, mode, echo.LatencySamples * 1000.0 / SampleRate, echo.ReferenceResets, guard);
    }

    private UIElement ExternalMonitoringControls()
    {
        var panel = new StackPanel();
        panel.Children.Add(ButtonSwitch(Loc.T("外放回声消除", "Speaker echo cancellation"), State.EchoCancellationEnabled, value =>
        {
            StopAudio(); State.EchoCancellationEnabled = value; SaveSoon(); ShowPage(4);
            Notice(Loc.T("回声设置已保存，点击开始处理使用。", "Echo settings saved. Start processing to use them."));
        }, LocalMode, () => State.EchoCancellationEnabled));
        var statusText = T("", 12, Muted);
        echoProtectionStatusText = statusText;
        statusText.Loaded += (_, _) => { echoProtectionStatusText = statusText; UpdateEchoProtectionStatus(); };
        statusText.Unloaded += (_, _) => { if (ReferenceEquals(echoProtectionStatusText, statusText)) echoProtectionStatusText = null; };
        panel.Children.Add(statusText); UpdateEchoProtectionStatus();
        var choices = new List<EchoOutputChoice> { new(null, Loc.T("自动：Discord 或系统播放设备", "Automatic: Discord or system playback")) };
        choices.AddRange(outputs.Where(d => d.IsActive && !AudioDeviceService.IsVirtualCableEndpoint(d.Name)).Select(d => new EchoOutputChoice(d.Id, d.DisplayName)));
        var picker = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(EchoOutputChoice.Name), Margin = new(0, 4, 0, 10) };
        picker.SelectedItem = choices.FirstOrDefault(c => c.Id == State.EchoReferenceOutputDeviceId) ?? choices[0];
        picker.SelectionChanged += (_, _) =>
        {
            if (building || picker.SelectedItem is not EchoOutputChoice choice) return;
            StopAudio(); State.EchoReferenceOutputDeviceId = choice.Id; SaveSoon();
            Notice(Loc.T("播放参考已保存，点击开始处理使用。", "Playback reference saved. Start processing to use it."));
        };
        panel.Children.Add(new Expander { Header = Loc.T("播放参考设备", "Playback reference device"), Foreground = Ink, Content = picker });
        panel.Children.Add(T(Loc.T("持续啸叫会触发静音保护；请先停止外部回放或改用耳机，再停止并重新开始。", "Sustained howling triggers a muted output. Stop external playback or use headphones, then stop and restart processing."), 12, Muted));
        return panel;
    }
    private static void CheckExternalPlaybackRouting()
    {
        var routing = PlaybackRoutingInspector.Inspect();
        // A Discord process can hold a virtual render session while its selected
        // voice output remains physical. Session existence alone is not proof of a loop.
        if (routing.DefaultPlaybackIsVirtual || routing.DefaultCommunicationsIsVirtual)
            throw new InvalidOperationException(Loc.T(
                "检测到 Windows 默认播放设备或默认通信输出是虚拟线路，可能将电脑声音混入麦克风并形成数字反馈。请将对应的 Windows 默认输出改为实际耳机或扬声器，再开始处理。",
                "The Windows default playback or communications output is a virtual cable. This can mix computer sound into the microphone and create digital feedback. Set the corresponding Windows default output to physical headphones or speakers, then start processing."));
    }
}
