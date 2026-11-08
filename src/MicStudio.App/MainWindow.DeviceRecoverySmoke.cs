using System.Text.Json;
using System.Threading;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    // All attempted recovery starts use the UiSmoke branch; never open an audio endpoint here.
    private void RunDeviceRecoveryChecks(Action<bool, string> Check)
    {
        if (!UiSmoke || deviceRecovery == null || engine.IsRunning || engine.IsRecording || engine.IsPlaying)
            throw new InvalidOperationException("Recovery checks require an isolated, stopped application.");
        var savedInputs = inputs; var savedOutputs = outputs;
        var savedInput = State.InputDeviceId; var savedCable = State.CableOutputId;
        var savedMode = State.Mode; var savedResume = State.ResumeSameDevice;
        var savedMonitor = monitor; var savedHeadphones = headphones; var savedMonitorId = monitorId;
        var savedMeter = lastMeter; var savedSend = activeCableSendId; var savedPage = currentPage;
        var savedClock = smokeRecoveryClock; var savedStarts = smokeRecoveryStarts;
        var savedSuccess = smokeRecoveryStartSucceeds;
        var mic = new AudioDeviceInfo("recovery-smoke-private-mic", "Synthetic recovery microphone", "Active", false, false, false, false, 48000, 2);
        var send = new AudioDeviceInfo("recovery-smoke-private-send", "CABLE Input (VB-Audio Virtual Cable)", "Active", false, false, true, false, 48000, 2);
        var receive = new AudioDeviceInfo("recovery-smoke-private-receive", "CABLE Output (VB-Audio Virtual Cable)", "Active", false, false, false, true, 48000, 2);
        void RestoreEndpoints()
        {
            inputs = [mic, receive]; outputs = [send];
            State.InputDeviceId = mic.Id; State.CableOutputId = send.Id; State.Mode = 1;
            monitor = headphones = false; monitorId = null; activeCableSendId = null; lastMeter = null;
        }
        RecoveryRoute Arm()
        {
            CancelDeviceRecovery("test_reset"); RestoreEndpoints();
            Interlocked.Exchange(ref recoveryFeedbackBlockedSession, -1);
            Interlocked.Exchange(ref recoverySystemBlocked, 0);
            State.ResumeSameDevice = true; deviceRecovery.SetEnabled(true); smokeRecoveryStartSucceeds = true;
            smokeRecoveryDuringStart = null;
            if (!TryReadRecoveryRoute(out var route) || route == null || !deviceRecovery.RememberRunning(route))
                throw new InvalidOperationException("Synthetic recovery route could not be armed.");
            recoveryCaptureSession = 123;
            return route;
        }
        void LoseMicrophone()
        {
            inputs = [receive]; ObserveDeviceRecovery();
        }
        void ReadyForOneSecond()
        {
            RestoreEndpoints(); ObserveDeviceRecovery(); EvaluateDeviceRecovery();
            smokeRecoveryClock += DeviceRecoveryPolicy.StableReadyMilliseconds; EvaluateDeviceRecovery();
        }
        try
        {
            Check(!State.ResumeSameDevice && !deviceRecovery.State.Enabled && !deviceRecovery.State.HasRunningIntent,
                "Same-device recovery defaults off and startup has no capture authorization");
            ShowPage(6); UpdateLayout();
            var preference = Descendants<CheckBox>(PageContent).Single(c => (c.Content as TextBlock)?.Text == Loc.T("断线后自动恢复", "Resume after disconnection"));
            preference.IsChecked = true;
            preference.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(State.ResumeSameDevice && deviceRecovery.State.Enabled && !deviceRecovery.State.HasRunningIntent && smokeRecoveryStarts == savedStarts && !engine.IsRunning,
                "Recovery settings opt-in changes a preference without authorizing stopped capture");
            vm.Save(); var reloaded = new StudioViewModel().State;
            var restoredPolicy = new DeviceRecoveryPolicy(() => smokeRecoveryClock); restoredPolicy.SetEnabled(reloaded.ResumeSameDevice);
            var legacy = JsonSerializer.Deserialize<WorkspaceState>("{\"SchemaVersion\":2}")!; legacy.Normalize();
            Check(reloaded.ResumeSameDevice && restoredPolicy.State.Enabled && !restoredPolicy.State.HasRunningIntent && restoredPolicy.State.Route == null && !legacy.ResumeSameDevice,
                "Recovery preference persists while runtime start intent is absent from reload and old workspaces");

            var wanted = Arm(); LoseMicrophone();
            Check(deviceRecovery.State.Pending && deviceRecovery.State.Code == "waiting_device" && !recoveryTimer.IsEnabled && smokeRecoveryStarts == savedStarts,
                "Confirmed selected-microphone loss waits for the exact route using no live retry timer in UI checks");
            RestoreEndpoints(); ObserveDeviceRecovery(); EvaluateDeviceRecovery(); smokeRecoveryClock += 999; EvaluateDeviceRecovery();
            Check(smokeRecoveryStarts == savedStarts && deviceRecovery.State.Code == "stabilizing",
                "Returned devices do not recover before the continuous stability interval");
            smokeRecoveryClock++; EvaluateDeviceRecovery();
            Check(smokeRecoveryStarts == savedStarts + 1 && deviceRecovery.State.Code == "recovered" && !engine.IsRunning && !engine.IsRecording && !engine.IsPlaying,
                "Exact stable reconnection consumes one simulated recovery without capture recording or playback");
            EvaluateDeviceRecovery(); smokeRecoveryClock += 5000; EvaluateDeviceRecovery();
            Check(smokeRecoveryStarts == savedStarts + 1 && !deviceRecovery.State.Pending,
                "Repeated refreshes after recovery cannot duplicate a successful attempt");

            Arm(); LoseMicrophone(); var beforeStop = smokeRecoveryStarts;
            var cancelButton = recoveryCancelButton ?? throw new InvalidOperationException("Missing recovery cancel control.");
            var cancelWasVisible = cancelButton.Visibility == Visibility.Visible;
            cancelButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            ReadyForOneSecond();
            Check(cancelWasVisible && cancelButton.Visibility == Visibility.Collapsed && deviceRecovery.State.Code == "user_stopped" && !deviceRecovery.State.HasRunningIntent && smokeRecoveryStarts == beforeStop,
                "Explicit stop cancels pending recovery even when the original devices return");

            Arm(); LoseMicrophone(); State.InputDeviceId = "another-selected-microphone"; ObserveDeviceRecovery();
            Check(deviceRecovery.State.Code == "route_changed" && !deviceRecovery.State.HasRunningIntent,
                "Changing microphone selection revokes pending recovery rather than substituting an input");
            Arm(); LoseMicrophone(); RestoreEndpoints(); inputs = [mic with { SampleRate = 44100 }, receive]; ObserveDeviceRecovery();
            Check(deviceRecovery.State.Code == "format_changed" && !deviceRecovery.State.HasRunningIntent,
                "A changed microphone format requires manual confirmation instead of recovery");
            Arm(); LoseMicrophone(); RestoreEndpoints(); inputs = [mic, receive with { Channels = 1 }]; ObserveDeviceRecovery();
            Check(deviceRecovery.State.Code == "format_changed" && !deviceRecovery.State.HasRunningIntent,
                "Receive-endpoint format changes revoke the original full-route fingerprint");

            Arm(); LoseMicrophone(); Interlocked.Exchange(ref recoveryFeedbackBlockedSession, recoveryCaptureSession); ObserveDeviceRecovery(); var feedbackStarts = smokeRecoveryStarts; ReadyForOneSecond();
            Check(deviceRecovery.State.Code == "feedback_protection" && !deviceRecovery.State.HasRunningIntent && smokeRecoveryStarts == feedbackStarts,
                "Raw-session feedback protection survives stopped-engine state and blocks automatic reopening");
            Arm(); LoseMicrophone(); Interlocked.Exchange(ref recoverySystemBlocked, 1); ObserveDeviceRecovery();
            Check(deviceRecovery.State.Code == "session_changed" && !deviceRecovery.State.HasRunningIntent && State.ResumeSameDevice,
                "Session or power blocking cancels runtime recovery without erasing the opt-in preference");

            Arm(); LoseMicrophone(); var lateFeedbackStarts = smokeRecoveryStarts;
            smokeRecoveryDuringStart = () => Interlocked.Exchange(ref recoveryFeedbackBlockedSession, recoveryCaptureSession);
            ReadyForOneSecond(); smokeRecoveryDuringStart = null;
            Check(smokeRecoveryStarts == lateFeedbackStarts + 1 && deviceRecovery.State.Code == "feedback_protection" &&
                !deviceRecovery.State.HasRunningIntent && !deviceRecovery.State.AttemptInProgress && !engine.IsRunning,
                "Original-session feedback arriving during recovery opening revokes completion before the session is replaced");
            Arm(); LoseMicrophone(); var lateSystemStarts = smokeRecoveryStarts;
            smokeRecoveryDuringStart = () => Interlocked.Exchange(ref recoverySystemBlocked, 1);
            ReadyForOneSecond(); smokeRecoveryDuringStart = null;
            Check(smokeRecoveryStarts == lateSystemStarts + 1 && deviceRecovery.State.Code == "session_changed" &&
                !deviceRecovery.State.HasRunningIntent && !deviceRecovery.State.AttemptInProgress && !engine.IsRunning,
                "Session blocking arriving during device opening cannot report a recovered running intent");
            Arm(); LoseMicrophone(); var lateCancelStarts = smokeRecoveryStarts;
            smokeRecoveryDuringStart = () => CancelDeviceRecovery();
            ReadyForOneSecond(); smokeRecoveryDuringStart = null;
            Check(smokeRecoveryStarts == lateCancelStarts + 1 && deviceRecovery.State.Code == "user_stopped" &&
                !deviceRecovery.State.HasRunningIntent && !deviceRecovery.State.AttemptInProgress && !engine.IsRunning,
                "Explicit cancellation during recovery opening invalidates the reservation and cannot be overwritten by success");

            Arm(); var genericStarts = smokeRecoveryStarts;
            HandleRecoveryEngineStatus(new(false, "Synthetic generic audio failure", "synthetic-error", engine.SessionId) { StopReason = AudioStopReason.AudioError });
            smokeRecoveryClock += 5000; EvaluateDeviceRecovery();
            Check(deviceRecovery.State.Code == "audio_error" && !deviceRecovery.State.HasRunningIntent && smokeRecoveryStarts == genericStarts,
                "A generic audio failure with available devices is not mistaken for a reconnectable unplug");
            Arm(); inputs = [mic with { SampleRate = 0 }, receive];
            ObserveDeviceRecovery();
            Check(deviceRecovery.State.HasRunningIntent && !deviceRecovery.State.Pending,
                "An active endpoint with unavailable format metadata is not treated as disconnected");
            Arm(); var confirmedStarts = smokeRecoveryStarts;
            HandleRecoveryEngineStatus(new(false, "Synthetic confirmed disconnect", "synthetic-disconnect", engine.SessionId) { StopReason = AudioStopReason.DeviceUnavailable });
            Check(deviceRecovery.State.Pending && deviceRecovery.State.Code == "waiting_device",
                "Engine-confirmed device loss is remembered even when endpoints have already reappeared");
            ReadyForOneSecond();
            Check(smokeRecoveryStarts == confirmedStarts + 1 && deviceRecovery.State.Code == "recovered" && !engine.IsRunning,
                "A confirmed quick reconnect still passes stability checks before one simulated recovery");

            Arm(); LoseMicrophone(); smokeRecoveryStartSucceeds = false; var failedStarts = smokeRecoveryStarts; ReadyForOneSecond();
            var failedState = deviceRecovery.State; smokeRecoveryClock += 10000; ObserveDeviceRecovery(); EvaluateDeviceRecovery();
            Check(failedState.Paused && failedState.Code == "recovery_failed" && smokeRecoveryStarts == failedStarts + 1 && deviceRecovery.State.Paused,
                "Failed recovery pauses after one attempt instead of repeatedly reopening a device");

            Arm(); var stoppedPath = ReadSignalPathState();
            Check(stoppedPath.Nodes[0].Code == "input_connected" && stoppedPath.Nodes[1].Code == "processing_stopped" && stoppedPath.Nodes[2].Code == "cable_ready_idle" && stoppedPath.Nodes[3].Code == "receiver_unverified",
                "Signal path distinguishes connected stopped capture matched cable and unverified reception");
            outputs = []; var missingPath = ReadSignalPathState();
            Check(missingPath.Nodes[2].Warning && missingPath.Nodes[2].Code.StartsWith("cable_", StringComparison.Ordinal) && missingPath.Nodes[3].Code == "receiver_unverified",
                "Incomplete cable routing is visibly unavailable without claiming the receiving app has audio");
            RestoreEndpoints(); lastMeter = new(-10, -20, -8, -18, 48000, 2, 0, TimeSpan.FromSeconds(1), engine.SessionId);
            Check(ReadSignalPathState().Nodes[0].Code == "input_connected",
                "A stale saved meter cannot make a stopped microphone appear actively measured");
            lastMeter = null; State.Mode = 2; var systemPath = ReadSignalPathState();
            Check(systemPath.Nodes[1].Code == "system_audio_unverified" && systemPath.Nodes[3].Code == "receiver_unverified",
                "System-route parameter presence is not displayed as verified system or receiving audio");
            State.Mode = 1; var details = SignalPathDiagnosticDetails() + RecoveryDiagnosticDetails();
            Check(wanted.EndpointFormats?.Contains(mic.Id, StringComparison.Ordinal) == true &&
                new[] { mic.Id, send.Id, receive.Id, mic.Name }.All(privateText => !details.Contains(privateText, StringComparison.Ordinal)),
                "New signal-path and recovery diagnostic sections redact endpoint names IDs and stored fingerprints");
        }
        finally
        {
            recoveryTimer.Stop(); deviceRecovery.Disarm("test_complete");
            State.ResumeSameDevice = savedResume; State.InputDeviceId = savedInput; State.CableOutputId = savedCable; State.Mode = savedMode;
            inputs = savedInputs; outputs = savedOutputs; monitor = savedMonitor; headphones = savedHeadphones; monitorId = savedMonitorId;
            lastMeter = savedMeter; activeCableSendId = savedSend;
            smokeRecoveryClock = savedClock; smokeRecoveryStarts = savedStarts; smokeRecoveryStartSucceeds = savedSuccess;
            smokeRecoveryDuringStart = null;
            recoveryCaptureSession = recoveryFeedbackBlockedSession = -1; Interlocked.Exchange(ref recoverySystemBlocked, 0);
            deviceRecovery = new(() => smokeRecoveryClock); deviceRecovery.SetEnabled(savedResume);
            saveTimer.Stop(); vm.Save(); ShowPage(savedPage); NoticeBar.Visibility = Visibility.Collapsed;
        }
    }
}
