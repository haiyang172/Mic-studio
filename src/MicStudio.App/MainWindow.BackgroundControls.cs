using Microsoft.Win32;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Automation;

namespace MicStudio.App;

public partial class MainWindow
{
    private static bool UiSmoke => Environment.GetEnvironmentVariable("MICSTUDIO_UI_SMOKE") == "1";
    private GlobalShortcutService? shortcuts;
    private HwndSource? shortcutSource;
    private bool suppressTransmitUpdates;
    private SmokeShortcutNative? smokeShortcutNative;
    private long smokeShortcutClock = 10000;
    private TextBlock? shortcutStatusText, transmitStatusText;
    private System.Windows.Forms.ToolStripMenuItem? trayTransmitItem;

    private void InitializeBackgroundControls()
    {
        shortcuts = UiSmoke
            ? new GlobalShortcutService(Dispatcher, smokeShortcutNative = new SmokeShortcutNative(), () => smokeShortcutClock, false)
            : new GlobalShortcutService(Dispatcher);
        InitializeVolumeLock();
        shortcuts.ActionTriggered += action => Safe(() => RunShortcutAction(action));
        shortcuts.PttChanged += _ => { ApplyTransmitGate(); UpdateTransmitChrome(); };
        shortcuts.PttLeaseRenewed += ApplyTransmitGate;
        shortcuts.StatusChanged += _ => { ApplyTransmitGate(); UpdateShortcutStatus(); UpdateTransmitChrome(); };
        SourceInitialized += (_, _) =>
        {
            shortcutSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            shortcutSource?.AddHook(ShortcutWindowMessage);
            ConfigureShortcuts(); RestoreVolumeLockPreference();
        };
        Deactivated += (_, _) => ResetPttForSafety();
        SystemEvents.PowerModeChanged += BackgroundPowerChanged;
        SystemEvents.SessionSwitch += BackgroundSessionChanged;
    }

    private void InitializeBackgroundHandle() => new WindowInteropHelper(this).EnsureHandle();

