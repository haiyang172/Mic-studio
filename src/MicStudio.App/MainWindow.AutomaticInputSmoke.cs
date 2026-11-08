using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private void RunAutomaticInputChecks(Action<bool, string> Check)
    {
        if (!UiSmoke || engine.IsRunning || engine.IsRecording || engine.IsPlaying)
            throw new InvalidOperationException("Automatic-input checks require isolated stopped audio.");

        var savedInputs = inputs;
        var savedOutputs = outputs;
        var savedInput = State.InputDeviceId;
        var savedCable = State.CableOutputId;
        var savedMode = State.Mode;
        var savedResume = State.ResumeSameDevice;
        var savedPreferences = State.AutomaticInputs.Clone();
        var savedVolumeLock = State.VolumeLockEnabled;
        var savedVolumeEndpoint = State.VolumeLockEndpointId;
        var savedMonitor = monitor;
        var savedHeadphones = headphones;
        var savedMonitorId = monitorId;
        var savedSend = activeCableSendId;
        var savedMeter = lastMeter;
        var savedPage = currentPage;
        var savedPickerItems = InputPicker.ItemsSource;
        var savedPickerSelection = InputPicker.SelectedItem;
        var savedClock = smokeRecoveryClock;
        var savedStarts = smokeRecoveryStarts;
        var savedSuccess = smokeRecoveryStartSucceeds;
        var savedRecoveryInput = smokeRecoveryInputId;
        var savedAutomaticSession = automaticRecoverySession;
        var savedDetailsExpanded = automaticInputsExpanded;
        var savedSuggestedGain = suggestedGain;
        var savedDiscordVerification = State.DiscordVerifiedAt;
        var savedSystemVolume = smokeVolume;
        var savedSystemMute = smokeInputMuted;
        var mic = new AudioDeviceInfo("automatic-smoke-private-primary", "Synthetic primary microphone", "Active", false, true, false, false, 48000, 2);
        var first = new AudioDeviceInfo("automatic-smoke-private-backup-one", "Synthetic backup one", "Active", false, false, false, false, 48000, 1);
        var second = new AudioDeviceInfo("automatic-smoke-private-backup-two", "Synthetic backup two", "Active", false, false, false, false, 44100, 2);
        var send = new AudioDeviceInfo("automatic-smoke-private-send", "CABLE Input (VB-Audio Virtual Cable)", "Active", false, false, true, false, 48000, 2);
        var receive = new AudioDeviceInfo("automatic-smoke-private-receive", "CABLE Output (VB-Audio Virtual Cable)", "Active", false, false, false, true, 48000, 2);

        static BackupMicrophone Backup(AudioDeviceInfo input) => new(input.Id, input.Name, input.SampleRate, input.Channels);

        void SetPicker()
        {
            var wasRefreshing = refreshing;
            refreshing = true;
            try
            {
                InputPicker.ItemsSource = inputs.ToList();
                InputPicker.SelectedItem = inputs.FirstOrDefault(input => input.Id == State.InputDeviceId);
            }
            finally { refreshing = wasRefreshing; }
        }

        void Prepare()
        {
            CancelDeviceRecovery("test_reset");
            inputs = [mic, first, second, receive];
            outputs = [send];
            State.InputDeviceId = mic.Id;
            State.CableOutputId = send.Id;
            State.Mode = 1;
            State.ResumeSameDevice = true;
            monitor = headphones = false;
            monitorId = null;
            activeCableSendId = null;
            lastMeter = null;
            State.VolumeLockEnabled = false;
            State.VolumeLockEndpointId = null;
            State.AutomaticInputs = new()
            {
                UseBackups = true,
                Backups = [Backup(first), Backup(second)]
            };
            Interlocked.Exchange(ref recoveryFeedbackBlockedSession, -1);
            Interlocked.Exchange(ref recoverySystemBlocked, 0);
            recoveryCaptureSession = 123;
            automaticRecoverySession = -1;
            smokeRecoveryClock += 2000;
            smokeRecoveryStartSucceeds = true;
            smokeRecoveryDuringStart = null;
            smokeRecoveryDuringCommit = null;
            smokeRecoveryInputId = null;
            deviceRecovery = new(() => smokeRecoveryClock);
            deviceRecovery.SetEnabled(true);
            SetPicker();
        }

        RecoveryRoute Arm(bool useBackups = true)
        {
            Prepare();
            State.AutomaticInputs.UseBackups = useBackups;
            if (!TryReadRecoveryRoute(out var route) || route == null ||
                !deviceRecovery.RememberRunning(route, AuthorizedBackupRoutes(route)))
                throw new InvalidOperationException("Synthetic automatic-input route could not be armed.");
            return route;
        }

        void LosePrimary()
        {
            inputs = [first, second, receive];
            ObserveDeviceRecovery();
        }

        void Stable()
        {
            EvaluateDeviceRecovery();
            smokeRecoveryClock += DeviceRecoveryPolicy.StableReadyMilliseconds;
            EvaluateDeviceRecovery();
        }

        T Control<T>(string id) where T : DependencyObject => Descendants<T>(PageContent)
            .Single(control => AutomationProperties.GetAutomationId(control) == id);

        void OpenControls()
        {
            ShowPage(6);
            UpdateLayout();
            Control<Expander>("automatic-inputs").IsExpanded = true;
            UpdateLayout();
        }

        void Click(string id)
        {
            var button = Control<System.Windows.Controls.Button>(id);
            if (!button.IsEnabled) throw new InvalidOperationException("Disabled automatic-input control: " + id);
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            UpdateLayout();
        }

        void AddThroughPicker(string pickerId, string buttonId, string inputId)
        {
            OpenControls();
            var picker = Control<ComboBox>(pickerId);
            picker.SelectedItem = picker.Items.OfType<AudioDeviceInfo>().Single(input => input.Id == inputId);
            Click(buttonId);
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<WorkspaceState>("{\"SchemaVersion\":2}")!;
            legacy.Normalize();
            Check(!State.AutomaticInputs.UseBackups && State.AutomaticInputs.Backups.Count == 0 &&
                State.AutomaticInputs.ExcludedInputIds.Count == 0 && !legacy.AutomaticInputs.UseBackups &&
                legacy.AutomaticInputs.Backups.Count == 0 && !engine.IsRunning,
                "Old workspaces and new automatic-input preferences start without backup permissions");

            Prepare();
            State.ResumeSameDevice = false;
            State.AutomaticInputs = new();
            OpenControls();
            var backupToggle = Control<CheckBox>("automatic-inputs-enabled");
            var disabledWithoutRecovery = !backupToggle.IsEnabled;
            State.ResumeSameDevice = true;
            OpenControls();
            backupToggle = Control<CheckBox>("automatic-inputs-enabled");
            backupToggle.IsChecked = true;
            backupToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(disabledWithoutRecovery && State.AutomaticInputs.UseBackups && !deviceRecovery.State.HasRunningIntent && !engine.IsRunning,
                "The advanced backup switch requires recovery and does not authorize stopped capture");

            AddThroughPicker("automatic-backup-picker", "automatic-backup-add", first.Id);
            AddThroughPicker("automatic-backup-picker", "automatic-backup-add", second.Id);
            Check(State.AutomaticInputs.Backups.SequenceEqual(new[] { Backup(first), Backup(second) }) && State.InputDeviceId == mic.Id,
                "Adding backups through the UI captures the explicitly selected IDs and formats without selecting them");
            OpenControls();
            Click("automatic-backup-up-1");
            Check(State.AutomaticInputs.Backups.Select(backup => backup.Id).SequenceEqual(new[] { second.Id, first.Id }),
                "The advanced reorder action changes backup priority");
            AddThroughPicker("automatic-exclusion-picker", "automatic-exclusion-add", second.Id);
            Check(State.AutomaticInputs.IsExcluded(second.Id) && State.AutomaticInputs.Backups.Count == 2 && !deviceRecovery.State.HasRunningIntent,
                "The UI exclusion action records an automatic-selection exclusion without starting audio");
            vm.Save();
            var reloaded = new StudioViewModel().State;
            Check(reloaded.AutomaticInputs.UseBackups && reloaded.AutomaticInputs.Backups.Select(backup => backup.Id).SequenceEqual(new[] { second.Id, first.Id }) &&
                reloaded.AutomaticInputs.IsExcluded(second.Id) && !engine.IsRunning,
                "Backup order captured formats and exclusions survive workspace reload");

            Prepare();
            State.AutomaticInputs.ExcludedInputIds.Add(mic.Id);
            var initial = ChooseInitialInput();
            Check(initial?.Id == first.Id && InputForAudioStart(null)?.Id == mic.Id,
                "Initial automatic selection respects exclusions while explicit microphone selection remains usable");

            Arm(false);
            LosePrimary();
            var disabledStarts = smokeRecoveryStarts;
            Stable();
            Check(smokeRecoveryStarts == disabledStarts && State.InputDeviceId == mic.Id && deviceRecovery.State.AuthorizedRoutes.Count == 1,
                "Configured devices cannot be used as backups while the separate backup switch is off");

            Arm();
            var activeStarts = smokeRecoveryStarts;
            ObserveDeviceRecovery(true);
            Stable();
            Check(smokeRecoveryStarts == activeStarts + 1 && smokeRecoveryInputId == mic.Id && State.InputDeviceId == mic.Id,
                "A ready current microphone retains priority over available backups during confirmed recovery");

            Arm();
            LosePrimary();
            outputs = [];
            ObserveDeviceRecovery();
            var sharedStarts = smokeRecoveryStarts;
            Stable();
            Check(smokeRecoveryStarts == sharedStarts && deviceRecovery.State.Pending && State.InputDeviceId == mic.Id,
                "A backup microphone cannot bypass a missing shared virtual-cable endpoint");

            Arm();
            inputs = [first with { SampleRate = 44100 }, receive];
            ObserveDeviceRecovery();
            var formatStarts = smokeRecoveryStarts;
            Stable();
            Check(smokeRecoveryStarts == formatStarts && deviceRecovery.State.Pending && State.InputDeviceId == mic.Id,
                "A configured backup with a changed capture format remains ineligible until explicitly updated");

            Arm();
            State.AutomaticInputs.ExcludedInputIds.Add(first.Id);
            LosePrimary();
            Stable();
            Check(smokeRecoveryInputId == second.Id && State.InputDeviceId == second.Id,
                "Automatic recovery skips an excluded higher-priority backup");

            Arm();
            State.AutomaticInputs.ExcludedInputIds.Add(mic.Id);
            LosePrimary();
            var excludedStarts = smokeRecoveryStarts;
            Stable();
            Check(smokeRecoveryStarts == excludedStarts && deviceRecovery.State.Code == "input_excluded" && !deviceRecovery.State.HasRunningIntent,
                "Excluding the interrupted current microphone revokes automatic recovery authorization");

            Arm();
            State.VolumeLockEnabled = true;
            State.VolumeLockEndpointId = mic.Id;
            smokeVolume = .55;
            smokeInputMuted = false;
            ShowPage(0);
            UpdateLayout();
            var oldSystemVolume = Descendants<Slider>(PageContent)
                .Single(slider => AutomationProperties.GetName(slider) == Loc.Tr("系统音量"));
            string? selectionDuringOpen = null;
            smokeRecoveryDuringStart = () => selectionDuringOpen = State.InputDeviceId;
            LosePrimary();
            var stableStarts = smokeRecoveryStarts;
            EvaluateDeviceRecovery();
            smokeRecoveryClock += 999;
            EvaluateDeviceRecovery();
            var waited = smokeRecoveryStarts == stableStarts;
            smokeRecoveryClock++;
            EvaluateDeviceRecovery();
            smokeRecoveryDuringStart = null;
            Check(waited && smokeRecoveryStarts == stableStarts + 1 && smokeRecoveryInputId == first.Id && selectionDuringOpen == mic.Id &&
                State.InputDeviceId == first.Id && (InputPicker.SelectedItem as AudioDeviceInfo)?.Id == first.Id &&
                !State.VolumeLockEnabled && State.VolumeLockEndpointId == null && !engine.IsRunning,
                "Stable backup recovery opens the exact target before committing input selection and clearing the old volume lock");
            RebuildAutomaticInputView(first.Id, deviceRecovery.State.Generation, engine.SessionId);
            UpdateLayout();
            var newSystemVolume = Descendants<Slider>(PageContent)
                .Single(slider => AutomationProperties.GetName(slider) == Loc.Tr("系统音量"));
            oldSystemVolume.Value = 35;
            var ignoredOldControl = smokeVolume == .55;
            newSystemVolume.Value = 65;
            Check(!ReferenceEquals(oldSystemVolume, newSystemVolume) && ignoredOldControl && smokeVolume == .65 && !engine.IsRunning,
                "After a backup switch detached system-volume controls cannot write to the old microphone and the rebuilt control targets the selected backup");
            inputs = [mic, first, second, receive];
            ObserveDeviceRecovery();
            EvaluateDeviceRecovery();
            Check(State.InputDeviceId == first.Id && smokeRecoveryStarts == stableStarts + 1 && !deviceRecovery.State.Pending,
                "Reappearance of the earlier microphone does not interrupt the selected backup");
            if (!TryReadRecoveryRoute(out var current) || current == null) throw new InvalidOperationException("Missing recovered synthetic route.");
            var remaining = AuthorizedBackupRoutes(current);
            Check(remaining.Count == 1 && remaining[0].InputId == second.Id && deviceRecovery.RememberRunning(current, remaining),
                "Rearming from a selected backup omits its duplicate entry from the next authorization snapshot");

            var obsoleteGeneration = deviceRecovery.State.Generation;
            var contentBeforeCancellation = PageContent.Children[0];
            CancelDeviceRecovery();
            RebuildAutomaticInputView(first.Id, obsoleteGeneration, engine.SessionId);
            Check(ReferenceEquals(contentBeforeCancellation, PageContent.Children[0]) && State.InputDeviceId == first.Id && !engine.IsRunning,
                "A queued input-view rebuild from a cancelled recovery generation leaves the current page untouched");

            Arm();
            LosePrimary();
            var changingStarts = smokeRecoveryStarts;
            EvaluateDeviceRecovery();
            smokeRecoveryClock += 700;
            inputs = [second, receive];
            ObserveDeviceRecovery();
            EvaluateDeviceRecovery();
            smokeRecoveryClock += 999;
            EvaluateDeviceRecovery();
            var restartedWait = smokeRecoveryStarts == changingStarts;
            smokeRecoveryClock++;
            EvaluateDeviceRecovery();
            Check(restartedWait && smokeRecoveryStarts == changingStarts + 1 && State.InputDeviceId == second.Id,
                "Changing the ready backup candidate restarts the full stability wait");

            Arm();
            LosePrimary();
            var returningStarts = smokeRecoveryStarts;
            EvaluateDeviceRecovery();
            smokeRecoveryClock += 500;
            inputs = [mic, first, second, receive];
            ObserveDeviceRecovery();
            EvaluateDeviceRecovery();
            smokeRecoveryClock += 500;
            EvaluateDeviceRecovery();
            Check(smokeRecoveryStarts == returningStarts + 1 && State.InputDeviceId == first.Id,
                "A briefly returning primary input does not starve an already stabilizing backup");

            Arm();
            LosePrimary();
            smokeRecoveryStartSucceeds = false;
            var failedStarts = smokeRecoveryStarts;
            Stable();
            Check(smokeRecoveryStarts == failedStarts + 1 && State.InputDeviceId == mic.Id && deviceRecovery.State.Code == "recovery_failed" && deviceRecovery.State.Paused,
                "A failed backup open preserves the prior selected input and pauses further automatic attempts");

            Arm();
            LosePrimary();
            smokeRecoveryDuringStart = () => CancelDeviceRecovery();
            Stable();
            smokeRecoveryDuringStart = null;
            Check(State.InputDeviceId == mic.Id && !deviceRecovery.State.HasRunningIntent && deviceRecovery.State.Code == "user_stopped",
                "Cancellation while a backup opens prevents target selection from being committed");

            Arm();
            LosePrimary();
            smokeRecoveryDuringStart = () => Interlocked.Exchange(ref recoveryFeedbackBlockedSession, recoveryCaptureSession);
            Stable();
            smokeRecoveryDuringStart = null;
            Check(State.InputDeviceId == mic.Id && !deviceRecovery.State.HasRunningIntent && deviceRecovery.State.Code == "feedback_protection",
                "Late source-session feedback blocks a backup completion and keeps the prior selection");

            Arm();
            State.VolumeLockEnabled = true;
            State.VolumeLockEndpointId = mic.Id;
            LosePrimary();
            smokeRecoveryDuringCommit = () => CancelDeviceRecovery();
            Stable();
            smokeRecoveryDuringCommit = null;
            Check(State.InputDeviceId == mic.Id && State.VolumeLockEnabled && State.VolumeLockEndpointId == mic.Id &&
                !deviceRecovery.State.HasRunningIntent && deviceRecovery.State.Code == "user_stopped",
                "Cancellation during backup selection commit preserves the interrupted input and its volume-lock preference while revoking intent");

            Arm();
            var diagnostics = AutomaticInputDiagnosticDetails() + RecoveryDiagnosticDetails() + SignalPathDiagnosticDetails();
            Check(new[] { mic.Id, first.Id, second.Id, send.Id, receive.Id, mic.Name, first.Name, second.Name }
                .All(privateText => !diagnostics.Contains(privateText, StringComparison.Ordinal)),
                "Automatic-input diagnostics expose counts and states while redacting saved device identities and names");
        }
        finally
        {
            recoveryTimer.Stop();
            deviceRecovery.Disarm("test_complete");
            State.InputDeviceId = savedInput;
            State.CableOutputId = savedCable;
            State.Mode = savedMode;
            State.ResumeSameDevice = savedResume;
            State.AutomaticInputs = savedPreferences;
            State.VolumeLockEnabled = savedVolumeLock;
            State.VolumeLockEndpointId = savedVolumeEndpoint;
            inputs = savedInputs;
            outputs = savedOutputs;
            monitor = savedMonitor;
            headphones = savedHeadphones;
            monitorId = savedMonitorId;
            activeCableSendId = savedSend;
            lastMeter = savedMeter;
            smokeRecoveryClock = savedClock;
            smokeRecoveryStarts = savedStarts;
            smokeRecoveryStartSucceeds = savedSuccess;
            smokeRecoveryInputId = savedRecoveryInput;
            smokeRecoveryDuringStart = null;
            smokeRecoveryDuringCommit = null;
            automaticRecoverySession = savedAutomaticSession;
            automaticInputsExpanded = savedDetailsExpanded;
            suggestedGain = savedSuggestedGain;
            State.DiscordVerifiedAt = savedDiscordVerification;
            smokeVolume = savedSystemVolume;
            smokeInputMuted = savedSystemMute;
            recoveryCaptureSession = recoveryFeedbackBlockedSession = -1;
            Interlocked.Exchange(ref recoverySystemBlocked, 0);
            deviceRecovery = new(() => smokeRecoveryClock);
            deviceRecovery.SetEnabled(savedResume);
            var wasRefreshing = refreshing;
            refreshing = true;
            try
            {
                InputPicker.ItemsSource = savedPickerItems;
                InputPicker.SelectedItem = savedPickerSelection;
            }
            finally { refreshing = wasRefreshing; }
            saveTimer.Stop();
            vm.Save();
            ShowPage(savedPage);
            NoticeBar.Visibility = Visibility.Collapsed;
        }
    }
}
