using System.Text.Json;
using System.Threading;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private DeviceRecoveryPolicy deviceRecovery = null!;
    private readonly DispatcherTimer recoveryTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TextBlock? recoveryStatusText;
    private Button? recoveryCancelButton;
    private int recoveryCaptureSession = -1, recoveryFeedbackBlockedSession = -1, recoverySystemBlocked;
    private int automaticRecoverySession = -1;
    private bool evaluatingRecovery;
    private long smokeRecoveryClock = 10000;
    private int smokeRecoveryStarts;
    private bool smokeRecoveryStartSucceeds = true;
    private Action? smokeRecoveryDuringStart;

    private void InitializeDeviceRecovery()
    {
        deviceRecovery = new(UiSmoke ? () => smokeRecoveryClock : null);
        deviceRecovery.SetEnabled(State.ResumeSameDevice);
        recoveryTimer.Tick += (_, _) => { if (!closed && !UiSmoke) RefreshDevices(); };
        // Preserve the guard across StopCore, even if its UI notification arrives late.
        engine.FeedbackProtectionTriggered += (_, protection) =>
        {
            Interlocked.Exchange(ref recoveryFeedbackBlockedSession, protection.SessionId);
            Dispatcher.BeginInvoke(() =>
            {
                if (!closed && protection.SessionId == recoveryCaptureSession) CancelDeviceRecovery("feedback_protection");
            });
        };
    }

    private void CancelDeviceRecovery(string reason = "user_stopped")
    {
        recoveryTimer.Stop();
        deviceRecovery?.Cancel(reason);
        recoveryCaptureSession = -1;
        UpdateRecoveryStatus();
    }

    private List<AudioDeviceInfo>? ReadRecoveryEndpoints()
    {
        if (!LocalMode || SelectedInput is not { IsActive: true, IsCableReceive: false } input || State.CableOutputId == null) return null;
        var cable = CableRouteResolver.Resolve(inputs, outputs, State.CableOutputId);
        if (!cable.IsReady || cable.Send!.Id != State.CableOutputId) return null;
        var required = new List<AudioDeviceInfo> { input, cable.Send, cable.Receive! };
        if (monitor)
        {
            if (!headphones || monitorId == null || outputs.FirstOrDefault(d => d.Id == monitorId && d.IsActive && !AudioDeviceService.IsVirtualCableEndpoint(d.Name)) is not { } output) return null;
            required.Add(output);
        }
        return required;
    }

    private bool TryReadRecoveryRoute(out RecoveryRoute? route)
    {
        route = null;
        var required = ReadRecoveryEndpoints();
        if (required == null || required.Any(d => d.SampleRate is < 8000 or > 384000 || d.Channels is < 1 or > 8)) return false;
        route = new(required[0].Id, required[1].Id, required[2].Id, required.Count > 3 ? required[3].Id : null, required[0].SampleRate, required[0].Channels)
        {
            EndpointFormats = JsonSerializer.Serialize(required.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => new { d.Id, d.SampleRate, d.Channels }))
        };
        return true;
    }

    private void RememberDeviceRecovery(bool cable)
    {
        if (!cable || !LocalMode || !engine.IsRunning || !TryReadRecoveryRoute(out var route) || route == null) return;
        if (engine.SampleRate != route.SampleRate || engine.Channels != route.Channels) return;
        recoveryCaptureSession = engine.SessionId;
        if ((recoveryCaptureSession >= 0 && Volatile.Read(ref recoveryFeedbackBlockedSession) == recoveryCaptureSession) || engine.FeedbackProtectionMuted)
        {
            CancelDeviceRecovery("feedback_protection");
            return;
        }
        deviceRecovery.RememberRunning(route);
        UpdateRecoveryStatus();
    }

    private bool RecoveryKeysMatch(RecoveryRoute wanted) => LocalMode && State.InputDeviceId == wanted.InputId &&
        State.CableOutputId == wanted.CableSendId && (monitor ? monitorId : null) == wanted.MonitorId && (!monitor || headphones);

    private bool RecoverySafetyAllows(RecoveryRoute wanted)
    {
        if (closed || !State.ResumeSameDevice || Volatile.Read(ref recoverySystemBlocked) != 0)
        {
            CancelDeviceRecovery("session_changed");
            return false;
        }
        if ((recoveryCaptureSession >= 0 && Volatile.Read(ref recoveryFeedbackBlockedSession) == recoveryCaptureSession) || engine.FeedbackProtectionMuted)
        {
            CancelDeviceRecovery("feedback_protection");
            return false;
        }
        if (engine.IsPlaying)
        {
            CancelDeviceRecovery("playback_started");
            return false;
        }
        if (!RecoveryKeysMatch(wanted))
        {
            CancelDeviceRecovery("route_changed");
            return false;
        }
        return true;
    }

    private void ObserveDeviceRecovery(bool confirmedLoss = false)
    {
        var state = deviceRecovery.State;
        var endpointsAvailable = ReadRecoveryEndpoints() != null;
        // An incomplete CABLE path stops local sending even when recovery is off.
        if (engine.IsRunning && LocalMode && activeCableSendId != null && !endpointsAvailable)
        {
            if (state.HasRunningIntent && state.Route is { } lostRoute && RecoverySafetyAllows(lostRoute)) deviceRecovery.ObserveUnavailable(lostRoute);
            StopLocalAudio(false);
            state = deviceRecovery.State;
        }
        if (!state.HasRunningIntent || state.Route is not { } wanted || !RecoverySafetyAllows(wanted))
        {
            UpdateRecoveryStatus();
            return;
        }
        var ready = TryReadRecoveryRoute(out var current);
        if (ready && current != wanted)
        {
            CancelDeviceRecovery("format_changed");
            if (engine.IsRunning) StopLocalAudio(false);
            return;
        }
        // The engine may confirm a loss and reconnect before the dispatcher runs.
        if (!endpointsAvailable || confirmedLoss)
        {
            if (!state.Pending && deviceRecovery.ObserveUnavailable(wanted))
            {
                ResetPttForSafety(true);
                if (engine.IsRunning) StopLocalAudio(false);
            }
        }
        if (deviceRecovery.State.Pending && !deviceRecovery.State.Paused && !UiSmoke) recoveryTimer.Start();
        UpdateRecoveryStatus();
    }

    private void HandleRecoveryEngineStatus(AudioEngineStatus status)
    {
        if (status.IsRunning || status.ErrorCode == null || !deviceRecovery.State.HasRunningIntent) return;
        var generation = deviceRecovery.State.Generation;
        // Refresh before classifying generic WASAPI failures; they are not all unplug events.
        if (!UiSmoke) RefreshDevices(evaluateRecovery: false);
        if (closed || status.SessionId != engine.SessionId || engine.IsRunning || deviceRecovery.State.Generation != generation) return;
        ObserveDeviceRecovery(status.StopReason == AudioStopReason.DeviceUnavailable);
        if (!deviceRecovery.State.Pending && !deviceRecovery.State.Paused) CancelDeviceRecovery("audio_error");
    }

    private bool RecoveryAttemptCanStart(RecoveryAttempt attempt) => deviceRecovery.IsAttemptCurrent(attempt) &&
        RecoverySafetyAllows(attempt.Route) && TryReadRecoveryRoute(out var current) && current == attempt.Route;

    private void EvaluateDeviceRecovery()
    {
        if (evaluatingRecovery || engine.IsRunning || deviceRecovery.State is not { Pending: true, Route: { } wanted } || !RecoverySafetyAllows(wanted)) return;
        var ready = TryReadRecoveryRoute(out var current);
        if (!deviceRecovery.TryTakeRecovery(ready ? current! : wanted, ready, out var attempt) || attempt == null)
        {
            if (deviceRecovery.State.Paused || !deviceRecovery.State.Pending) recoveryTimer.Stop();
            UpdateRecoveryStatus(); return;
        }
        evaluatingRecovery = true; recoveryTimer.Stop();
        try
        {
            var started = false;
            if (RecoveryAttemptCanStart(attempt))
            {
                if (UiSmoke) { smokeRecoveryStarts++; started = smokeRecoveryStartSucceeds; smokeRecoveryDuringStart?.Invoke(); }
                else
                {
                    StartAudio(true, attempt); started = engine.IsRunning;
                    if (started) automaticRecoverySession = engine.SessionId;
                }
                if (started && (!RecoveryAttemptCanStart(attempt) || engine.FeedbackProtectionMuted ||
                    (!UiSmoke && Volatile.Read(ref recoveryFeedbackBlockedSession) == engine.SessionId)))
                { if (!UiSmoke && engine.IsRunning) StopLocalAudio(false); started = false; }
            }
            if (deviceRecovery.CompleteAttempt(attempt, started) && started)
            {
                if (!UiSmoke) recoveryCaptureSession = engine.SessionId;
                if (recoveryCaptureSession >= 0 && Volatile.Read(ref recoveryFeedbackBlockedSession) == recoveryCaptureSession) CancelDeviceRecovery("feedback_protection");
                else if (!RecoverySafetyAllows(attempt.Route)) { if (!UiSmoke && engine.IsRunning) StopLocalAudio(false); }
                else Notice(Loc.T("同一设备已恢复处理；录音和文件回放没有自动重启，接收软件仍需确认。", "Processing resumed on the same devices. Recording and file playback were not restarted; confirm the receiving app."));
            }
        }
        finally { evaluatingRecovery = false; UpdateRecoveryStatus(); UpdateChrome(); }
    }

    private string RecoveryStatusDescription()
    {
        var state = deviceRecovery.State;
        if (!State.ResumeSameDevice) return Loc.T("自动恢复已关闭", "Automatic recovery is off");
        return state.Code switch
        {
            "armed" or "recovered" => Loc.T("仅为当前已开始的虚拟线路等待同一设备。", "Recovery is armed only for this explicitly started virtual-cable session."),
            "waiting_device" => Loc.T("等待原麦克风和原线路重新接入；不会换用其他设备。", "Waiting for the original microphone and cable; no other devices will be substituted."),
            "stabilizing" or "recovering" => Loc.T("原设备已返回，正在确认连接稳定。", "The original devices returned; checking that the connection is stable."),
            "recovery_failed" or "flapping" => Loc.T("自动恢复已暂停。请检查连接后手动开始。", "Automatic recovery paused. Check connections, then start manually."),
            "feedback_protection" => Loc.T("啸叫保护后不自动恢复，请先处理回声再手动开始。", "Recovery is blocked after feedback protection. Resolve the feedback, then start manually."),
            "format_changed" or "route_changed" => Loc.T("设备或格式已改变，请确认后手动开始。", "A device or format changed. Confirm it, then start manually."),
            _ => Loc.T("下次手动开始虚拟线路后生效；启动软件不会自动开麦。", "Arms after the next manual virtual-cable start. Opening the app does not open the microphone.")
        };
    }

    private void UpdateRecoveryStatus()
    {
        if (recoveryStatusText != null)
        {
            recoveryStatusText.Text = RecoveryStatusDescription();
            recoveryStatusText.Visibility = State.ResumeSameDevice ? Visibility.Visible : Visibility.Collapsed;
        }
        if (recoveryCancelButton != null)
            recoveryCancelButton.Visibility = deviceRecovery.State is { Pending: true } or { AttemptInProgress: true } ? Visibility.Visible : Visibility.Collapsed;
        UpdateSignalPath();
    }

    private UIElement DeviceRecoveryControls()
    {
        var panel = new StackPanel();
        panel.Children.Add(Toggle(Loc.T("同一设备重新接入后恢复", "Resume when the same devices reconnect"), State.ResumeSameDevice, enabled =>
        {
            State.ResumeSameDevice = enabled; deviceRecovery.SetEnabled(enabled);
            if (!enabled) CancelDeviceRecovery("disabled");
            else if (engine.IsRunning && activeCableSendId != null) RememberDeviceRecovery(true);
            SaveSoon(); UpdateRecoveryStatus();
        }));
        var text = T(RecoveryStatusDescription(), 12, Muted);
        var cancel = B(Loc.T("停止并取消恢复", "Stop and cancel recovery"), StopAudio);
        recoveryStatusText = text;
        recoveryCancelButton = cancel;
        text.Loaded += (_, _) =>
        {
            recoveryStatusText = text;
            recoveryCancelButton = cancel;
            UpdateRecoveryStatus();
        };
        text.Unloaded += (_, _) =>
        {
            if (ReferenceEquals(recoveryStatusText, text)) recoveryStatusText = null;
            if (ReferenceEquals(recoveryCancelButton, cancel)) recoveryCancelButton = null;
        };
        panel.Children.Add(text);
        panel.Children.Add(cancel);
        panel.Children.Add(T(Loc.T("只恢复本次已开始的虚拟线路。停止、换设备、锁屏或睡眠后需手动开始。录音需手动重新开始。", "Resumes an already-started virtual cable session. Stop, device changes, session lock or sleep require a manual start. Restart recording separately."), 12, Muted));
        UpdateRecoveryStatus();
        return Card(Loc.T("断线恢复", "Connection recovery"), null, panel);
    }

    private string RecoveryDiagnosticDetails() => Loc.F("\n\n同设备恢复\n偏好：{0}\n状态代码：{1}\n等待恢复：{2}\n最近自动尝试：{3}",
        "\n\nSame-device recovery\nPreference: {0}\nStatus code: {1}\nPending: {2}\nRecent automatic attempts: {3}",
        State.ResumeSameDevice, deviceRecovery.State.Code, deviceRecovery.State.Pending, deviceRecovery.State.AutomaticAttemptsInWindow);
}