    private nint ShortcutWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!closed && shortcuts?.ProcessWindowMessage(message, wParam, lParam) == true) handled = true;
        return 0;
    }

    private void BackgroundPowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode != PowerModes.StatusChange) Interlocked.Exchange(ref recoverySystemBlocked, 1);
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            ResetPttForSafety();
            if (args.Mode != PowerModes.StatusChange)
            {
                CancelDeviceRecovery("power_changed");
                Safe(StopLoopTuningPreview);
                if (engine.IsRunning && engine.SessionId == automaticRecoverySession) StopLocalAudio(false);
            }
            if (args.Mode == PowerModes.Suspend) volumeLock?.Disable();
            UpdateVolumeLockStatus();
        });
    }
    private void BackgroundSessionChanged(object sender, SessionSwitchEventArgs args)
    {
        Interlocked.Exchange(ref recoverySystemBlocked, 1);
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            ResetPttForSafety(); CancelDeviceRecovery("session_changed");
            Safe(StopLoopTuningPreview);
            if (engine.IsRunning && engine.SessionId == automaticRecoverySession) StopLocalAudio(false);
        });
    }

    private void DisposeBackgroundControls()
    {
        SystemEvents.PowerModeChanged -= BackgroundPowerChanged;
        SystemEvents.SessionSwitch -= BackgroundSessionChanged;
        shortcuts?.Dispose(); shortcuts = null;
        shortcutSource?.RemoveHook(ShortcutWindowMessage); shortcutSource = null;
        volumeLock?.Dispose(); volumeLock = null;
        trayTransmitItem = null;
    }

    private void ConfigureShortcuts()
    {
        if (shortcuts == null || shortcutSource == null || closed) return;
        ResetPttForSafety();
        shortcuts.Configure(shortcutSource.Handle, State.Shortcuts);
        ApplyTransmitGate(); UpdateShortcutStatus(); UpdateTransmitChrome();
    }

    private void ResetPttForSafety(bool forceCloseSend = false)
    {
        suppressTransmitUpdates = true;
        try { shortcuts?.ResetPushToTalk(); }
        finally { suppressTransmitUpdates = false; }
        if ((forceCloseSend || State.Shortcuts.PushToTalkEnabled) && engine.IsRunning)
            engine.SetTransmitEnabled(false, engine.SessionId);
        UpdateTransmitChrome();
    }

    private bool SendAllowed => !State.TransmitMuted && (!State.Shortcuts.PushToTalkEnabled ||
        (State.Shortcuts.Enabled && shortcuts is { PttReady: true, IsPttDown: true }));
    private void ApplyTransmitGate()
    {
        if (suppressTransmitUpdates || closed || !LocalMode || !engine.IsRunning) return;
        var enabled = SendAllowed;
        engine.SetTransmitEnabled(enabled, engine.SessionId, enabled && State.Shortcuts.PushToTalkEnabled ? GlobalShortcutService.PttLeaseMilliseconds : 0);
    }
    private void SetTransmitMuted(bool muted)
    {
        State.TransmitMuted = muted;
        if (muted) ResetPttForSafety();
        ApplyTransmitGate(); SaveSoon(); UpdateChrome();
    }
    private void RunShortcutAction(ShortcutAction action)
    {
        if (!LocalMode)
        {
            Notice(Loc.T("这些快捷操作用于虚拟输入线路，请先切换处理方式。", "These shortcuts control the virtual-input route. Change the processing mode first."));
            return;
        }
        switch (action)
        {
            case ShortcutAction.ToggleTransmitMute: SetTransmitMuted(!State.TransmitMuted); break;
            case ShortcutAction.ToggleEffects: vm.Change(s => s.Bypass = !s.Bypass); break;
            case ShortcutAction.NextPreset:
                var presets = AllPresets().ToList();
                if (presets.Count > 0) LoadPreset(presets[(presets.FindIndex(p => p.Id == State.SelectedPresetId) + 1) % presets.Count]);
                break;
            case ShortcutAction.GainUp: vm.Change(s => s.InputGainDb = Math.Min(30, s.InputGainDb + 1)); break;
            case ShortcutAction.GainDown: vm.Change(s => s.InputGainDb = Math.Max(-24, s.InputGainDb - 1)); break;
            case ShortcutAction.StopSending: StopAudio(); break;
        }
    }

    private UIElement TransmitControls()
    {
        var panel = new StackPanel();
        panel.Children.Add(ButtonSwitch(Loc.T("发送静音", "Mute send"), State.TransmitMuted, SetTransmitMuted, true, () => State.TransmitMuted));
        var text = T("", 12, Muted); transmitStatusText = text;
        text.Loaded += (_, _) => { transmitStatusText = text; UpdateTransmitChrome(); };
        text.Unloaded += (_, _) => { if (ReferenceEquals(transmitStatusText, text)) transmitStatusText = null; };
        panel.Children.Add(text);
        UpdateTransmitChrome(); return panel;
    }
    private string TransmitDescription()
    {
        if (!LocalMode || !engine.IsRunning || activeCableSendId == null)
            return Loc.T("发送静音和按住说话只控制虚拟线路，监听与录音保持独立。", "Mute send and push-to-talk control the virtual cable; monitoring and recording stay separate.");
        if (engine.FeedbackProtectionMuted) return Loc.T("啸叫保护：发送已静音", "Feedback protection: send muted");
        if (State.TransmitMuted) return Loc.T("发送已静音 · 监听与录音保持独立", "Send muted · monitoring and recording stay separate");
        if (State.Shortcuts.PushToTalkEnabled)
        {
            if (shortcuts?.PttReady != true) return Loc.T("按住说话未就绪，发送已静音；请检查快捷键。", "Push-to-talk is not ready; send muted. Check shortcuts.");
            return engine.TransmitEnabled ? Loc.T("按住说话：正在发送", "Push-to-talk: sending")
                : Loc.T("按住说话：等待按键", "Push-to-talk: waiting for key");
        }
        return Loc.T("虚拟线路：正在发送", "Virtual cable: sending");
    }
    private void UpdateTransmitChrome()
    {
        if (transmitStatusText != null) transmitStatusText.Text = TransmitDescription();
        if (LocalMode && engine.IsRunning && activeCableSendId != null && !engine.FeedbackProtectionMuted &&
            (State.TransmitMuted || State.Shortcuts.PushToTalkEnabled))
        {
            CaptureFooter.Text = TransmitDescription();
            CaptureFooter.Foreground = engine.TransmitEnabled ? Accent : Amber;
        }
        UpdateTrayMenu();
        UpdateSignalPath();
    }

    private static string ShortcutName(ShortcutAction action) => action switch
    {
        ShortcutAction.ToggleTransmitMute => Loc.T("发送静音", "Mute send"),
        ShortcutAction.ToggleEffects => Loc.T("效果开关", "Toggle effects"),
        ShortcutAction.NextPreset => Loc.T("下一个预设", "Next preset"),
        ShortcutAction.GainUp => Loc.T("增益 +1 dB", "Gain +1 dB"),
        ShortcutAction.GainDown => Loc.T("增益 −1 dB", "Gain -1 dB"),
        ShortcutAction.StopSending => Loc.T("停止本地处理", "Stop local processing"),
        _ => Loc.T("按住说话", "Push to talk")
    };
    private string ShortcutIssueText(ShortcutIssue issue)
    {
        var detail = issue.Code switch
        {
            "registration_failed" => Loc.T("被其他程序占用或无法注册", "In use by another app or registration failed"),
            "duplicate_binding" or "duplicate_action" => Loc.T("快捷键重复", "Duplicate shortcut"),
            "reserved_key" => Loc.T("系统保留组合，请换一个", "Reserved system shortcut; choose another"),
            "modifier_required" => Loc.T("请加入 Ctrl、Alt 或 Shift", "Add Ctrl, Alt or Shift"),
            "ptt_requires_shortcuts" => Loc.T("请先开启后台快捷键", "Enable background shortcuts first"),
            "ptt_unassigned" => Loc.T("请设置按住说话的快捷键", "Assign a push-to-talk shortcut"),
            _ => Loc.T("快捷键无效或暂不可用", "Shortcut invalid or unavailable")
        };
        return (issue.Action is { } action ? ShortcutName(action) + ": " : "") + detail;
    }
    private void UpdateShortcutStatus()
    {
        if (shortcutStatusText == null) return;
        var status = shortcuts?.Status;
        shortcutStatusText.Text = !State.Shortcuts.Enabled ? Loc.T("后台快捷键已关闭", "Background shortcuts are off")
            : status is null ? Loc.T("快捷键初始化中", "Initializing shortcuts")
            : status.Issues.Count > 0 ? string.Join("\n", status.Issues.Select(ShortcutIssueText))
            : Loc.F("已启用 {0} 个快捷键", "{0} shortcuts enabled", status.RegisteredCount);
        shortcutStatusText.Foreground = status?.Issues.Count > 0 ? Amber : Muted;
    }

    private Border ShortcutSettingsCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Toggle(Loc.T("启用后台快捷键", "Enable background shortcuts"), State.Shortcuts.Enabled, value =>
        {
            State.Shortcuts.Enabled = value;
            if (!value) State.Shortcuts.PushToTalkEnabled = false;
            ConfigureShortcuts(); SaveSoon(); ShowPage(6);
        }));
        panel.Children.Add(Toggle(Loc.T("按住快捷键说话", "Hold shortcut to talk"), State.Shortcuts.PushToTalkEnabled, value =>
        {
            State.Shortcuts.PushToTalkEnabled = value; ConfigureShortcuts(); SaveSoon(); UpdateChrome();
        }, enabled: State.Shortcuts.Enabled));
        panel.Children.Add(T(Loc.T("只控制本软件的虚拟线路发送。停止时按键不会开麦；监听与录音保持独立。", "Controls this app's virtual-cable send. Keys never start a stopped microphone; monitoring and recording stay separate."), 12, Muted));
        var statusText = T("", 12, Muted); shortcutStatusText = statusText;
        statusText.Loaded += (_, _) => { shortcutStatusText = statusText; UpdateShortcutStatus(); };
        statusText.Unloaded += (_, _) => { if (ReferenceEquals(shortcutStatusText, statusText)) shortcutStatusText = null; };
        panel.Children.Add(statusText);
        var editor = new StackPanel();
        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            var binding = State.Shortcuts.Bindings.FirstOrDefault(b => b.Action == action);
            var row = new WrapPanel { Margin = new(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = ShortcutName(action), Width = 155, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
            var edit = B(binding is { Enabled: true } ? binding.DisplayText : Loc.T("未设置", "Unassigned"), () => EditShortcut(action));
            AutomationProperties.SetName(edit, Loc.T("修改快捷键：", "Edit shortcut: ") + ShortcutName(action));
            row.Children.Add(edit);
            row.Children.Add(B(Loc.T("清除", "Clear"), () => { State.Shortcuts.Bindings.RemoveAll(b => b.Action == action); ConfigureShortcuts(); SaveSoon(); ShowPage(6); }));
            editor.Children.Add(row);
        }
        editor.Children.Add(B(Loc.T("恢复默认快捷键", "Restore default shortcuts"), () => { State.Shortcuts.Bindings = ShortcutPreferences.CreateDefaults(); ConfigureShortcuts(); SaveSoon(); ShowPage(6); }));
        panel.Children.Add(new Expander { Header = Loc.T("编辑快捷键", "Edit shortcuts"), Foreground = Ink, Content = editor, Margin = new(0, 8, 0, 0) });
        UpdateShortcutStatus(); return Card(Loc.T("快捷键与按住说话", "Shortcuts and push-to-talk"), null, panel);
    }

    private void EditShortcut(ShortcutAction action)
    {
        shortcuts?.Suspend();
        ShortcutBinding? chosen = null;
        var dialog = new Window { Owner = this, Title = ShortcutName(action), Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        foreach (var resource in new[] { "Bg", "Panel", "Ink", "Muted", "PanelHover", "Line", "Accent", "Amber", "PrimaryInk", "AccentSoft" })
            dialog.Resources[resource] = FindResource(resource);
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(T(Loc.T("按下要使用的组合键", "Press the shortcut you want to use"), 16));
        var keyBox = new TextBox { IsReadOnly = true, Margin = new(0, 12, 0, 12), MinHeight = 42 };
        var message = T("", 12, Amber);
        keyBox.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true; var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { dialog.Close(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var modifiers = ShortcutModifiers.None;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= ShortcutModifiers.Windows;
            var candidate = new ShortcutBinding { Action = action, VirtualKey = KeyInterop.VirtualKeyFromKey(key), Modifiers = modifiers };
            var duplicate = State.Shortcuts.Bindings.Any(b => b.Enabled && b.Action != action && b.VirtualKey == candidate.VirtualKey && b.Modifiers == modifiers);
            if (!candidate.TryValidate(out var code) || duplicate)
            {
                chosen = null; keyBox.Text = candidate.DisplayText;
                message.Text = ShortcutIssueText(new(action, duplicate ? "duplicate_binding" : code)); return;
            }
            chosen = candidate; keyBox.Text = candidate.DisplayText; message.Text = "";
        };
        panel.Children.Add(keyBox); panel.Children.Add(message);
        panel.Children.Add(Buttons(B(Loc.T("保存", "Save"), () =>
        {
            if (chosen == null) return;
            State.Shortcuts.Bindings.RemoveAll(b => b.Action == action); State.Shortcuts.Bindings.Add(chosen); dialog.Close();
        }, true), B(Loc.T("取消", "Cancel"), dialog.Close)));
        dialog.Content = panel; dialog.Loaded += (_, _) => keyBox.Focus();
        try { dialog.ShowDialog(); }
        finally { shortcuts?.Resume(); ConfigureShortcuts(); SaveSoon(); ShowPage(6); }
    }

    private sealed class SmokeShortcutNative : IGlobalShortcutNative
    {
        public Dictionary<int, (uint Modifiers, uint Key)> Registered { get; } = [];
        public HashSet<int> Down { get; } = [];
        public bool FailRegistration { get; set; }
        public bool Register(nint hwnd, int id, uint modifiers, uint key, out int error)
        {
            error = FailRegistration ? 1409 : 0;
            if (FailRegistration) return false;
            Registered[id] = (modifiers & 15, key); return true;
        }
        public void Unregister(nint hwnd, int id) => Registered.Remove(id);
        public bool IsDown(int key) => Down.Contains(key);
    }
}
