using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private sealed record EchoOutputChoice(string? Id, string Name);
    private UIElement ExternalMonitoringControls()
    {
        var panel = new StackPanel();
        panel.Children.Add(ButtonSwitch(Loc.T("外放回声消除", "Speaker echo cancellation"), State.EchoCancellationEnabled, value =>
        {
            StopAudio(); State.EchoCancellationEnabled = value; SaveSoon(); ShowPage(4);
            Notice(Loc.T("回声设置已保存，点击开始处理使用。", "Echo settings saved. Start processing to use them."));
        }, LocalMode, () => State.EchoCancellationEnabled));
        var status = engine.EchoCancellationStatus;
        var description = !engine.IsRunning
            ? Loc.T("开始后使用电脑播放声作为本地参考，不保存或上传播放内容。", "Processing uses computer playback as a local reference; playback is never saved or uploaded.")
            : status.IsActive && !status.ReferenceAvailable
                ? Loc.T("等待电脑播放参考；当前保持麦克风信号和啸叫保护。", "Waiting for computer playback reference; microphone passthrough and feedback protection remain available.")
            : status.IsActive
                ? Loc.T("回声消除运行中；实际效果取决于外放和麦克风的距离及音量。", "Echo cancellation is running; results depend on speaker/microphone placement and volume.")
                : State.EchoCancellationEnabled
                    ? Loc.T("回声消除未就绪，当前仅有啸叫输出保护。详情见诊断。", "Echo cancellation is not ready; feedback output protection remains active. See Diagnostics.")
                    : Loc.T("回声消除已关闭；啸叫输出保护仍开启。", "Echo cancellation is off; feedback output protection stays on.");
        panel.Children.Add(T(description, 12, engine.IsRunning && State.EchoCancellationEnabled && !status.IsActive ? Amber : Muted));
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
                "检测到 Windows 或 Discord 正把播放声音送入虚拟线路，可能形成数字反馈。请把外部播放输出改为实际耳机或扬声器，再开始处理。",
                "Windows or Discord is sending playback into a virtual cable, which can create digital feedback. Select physical headphones or speakers for external playback, then start processing."));
    }
}
