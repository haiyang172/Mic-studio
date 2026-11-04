using System.Text.Json;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    // Runs only with injected adapters. No OS hotkeys, endpoint changes or audio streams.
    private void RunBackgroundControlChecks(Action<bool, string> Check)
    {
        if (!UiSmoke || smokeShortcutNative == null || shortcuts == null || volumeLock == null)
            throw new InvalidOperationException("Background checks require isolated fake adapters.");
        var native = smokeShortcutNative;
        var shortcutService = shortcuts;
        var volumeService = volumeLock;
        var savedInputs = inputs;
        var savedInputId = State.InputDeviceId;
        var savedDraft = Draft.Clone();
        var savedPreset = State.SelectedPresetId;
        var savedCreative = State.CreativeThemeId;
        var savedMode = State.Mode;
        var savedVolume = smokeVolume;
        var writesBefore = smokeVolumeWrites;
        void ClickToggle(string name, bool value)
        {
            ShowPage(6); UpdateLayout();
            var toggle = Descendants<CheckBox>(PageContent).Single(c => (c.Content as TextBlock)?.Text == name);
            if (!toggle.IsEnabled) throw new InvalidOperationException("Disabled background setting: " + name);
            toggle.IsChecked = value;
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        void Emit(ShortcutAction action)
        {
            var binding = State.Shortcuts.Bindings.Single(b => b.Action == action && b.Enabled);
            var registered = native.Registered.Single(p => p.Value.Key == binding.VirtualKey && p.Value.Modifiers == (uint)binding.Modifiers);
            var handled = false;
            ShortcutWindowMessage(shortcutSource!.Handle, GlobalShortcutService.HotkeyMessage, registered.Key,
                (nint)(((uint)binding.VirtualKey << 16) | (uint)binding.Modifiers), ref handled);
            if (!handled) throw new InvalidOperationException("Registered shortcut was not dispatched.");
        }
        void Poll() { smokeShortcutClock += 20; shortcutService.PollKeyState(); }
        void PressPtt()
        {
            native.Down.UnionWith([0x11, 0x12, 0x20]);
            Emit(ShortcutAction.PushToTalk);
        }
        try
        {
            Check(!State.Shortcuts.Enabled && !State.Shortcuts.PushToTalkEnabled && native.Registered.Count == 0 &&
                !State.VolumeLockEnabled && !volumeService.State.Enabled, "Background controls are opt-in and inert on startup");
            State.Mode = 1;
            ClickToggle("启用后台快捷键", true);
            Check(State.Shortcuts.Enabled && native.Registered.Count == 6 && !shortcutService.PttReady,
                "Settings toggle registers ordinary background shortcuts without opening audio");
            Emit(ShortcutAction.ToggleTransmitMute);
            Check(State.TransmitMuted && !engine.IsRunning, "Background mute action changes local send preference without starting capture");
            ShowPage(0); UpdateLayout();
            var muteButton = Descendants<System.Windows.Controls.Button>(PageContent).Single(b => (b.Content as string)?.StartsWith("发送静音：") == true);
            muteButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(!State.TransmitMuted && !engine.IsRunning, "Main-page send mute and shortcut share the same state");
            var bypass = Draft.Bypass;
            Emit(ShortcutAction.ToggleEffects);
            Check(Draft.Bypass != bypass, "Background effects shortcut reaches the live draft");
            vm.Change(s => s.InputGainDb = 0);
            Emit(ShortcutAction.GainUp); var raised = Draft.InputGainDb;
            Emit(ShortcutAction.GainDown);
            Check(raised == 1 && Draft.InputGainDb == 0, "Background gain shortcuts adjust one decibel in both directions");
            vm.Change(s => s.InputGainDb = 30); Emit(ShortcutAction.GainUp);
            var upper = Draft.InputGainDb;
            vm.Change(s => s.InputGainDb = -24); Emit(ShortcutAction.GainDown);
            Check(upper == 30 && Draft.InputGainDb == -24, "Background gain actions respect the supported range");
            var presets = AllPresets().ToList(); LoadPreset(presets[0]); Emit(ShortcutAction.NextPreset);
            Check(State.SelectedPresetId == presets[1 % presets.Count].Id && !engine.IsRunning,
                "Next-preset shortcut loads a sound without opening stopped audio");

            ClickToggle("按住快捷键说话", true);
            Check(State.Shortcuts.PushToTalkEnabled && shortcutService.PttReady && native.Registered.Count == 7 && !SendAllowed,
                "Push-to-talk settings start closed and add the registered talk key");
            Poll(); PressPtt();
            Check(shortcutService.IsPttDown && SendAllowed && !engine.IsRunning && !engine.TransmitEnabled,
                "Fresh talk press reaches the send policy but cannot start a stopped microphone");
            native.Down.Remove(0x20); Poll();
            Check(!shortcutService.IsPttDown && !SendAllowed, "Talk-key release closes the application send policy");
            PressPtt(); native.Down.Remove(0x12); Poll();
            Check(!shortcutService.IsPttDown && !SendAllowed, "Releasing a required modifier closes push-to-talk");
            native.Down.Add(0x12); Emit(ShortcutAction.PushToTalk);
            Check(!SendAllowed, "Restoring a modifier while holding talk does not reopen sending");
            native.Down.Remove(0x20); Poll(); PressPtt(); ResetPttForSafety();
            Emit(ShortcutAction.PushToTalk);
            Check(!SendAllowed, "Application safety reset requires a fresh talk-key release and press");
            native.Down.Remove(0x20); Poll(); PressPtt(); smokeShortcutClock += 350; shortcutService.PollKeyState();
            Emit(ShortcutAction.PushToTalk);
            Check(!SendAllowed, "Stalled shortcut heartbeat cannot reopen sending from a held key");
            native.Down.Remove(0x20); Poll(); PressPtt(); SetTransmitMuted(true);
            Check(State.TransmitMuted && !SendAllowed && !shortcutService.IsPttDown,
                "Manual send mute overrides and resets an active talk press");
            SetTransmitMuted(false); native.Down.Clear(); Poll();
            native.FailRegistration = true; ConfigureShortcuts(); UpdateShortcutStatus();
            Check(!shortcutService.PttReady && !SendAllowed && shortcutService.Status.Issues.Any(i => i.Code == "registration_failed") &&
                shortcutStatusText?.Text.Contains("无法注册", StringComparison.Ordinal) == true,
                "Registration conflicts are visible and leave push-to-talk closed");
            native.FailRegistration = false; ConfigureShortcuts();
            Emit(ShortcutAction.StopSending);
            Check(!engine.IsRunning && !engine.IsRecording && !monitor && !SendAllowed,
                "Stop-local-processing shortcut also resets the talk state");

            inputs = [new("smoke-lock-input", "Synthetic microphone", "Active", false, false, false, false, 48000, 2)];
            State.InputDeviceId = "smoke-lock-input"; smokeVolume = .54;
            ClickToggle("锁定所选麦克风音量", true);
            Check(State.VolumeLockEnabled && State.VolumeLockEndpointId == "smoke-lock-input" && volumeService.State.IsActive &&
                volumeService.State.Target == .54 && smokeVolumeWrites == writesBefore,
                "Volume-lock settings adopt only the selected input's current level without an initial write");
            SetSelectedInputVolume("smoke-lock-input", .63); volumeService.PollNow();
            Check(volumeService.State.Target == .63 && smokeVolume == .63 && smokeVolumeWrites == writesBefore,
                "Own input-volume adjustment becomes the lock target instead of being undone");
            smokeVolume = .2; volumeService.PollNow();
            Check(smokeVolume == .63 && smokeVolumeWrites == writesBefore + 1,
                "Opt-in lock restores an outside volume change through the injected endpoint adapter");
            State.TransmitMuted = true;
            State.Shortcuts.Bindings.Single(b => b.Action == ShortcutAction.GainUp).VirtualKey = 0x75;
            vm.Save();
            var reloaded = new StudioViewModel().State;
            Check(reloaded.Shortcuts.Enabled && reloaded.Shortcuts.PushToTalkEnabled && reloaded.TransmitMuted &&
                reloaded.Shortcuts.Bindings.Single(b => b.Action == ShortcutAction.GainUp).VirtualKey == 0x75 &&
                reloaded.VolumeLockEnabled && reloaded.VolumeLockEndpointId == "smoke-lock-input",
                "Background shortcuts, talk mode, manual mute and exact lock endpoint survive workspace reload");
            volumeService.Disable(); smokeVolume = .41; RestoreVolumeLockPreference();
            Check(volumeService.State.IsActive && volumeService.State.Target == .41 && smokeVolumeWrites == writesBefore + 1,
                "Restored volume-lock preference adopts the fresh current level without replaying an old target");
            DisableVolumeLockForDeviceChange(); State.InputDeviceId = "different-input";
            Check(!State.VolumeLockEnabled && State.VolumeLockEndpointId == null && !volumeService.State.Enabled,
                "Changing the selected microphone clears lock opt-in rather than transferring it");
            State.VolumeLockEnabled = true; State.VolumeLockEndpointId = "smoke-lock-input"; RestoreVolumeLockPreference();
            Check(!volumeService.State.Enabled, "Stored lock preference cannot attach to a different microphone");
            var older = JsonSerializer.Deserialize<WorkspaceState>("{\"SchemaVersion\":2}")!; older.Normalize();
            Check(!older.Shortcuts.Enabled && !older.Shortcuts.PushToTalkEnabled && !older.TransmitMuted && !older.VolumeLockEnabled,
                "Older workspace files migrate with all new background controls disabled");
            ClickToggle("启用后台快捷键", false);
            Check(!State.Shortcuts.Enabled && !State.Shortcuts.PushToTalkEnabled && native.Registered.Count == 0,
                "Disabling background shortcuts unregisters all bindings and exits talk mode");
        }
        finally
        {
            native.FailRegistration = false; native.Down.Clear();
            State.Shortcuts = new(); State.TransmitMuted = false;
            State.VolumeLockEnabled = false; State.VolumeLockEndpointId = null; volumeService.Disable();
            inputs = savedInputs; State.InputDeviceId = savedInputId; State.Mode = savedMode;
            smokeVolume = savedVolume; vm.Replace(savedDraft); State.SelectedPresetId = savedPreset; State.CreativeThemeId = savedCreative;
            ConfigureShortcuts(); saveTimer.Stop(); vm.Save();
            ShowPage(6); NoticeBar.Visibility = Visibility.Collapsed;
        }
    }
}
