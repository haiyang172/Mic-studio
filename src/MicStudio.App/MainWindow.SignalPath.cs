using System.Globalization;
using System.Windows.Automation;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private sealed record SignalPathNode(string Title, string Code, string Status, string Meter = "", bool Active = false, bool Warning = false);
    private sealed record SignalPathState(SignalPathNode[] Nodes, string Detail, bool Warning, int TargetPage, string Action, string EchoCode, long BufferResets);
    private sealed record SignalPathView(Border Root, TextBlock[] Titles, TextBlock[] Statuses, TextBlock[] Meters, TextBlock Detail, System.Windows.Controls.Button Action);
    private readonly List<SignalPathView> signalPathViews = [];

    private UIElement SignalPathCard(bool compact = false)
    {
        var state = ReadSignalPathState();
        var stages = new WrapPanel { Margin = new(0, 4, -14, 0) };
        var titles = new TextBlock[4]; var statuses = new TextBlock[4]; var meters = new TextBlock[4];
        for (var index = 0; index < 4; index++)
        {
            var column = new StackPanel { Margin = new(0, 0, 14, compact ? 6 : 12) };
            titles[index] = T("", 12, Muted, margin: new(0, 0, 0, 8));
            statuses[index] = T("", compact ? 13 : 14, weight: FontWeights.SemiBold, margin: new(0, 0, 0, 6));
            meters[index] = T("", 12, Muted, margin: new(0));
            AutomationProperties.SetAutomationId(statuses[index], "signal-path-" + new[] { "input", "processing", "send", "receiver" }[index]);
            column.Children.Add(titles[index]); column.Children.Add(statuses[index]); column.Children.Add(meters[index]); stages.Children.Add(column);
        }
        stages.SizeChanged += (_, _) =>
        {
            var width = stages.ActualWidth;
            if (width <= 0) return;
            var columns = width >= 680 ? 4 : width >= 330 ? 2 : 1;
            foreach (FrameworkElement column in stages.Children) column.Width = Math.Max(1, width / columns - 14);
        };
        var detail = T("", 12, Muted, margin: new(0, compact ? 4 : 8, 0, 8));
        AutomationProperties.SetAutomationId(detail, "signal-path-detail");
        var action = B("", () => { var latest = ReadSignalPathState(); ShowPage(latest.TargetPage); });
        AutomationProperties.SetAutomationId(action, "signal-path-action");
        var root = Card(Loc.T("声音线路", "Signal path"), Loc.T("这里显示本软件观察到的状态；接收软件需要单独测试。", "Shows this app's observations; the receiving app requires a separate test."), stages, detail, Buttons(action));
        AutomationProperties.SetAutomationId(root, "signal-path");
        var view = new SignalPathView(root, titles, statuses, meters, detail, action);
        root.Loaded += (_, _) => { if (!signalPathViews.Contains(view)) signalPathViews.Add(view); RefreshSignalPathView(view, ReadSignalPathState()); };
        root.Unloaded += (_, _) => signalPathViews.Remove(view);
        RefreshSignalPathView(view, state);
        return root;
    }

    private void UpdateSignalPath()
    {
        if (signalPathViews.Count == 0) return;
        var state = ReadSignalPathState();
        foreach (var view in signalPathViews.ToArray()) RefreshSignalPathView(view, state);
    }

    private void RefreshSignalPathView(SignalPathView view, SignalPathState state)
    {
        for (var index = 0; index < state.Nodes.Length; index++)
        {
            var node = state.Nodes[index];
            view.Titles[index].Text = (index + 1) + "  " + node.Title + (index < 3 ? "  →" : "");
            view.Statuses[index].Text = node.Status;
            view.Statuses[index].Foreground = node.Warning ? Amber : node.Active ? Accent : Ink;
            AutomationProperties.SetItemStatus(view.Statuses[index], node.Code);
            view.Meters[index].Text = node.Meter;
            view.Meters[index].Visibility = node.Meter.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        view.Detail.Text = state.Detail; view.Detail.Foreground = state.Warning ? Amber : Muted;
        view.Action.Content = state.Action; AutomationProperties.SetName(view.Action, state.Action);
        // On Devices the entry leads back to the ordinary microphone controls.
        view.Action.Visibility = state.Action.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private SignalPathState ReadSignalPathState()
    {
        var running = engine.IsRunning;
        var session = engine.SessionId;
        var observed = running && lastMeter is { } meter && meter.SessionId == session;
        var route = CableRouteResolver.Resolve(inputs, outputs, State.CableOutputId);
        var input = SelectedInput;
        var inputAvailable = input is { IsActive: true };
        var virtualInput = input?.IsCableReceive == true;
        var feedbackMuted = running && engine.FeedbackProtectionMuted;
        var sendConfigured = running && activeCableSendId != null;
        var sameSend = sendConfigured && route.IsReady && route.Send!.Id == activeCableSendId;
        var sendOpen = sameSend && engine.TransmitEnabled && !feedbackMuted;
        var echo = engine.EchoCancellationStatus;
        var resets = Math.Max(0, engine.OutputBufferResets);
        string MeterText(double peak) => !double.IsFinite(peak) ? "—" : peak <= -150 ? Loc.T("接近静音", "Near silence") : peak.ToString("0.0", CultureInfo.CurrentCulture) + " dBFS";
        var nodes = new SignalPathNode[4];
        nodes[0] = !inputAvailable
            ? new(Loc.T("麦克风", "Microphone"), "input_unavailable", Loc.T("设备未连接", "Device disconnected"), Warning: true)
            : virtualInput && LocalMode
                ? new(Loc.T("麦克风", "Microphone"), "input_is_virtual", Loc.T("请选择真实麦克风", "Choose a physical microphone"), Warning: true)
                : observed
                    ? new(Loc.T("麦克风", "Microphone"), "input_observed", Loc.T("已测得输入", "Input measured"), MeterText(lastMeter!.InputPeakDb), Active: true)
                    : new(Loc.T("麦克风", "Microphone"), running ? "input_waiting_samples" : "input_connected", running ? Loc.T("等待采集电平", "Waiting for samples") : Loc.T("已连接 · 未采集", "Connected · not capturing"));
        nodes[1] = !LocalMode
            ? new(Loc.T("系统效果", "System effects"), NativeMode && nativeWriter != null ? "native_parameters_sent" : "system_audio_unverified",
                NativeMode && nativeWriter != null ? Loc.T("参数已发送 · 声音待验证", "Parameters sent · audio unverified") : Loc.T("音频生效待验证", "Audio effect unverified"), Warning: true)
            : !running
                ? new(Loc.T("本地处理", "Local processing"), "processing_stopped", Loc.T("已停止", "Stopped"))
                : new(Loc.T("本地处理", "Local processing"), Draft.Bypass ? "effects_bypassed" : observed ? "processing_observed" : "processing_waiting_samples",
                    Draft.Bypass ? Loc.T("效果已关闭", "Effects off") : observed ? Loc.T("处理电平已测得", "Processed level measured") : Loc.T("等待处理电平", "Waiting for output samples"),
                    observed ? MeterText(lastMeter!.OutputPeakDb) : "", Active: observed && !Draft.Bypass);
        nodes[2] = !LocalMode
            ? new(Loc.T("系统输入", "System input"), "system_route", Loc.T("接收软件仍选原麦克风", "Receiver uses the original mic"))
            : !route.IsReady
                ? new(Loc.T("虚拟线路", "Virtual cable"), "cable_" + route.Status.ToString().ToLowerInvariant(), Loc.T("线路未就绪", "Cable not ready"), Warning: true)
                : !running || !sendConfigured
                    ? new(Loc.T("虚拟线路", "Virtual cable"), "cable_ready_idle", Loc.T("已匹配 · 未发送", "Matched · not sending"))
                    : !sameSend
                        ? new(Loc.T("虚拟线路", "Virtual cable"), "cable_changed", Loc.T("线路已变化", "Cable changed"), Warning: true)
                        : feedbackMuted
                            ? new(Loc.T("虚拟线路", "Virtual cable"), "feedback_muted", Loc.T("啸叫保护已静音", "Feedback protection muted"), Warning: true)
                            : State.TransmitMuted
                                ? new(Loc.T("虚拟线路", "Virtual cable"), "send_muted", Loc.T("发送已静音", "Send muted"))
                                : State.Shortcuts.PushToTalkEnabled && shortcuts?.PttReady != true
                                    ? new(Loc.T("虚拟线路", "Virtual cable"), "ptt_unavailable", Loc.T("按住说话未就绪", "Push-to-talk not ready"), Warning: true)
                                    : !sendOpen
                                        ? new(Loc.T("虚拟线路", "Virtual cable"), State.Shortcuts.PushToTalkEnabled ? "ptt_waiting" : "send_closed", State.Shortcuts.PushToTalkEnabled ? Loc.T("等待按住说话", "Waiting for talk key") : Loc.T("发送已关闭", "Send closed"))
                                        : new(Loc.T("虚拟线路", "Virtual cable"), "local_send_open", Loc.T("本地发送已开启", "Local send is open"), Active: true);
        nodes[3] = new(Loc.T("接收软件", "Receiving app"), "receiver_unverified", Loc.T("实际收到待确认", "Reception unverified"), Loc.T("需在通话或录音软件测试", "Test in the receiving app"));

        var warning = false;
        var targetPage = currentPage == 4 ? 0 : 4;
        var action = currentPage == 4 ? Loc.T("打开麦克风页面", "Open microphone controls") : Loc.T("检查设备与连接", "Check devices and connections");
        string detail;
        if (feedbackMuted)
        { detail = Loc.T("已阻断持续啸叫。停止外部麦克风回放或改用耳机，再停止并重新开始处理。", "Sustained feedback was blocked. Stop external mic playback or use headphones, then stop and restart processing."); warning = true; }
        else if (!inputAvailable || (virtualInput && LocalMode))
        { detail = Loc.T("请确认所选真实麦克风；不会自动换用其他麦克风。", "Check the selected physical microphone; another microphone is never substituted automatically."); warning = true; }
        else if (LocalMode && !route.IsReady)
        { detail = RouteMessage(route); warning = true; }
        else if (LocalMode && sendConfigured && !sameSend)
        { detail = Loc.T("当前线路与已开始的线路不同，请检查设备后手动重新开始。", "The selected cable differs from the running route. Check devices, then restart manually."); warning = true; }
        else if (!LocalMode)
            detail = Loc.T("系统参数或设备存在不证明音频生效；请在接收软件中单独验证。", "System parameters or device presence do not verify audio effects; test separately in the receiving app.");
        else if (running && State.EchoCancellationEnabled && (!echo.IsActive || !echo.ReferenceAvailable))
        { detail = Loc.T("播放参考尚未就绪，不能确认回声消除；其他调音按当前设置运行。", "Playback reference is not ready; echo removal is unverified. Other processing follows the current settings."); warning = true; }
        else if (running && resets > 0)
        { detail = Loc.F("本次渲染缓冲已重置 {0} 次；如持续增加，请检查设备与连接。", "Render buffer reset {0} times this session; check devices and connections if the count keeps rising.", resets); warning = true; }
        else if (!running)
        { detail = Loc.T("开始处理后可查看输入和处理电平。", "Start processing to see the input and processed levels."); targetPage = 0; action = currentPage == 0 ? "" : Loc.T("打开麦克风页面", "Open microphone controls"); }
        else
            detail = Loc.T("这里只确认本地观察与发送状态，不证明通话或录音软件已经收到声音。", "These are local observations and send states, not proof that a calling or recording app received audio.");
        if (currentPage == 4 && targetPage == 4) action = "";
        return new(nodes, detail, warning, targetPage, action, echo.Code, resets);
    }

    private string SignalPathDiagnosticDetails()
    {
        var state = ReadSignalPathState();
        return Loc.F("\n\n声音线路（本地观察）\n输入：{0}\n处理：{1}\n发送：{2}\n接收软件：{3}\n回声状态代码：{4}\n渲染缓冲重置：{5}",
            "\n\nSignal path (local observations)\nInput: {0}\nProcessing: {1}\nSend: {2}\nReceiving app: {3}\nEcho status code: {4}\nRender buffer resets: {5}",
            state.Nodes[0].Code, state.Nodes[1].Code, state.Nodes[2].Code, state.Nodes[3].Code, state.EchoCode, state.BufferResets);
    }
}
