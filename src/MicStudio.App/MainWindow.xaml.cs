using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Interop;
using MicStudio.Apo;
using MicStudio.Core;
using MicStudio.WindowsAudio;
using WpfButton = System.Windows.Controls.Button;

namespace MicStudio.App;

public partial class MainWindow : Window
{
    private readonly StudioViewModel vm = new();
    private WorkspaceState State => vm.State;
    private AudioSettings Draft => State.Draft;
    private readonly AudioDeviceService devices = new();
    private readonly AudioEngine engine;
    private ApoInstallation apo;
    private IReadOnlyList<AudioDeviceInfo> inputs = [], outputs = [];
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer calibrationTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly List<double> calibrationPeaks = [];
    private readonly List<(TextBlock Text, ProgressBar Bar, bool Output)> meters = [];
    private readonly List<TextBlock> liveLabels = [];
    private readonly List<Action> soundControlRefreshers = [];
    private readonly string[] pageIcons = ["\uE80F", "\uE9E9", "\uE9D9", "\uE8D4", "\uE7F4", "\uE9D9", "\uE713"];
    private readonly bool startHidden;
    private int currentPage;
    private bool building, refreshing, closed, trayHintShown;
    private AudioSettings applied;
    private AudioMeter? lastMeter;
    private double? suggestedGain;
    private bool calibrating, monitor, headphones;
    private string? monitorId;
    private EqCurve? curve;
    private int selectedBand = 4;
    private StackPanel? bandEditor;
    private WrapPanel? effectCards;
    private TextBlock? presetBadge;
    private WpfButton? playerButton;
    private System.Windows.Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? trayProcessingItem;
    private bool forceClose;
    private string? noticeRawText;
    private string applyState = "设置已就绪";
    private static string ProductVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.4.1";

    private Brush Ink => (Brush)FindResource("Ink");
    private Brush Muted => (Brush)FindResource("Muted");
    private Brush Accent => (Brush)FindResource("Accent");
    private Brush Amber => (Brush)FindResource("Amber");
    private bool LocalMode => State.Mode == 1;
    private string[] PageNames => [.. Enumerable.Range(0, pageIcons.Length).Select(Loc.PageName)];
    private AudioDeviceInfo? SelectedInput => inputs.FirstOrDefault(d => d.Id == State.InputDeviceId);
    private int SampleRate => engine is { IsRunning: true } && engine.SampleRate > 0 ? engine.SampleRate : SelectedInput?.SampleRate is > 0 ? SelectedInput.SampleRate : 48000;

    public MainWindow(bool startHidden = false)
    {
        InitializeComponent();
        this.startHidden = startHidden;
        DataContext = vm;
        Width = Math.Min(1280, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 24);
        engine = new(devices);
        driverManager.DownloadProgressChanged += progress => Dispatcher.BeginInvoke(() => { if (!closed && driverStatusText != null) driverStatusText.Text = progress.Percent is { } percent ? Loc.F("下载中 {0}%", "Downloading {0}%", percent) : Loc.F("已下载 {0} KB", "Downloaded {0} KB", progress.BytesReceived / 1024); });
        applied = Draft.Clone();
        apo = ApoInstallation.Detect();
        if (vm.IsFirstRun && !apo.Found) State.Mode = 1;
        if (!LocalMode) applyState = "APO 需在设备页更新";
        ApplyLanguageChrome();
        // Repair a stale Run value after an update or a moved portable folder.
        if (State.LaunchAtLogin) StartupRegistration.TryApply(true, out _);
        else if (StartupRegistration.IsCurrentExecutableRegistered()) StartupRegistration.TryApply(false, out _);
        RefreshDevices(true);
        ModePicker.SelectedIndex = State.Mode;
        InputPicker.SelectionChanged += (_, _) =>
        {
            if (refreshing || InputPicker.SelectedItem is not AudioDeviceInfo item || item.Id == State.InputDeviceId) return;
            StopAudio(); suggestedGain = null; State.InputDeviceId = item.Id; InvalidateDiscord(); SaveSoon(); ShowPage(currentPage);
        };
        ModePicker.SelectionChanged += (_, _) =>
        {
            if (ModePicker.SelectedIndex == State.Mode) return;
            StopAudio(); suggestedGain = null; State.Mode = ModePicker.SelectedIndex; applyState = LocalMode ? "设置已就绪" : "APO 需在设备页更新"; InvalidateDiscord(); SaveSoon(); ShowPage(currentPage);
        };
        BypassButton.Click += (_, _) => { vm.Change(s => s.Bypass = !s.Bypass); UpdateChrome(); };
        vm.DraftChanged += (_, _) => OnSoundChanged();
        liveTimer.Tick += (_, _) => FlushLiveSettings();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); try { vm.Save(); UpdateChrome(); } catch (Exception ex) { Notice(Loc.T("保存失败：", "Save failed: ") + ex.Message); } };
        devices.DevicesChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (!closed) { RefreshDevices(); if (currentPage is 4 or 5) ShowPage(currentPage); } });
        engine.MeterUpdated += (_, m) => Dispatcher.BeginInvoke(() => { if (!closed && engine.IsRunning && m.SessionId == engine.SessionId) UpdateMeters(m); });
        engine.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() =>
        {
            if (closed || status.IsRunning != engine.IsRunning || status.SessionId != engine.SessionId) return;
            if (status.ErrorCode != null) Notice(Loc.Tr(status.Message) + "  " + status.ErrorCode);
            if (!status.IsRunning) { activeCableSendId = null; liveTimer.Stop(); applyState = LocalMode ? "设置已就绪" : "APO 需在设备页更新"; lastMeter = null; ClearMeters(); if (calibrating) { calibrating = false; calibrationTimer.Stop(); calibrationPeaks.Clear(); suggestedGain = null; } }
            UpdateChrome(); UpdateLiveLabels();
        });
        engine.RecordingStopped += (_, r) => Dispatcher.BeginInvoke(() => { if (!closed) { Notice(Loc.F("录音已保存在本机（{0:0.0} 秒），未上传。停止采集后可用耳机回放。", "Recording saved locally ({0:0.0} s); nothing was uploaded. Play it through headphones after stopping capture.", r.Duration.TotalSeconds)); UpdateLiveLabels(); } });
        calibrationTimer.Tick += (_, _) => CompleteCalibration();
        PreviewKeyDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || Keyboard.FocusedElement is TextBox) return;
            if (e.Key == Key.S) { SavePreset(); e.Handled = true; }
        };
        SizeChanged += (_, _) => UpdateCompact();
        SourceInitialized += (_, _) => { try { var value = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref value, sizeof(int)); } catch (DllNotFoundException) { } };
        Closing += (_, e) =>
        {
            if (State.MinimizeToTray && !forceClose)
            {
                e.Cancel = true;
                try { vm.Save(); } catch { }
                Hide(); EnsureTray();
                if (!trayHintShown) { trayHintShown = true; try { tray?.ShowBalloonTip(4000, "Mic Studio", Loc.T("Mic Studio 仍在后台运行。双击托盘图标可打开窗口，或从托盘菜单退出。", "Mic Studio is still running in the background. Double-click the tray icon to open it, or exit from the tray menu."), System.Windows.Forms.ToolTipIcon.Info); } catch { } }
                return;
            }
            DisposeApplication();
        };
        ApplyTheme(); ShowPage(0); UpdateCompact();
        if (startHidden) { EnsureTray(); Hide(); if (State.LaunchAtLogin && State.AutoStartProcessing) TryAutoStart(); }
        if (vm.RecoveryWarning is not null) Notice(vm.RecoveryWarning);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void UpdateCompact()
    {
        var compact = ActualWidth < 1000;
        NavColumn.Width = new(compact ? 66 : 190);
        Brand.Text = "Mic Studio";
        Brand.FontSize = 20;
        BrandText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BrandHeader.Margin = compact ? new(17, 24, 17, 24) : new(18, 24, 14, 24);
        BrandCaption.Visibility = SideCaption.Visibility = Visibility.Collapsed;
        SideVersion.Text = ProductVersion;
        SideVersion.FontSize = compact ? 9 : 11;
        foreach (WpfButton button in Navigation.Children)
            if (button.Content is StackPanel panel && panel.Children.Count > 1) panel.Children[1].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        InputPicker.Width = ActualWidth < 700 ? 245 : 310;
    }

    internal void ApplyLanguageChrome()
    {
        Title = "Mic Studio";
        InputDeviceLabel.Text = Loc.T("输入设备", "Input device");
        ModeLabel.Text = Loc.T("处理方式", "Processing");
        EffectLabel.Text = Loc.T("效果", "Effects");
        InputPicker.ToolTip = Loc.T("按端点 ID 保存。", "Saved by endpoint ID.");
        BypassButton.ToolTip = Loc.T("控制增益、EQ 和声音效果；与麦克风静音分开。", "Controls gain, EQ and sound effects; separate from microphone mute.");
        if (ModePicker.Items.Count >= 2)
        {
            ((ComboBoxItem)ModePicker.Items[0]).Content = Loc.T("系统输入 · APO", "System input · APO");
            ((ComboBoxItem)ModePicker.Items[1]).Content = Loc.T("虚拟输入 · CABLE", "Virtual input · CABLE");
        }
        BuildNavigation();
        UpdateChrome();
        UpdateCompact();
        BuildTrayMenu();
        if (NoticeBar.Visibility == Visibility.Visible && noticeRawText != null) NoticeText.Text = Loc.Tr(noticeRawText);
    }

    private void BuildNavigation()
    {
        Navigation.Children.Clear();
        for (var i = 0; i < pageIcons.Length; i++)
        {
            var page = i;
            var name = Loc.PageName(i);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = pageIcons[i], FontFamily = new("Segoe MDL2 Assets"), Width = 25, FontSize = 16 });
            content.Children.Add(new TextBlock { Text = name, Margin = new(8, 0, 0, 0) });
            var button = new WpfButton { Content = content, Style = (Style)FindResource("Nav"), ToolTip = name };
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => ShowPage(page);
            Navigation.Children.Add(button);
        }
    }

    internal void SetLanguage(string language)
    {
        if (language is not (UiText.Chinese or UiText.English) || language == State.Language) return;
        State.Language = language;
        UiText.Set(language);
        ApplyLanguageChrome();
        SaveSoon();
        ShowPage(currentPage);
    }

    private void DisposeApplication()
    {
        if (closed) return;
        closed = true; saveTimer.Stop(); liveTimer.Stop(); calibrationTimer.Stop();
        lifetime.Cancel();
        try { vm.Save(); } catch { }
        engine.Dispose(); devices.Dispose();
        tray?.ContextMenuStrip?.Dispose(); tray?.Dispose(); trayIcon?.Dispose(); driverManager.Dispose();
        tray = null; trayIcon = null; trayProcessingItem = null;
    }

    private void TryAutoStart()
    {
        if (!LocalMode) return;
        if (SelectedInput is not { IsActive: true })
        {
            tray?.ShowBalloonTip(5000, "Mic Studio", Loc.T("登录自动开始处理已跳过：没有可用的所选麦克风。", "Automatic processing at sign-in was skipped: the selected microphone is unavailable."), System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }
        try
        {
            StartAudio(LocalMode);
            if (!engine.IsRunning) tray?.ShowBalloonTip(4000, "Mic Studio", Loc.T("自动开始处理未启动；请在主窗口检查设备。", "Automatic processing did not start; check the device in the main window."), System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Notice(Loc.F("登录自动开始处理失败：{0}", "Automatic processing at sign-in failed: {0}", ex.Message));
            tray?.ShowBalloonTip(5000, "Mic Studio", ex.Message, System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private void ShowMainWindow()
    {
        if (closed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleProcessing()
    {
        if (engine.IsRunning) StopAudio(); else StartAudio(LocalMode);
    }

    private void ExitApplication()
    {
        forceClose = true;
        DisposeApplication();
        Close();
        System.Windows.Application.Current?.Shutdown();
    }

    internal void ShowPage(int page)
    {
        building = true; currentPage = page; curve = null; bandEditor = null; effectCards = null; meters.Clear(); liveLabels.Clear(); soundControlRefreshers.Clear(); presetBadge = null; playerButton = null;
        PageContent.Children.Clear(); PageScroll.ScrollToTop();
        for (var i = 0; i < Navigation.Children.Count; i++)
        {
            var b = (WpfButton)Navigation.Children[i];
            b.Foreground = i == page ? Accent : (Brush)FindResource("SidebarInk");
            b.Background = i == page ? (Brush)FindResource("NavSelectedBg") : Brushes.Transparent;
        }
        switch (page) { case 0: BuildOverview(); break; case 1: BuildEqualizer(); break; case 2: BuildEffects(); break; case 3: BuildPresets(); break; case 4: BuildConnections(); break; case 5: BuildDiagnostics(); break; case 6: BuildSettings(); break; }
        building = false; UpdateChrome(); if (lastMeter != null) UpdateMeters(lastMeter); UpdateLiveLabels();
    }

    private void UpdateChrome()
    {
        BypassButton.Content = Draft.Bypass ? Loc.T("效果开关：关", "Effects: Off") : Loc.T("效果开关：开", "Effects: On");
        AutomationProperties.SetName(BypassButton, (string)BypassButton.Content);
        BypassButton.Foreground = Draft.Bypass ? Amber : Accent;
        ConfigFooter.Text = Loc.Tr(applyState);
        CaptureFooter.Text = engine.IsRunning ? (LocalMode ? Loc.T("●  处理中", "●  Processing") : Loc.T("●  测量中", "●  Measuring")) : SelectedInput is { IsActive: true } ? Loc.T("已停止", "Stopped") : Loc.T("设备不可用", "Device unavailable");
        CaptureFooter.Foreground = engine.IsRunning ? Accent : (Brush)FindResource("SidebarInk");
        if (playerButton != null)
        {
            playerButton.Content = engine.IsRunning ? Loc.T("■  停止", "■  Stop") : LocalMode ? Loc.T("▶  开始处理", "▶  Start processing") : Loc.T("▶  开始测量", "▶  Start measuring");
            AutomationProperties.SetName(playerButton, engine.IsRunning ? Loc.T("停止", "Stop") : LocalMode ? Loc.T("开始处理", "Start processing") : Loc.T("开始测量", "Start measuring"));
            playerButton.IsEnabled = engine.IsRunning || SelectedInput is { IsActive: true };
        }
        DiscordFooter.Text = State.DiscordVerifiedAt is { } time ? Loc.T("Discord 人工确认 · ", "Discord manually confirmed · ") + time.ToLocalTime().ToString("MM-dd HH:mm") : Loc.T("Discord 待验证", "Discord unverified");
        DiscordFooter.Foreground = State.DiscordVerifiedAt is null ? Amber : Accent;
        if (presetBadge != null)
        {
            presetBadge.Text = Loc.T("当前预设：", "Preset: ") + CurrentPresetName();
        }
        UpdateTrayMenu();
        var wasBuilding = building; building = true;
        try { foreach (var refresh in soundControlRefreshers.ToArray()) refresh(); }
        finally { building = wasBuilding; }
    }
    private void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }
    private void InvalidateDiscord() { State.DiscordVerifiedAt = null; UpdateChrome(); }
    private void Notice(string text)
    {
        noticeRawText = text; NoticeText.Text = Loc.Tr(text); NoticeBar.Visibility = Visibility.Visible;
        if (!IsVisible && tray != null)
        {
            try { tray.ShowBalloonTip(4000, "Mic Studio", NoticeText.Text, System.Windows.Forms.ToolTipIcon.Warning); } catch { }
        }
    }
    private void DismissNotice(object sender, RoutedEventArgs e) => NoticeBar.Visibility = Visibility.Collapsed;
    private void Safe(Action action) { try { action(); } catch (Exception ex) { Notice(ex.Message); } }
    private void StopAudio()
    {
        activeCableSendId = null;
        liveTimer.Stop();
        calibrating = false; calibrationTimer.Stop();
        engine.Stop(); engine.StopPlayback(); lastMeter = null; ClearMeters(); applyState = LocalMode ? "设置已就绪" : "APO 需在设备页更新"; UpdateChrome(); UpdateLiveLabels();
    }

    private void RefreshDevices(bool initial = false)
    {
        refreshing = true;
        try
        {
            var previous = SelectedInput;
            inputs = devices.EnumerateInputs(); outputs = devices.EnumerateOutputs();
            var current = SelectedInput;
            if (!initial && (previous?.IsActive != current?.IsActive || previous?.SampleRate != current?.SampleRate || previous?.Channels != current?.Channels))
            { suggestedGain = null; InvalidateDiscord(); SaveSoon(); }
            if (initial && State.InputDeviceId is null) State.InputDeviceId = inputs.FirstOrDefault(x => x.IsActive && !x.IsCableReceive && x.IsDefaultCommunications)?.Id ?? inputs.FirstOrDefault(x => x.IsActive && !x.IsCableReceive)?.Id;
            var list = inputs.ToList();
            if (State.InputDeviceId != null && !list.Any(d => d.Id == State.InputDeviceId))
                list.Add(new(State.InputDeviceId, Loc.T("已保存的麦克风 · 设备已断开", "Saved microphone · device disconnected"), "Disconnected", false, false, false, false, 0, 0));
            InputPicker.ItemsSource = list;
            InputPicker.SelectedItem = list.FirstOrDefault(x => x.Id == State.InputDeviceId);
            SelectCableRoute();
        }
        catch (Exception ex) { Notice(Loc.F("设备枚举失败：{0}（0x{1:X8}）。可在连接页检查 Windows 麦克风访问设置。管理员权限并非唯一原因。", "Device enumeration failed: {0} (0x{1:X8}). Check Windows microphone access on Devices. Administrator rights are not the only cause.", ex.Message, ex.HResult)); }
        finally { refreshing = false; }
    }

    private void StartAudio(bool cable = false)
    {
        Safe(() =>
        {
            // A driver reinstall can recreate endpoint IDs while the app is open.
            // Resolve the current pair before a user starts, not just at first launch.
            if (cable) RefreshDevices();
            if (SelectedInput is not { IsActive: true } input) throw new InvalidOperationException(Loc.T("请选择已连接的麦克风；不会自动改用其他设备。", "Select a connected microphone; another device is never substituted automatically."));
            var snapshot = Draft.CreateProcessingSnapshot(SampleRate, out var adjusted);
            if (LocalMode && apo.Found)
            {
                // Known own hook could run twice. Refuse instead of asserting an unverified raw capture path.
                var main = Path.Combine(apo.ConfigDirectory, "config.txt");
                if (File.Exists(main) && File.ReadAllText(main).Contains(ApoConfigService.BeginMarker, StringComparison.Ordinal))
                    throw new InvalidOperationException(Loc.T("检测到 Mic Studio 系统 APO 挂接。请先在连接页移除自有挂接，再使用本地 DSP，避免重复处理。其他 APO 效果也需自行检查。", "A Mic Studio system APO hook was detected. Remove the own hook on Devices before using local DSP to avoid double processing. Check other APO effects separately."));
            }
            if (cable && !LocalMode) throw new InvalidOperationException(Loc.T("发送虚拟线路需切换到本地 DSP 路线。", "Sending to the virtual cable requires the local DSP route."));
            if (cable)
            {
                var route = SelectCableRoute();
                if (!route.IsReady) { cableConnectionWarning = RouteMessage(route); throw new InvalidOperationException(cableConnectionWarning); }
            }
            liveTimer.Stop();
            engine.Start(new CaptureOptions { InputDeviceId = input.Id, ProcessLocally = LocalMode, Settings = snapshot, CableOutputDeviceId = cable ? State.CableOutputId : null, MonitorOutputDeviceId = monitor ? monitorId : null, MonitorConfirmedHeadphones = headphones });
            activeCableSendId = cable ? State.CableOutputId : null;
            applied = snapshot; applyState = LocalMode ? "实时调音" : "APO 音频待验证"; UpdateChrome();
            if (adjusted.Count > 0) Notice(Loc.T("已按当前设备采样率适配 EQ：", "EQ fitted to the current device sample rate: ") + Loc.Join(adjusted) + Loc.T("。原预设参数已保留。", ". The saved preset parameters were preserved."));
        });
    }

    private void ApplyDraft()
    {
        Safe(() =>
        {
            if (LocalMode)
            {
                FlushLiveSettings(); SaveSoon(); return;
            }
            var eqOnly = Draft.CreateProcessingSnapshot(SampleRate, out _);
            var excluded = ApoConfigGenerator.GetUnsupportedEffects(eqOnly);
            eqOnly.Denoise.Enabled = eqOnly.Compressor.Enabled = eqOnly.Gate.Enabled = eqOnly.Limiter.Enabled = false;
            eqOnly.Creative.SaturationEnabled = eqOnly.Creative.DelayEnabled = eqOnly.Creative.ReverbEnabled = eqOnly.Creative.RobotEnabled = false;
            var plan = new ApoConfigService(apo).Preview(eqOnly, State.InputDeviceId ?? "", SampleRate);
            var extra = excluded.Count > 0 ? Loc.T("当前路线仅应用增益与 EQ。以下本地效果不写入 APO：", "This route applies gain and EQ only. These local effects are not written to APO: ") + Loc.Join(excluded) + Loc.T("。\n\n", ".\n\n") : "";
            ShowTextDialog("APO 配置预览", extra + plan.Message + "\n\n" + plan.PreviewText, plan.CanApply ? Loc.T("应用增益与 EQ", "Apply gain and EQ") : null, () =>
            {
                var result = new ApoConfigService(apo).Apply(plan);
                Notice(result.Message);
                if (result.Success) { applyState = "APO 配置已保存 · 音频待验证"; applied = Draft.Clone(); InvalidateDiscord(); }
                if (result.NeedsElevation) Notice(result.Message + Loc.T(" 请在设置页选择“请求管理员启动”，然后重新预览并应用。", " Choose “Restart as administrator” in Settings, then preview and apply again."));
                UpdateChrome();
            });
        });
    }

    private void Header(string eyebrow, string title, string description, bool apply = true)
    {
        Add(T(Loc.PageName(currentPage), 24, Ink, FontWeights.SemiBold, new(0, 0, 0, 18)));
        if (apply)
        {
            var row = new WrapPanel();
            row.Children.Add(B("恢复默认", RestoreDefaults));
            presetBadge = T("", 12, Muted, margin: new(8, 0, 0, 8)); row.Children.Add(presetBadge);
            Add(row);
        }
    }
    private void Add(UIElement element)
    {
        if (currentPage != 2 || element is not Border card) { PageContent.Children.Add(element); return; }
        if (effectCards == null)
        {
            var panel = new WrapPanel { Margin = new(0, 0, -12, 0) }; effectCards = panel; PageContent.Children.Add(panel);
            panel.SizeChanged += (_, _) => { var width = Math.Max(100, panel.ActualWidth); foreach (Border item in panel.Children) item.Width = width >= 730 ? (width - 24) / 2 : width - 12; };
        }
        card.Margin = new(0, 0, 12, 12); effectCards.Children.Add(card);
    }
    private TextBlock T(string text, double size = 13, Brush? brush = null, FontWeight? weight = null, Thickness? margin = null) => new() { Text = Loc.Tr(text), FontSize = Math.Max(12, size), Foreground = brush ?? Ink, FontWeight = weight ?? FontWeights.Normal, Margin = margin ?? new(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private WpfButton B(string text, Action action, bool primary = false)
    {
        var label = Loc.Tr(text);
        var b = new WpfButton { Content = label, Style = (Style)FindResource(primary ? "Primary" : typeof(WpfButton)) };
        b.Click += (_, _) => Safe(action); AutomationProperties.SetName(b, label); return b;
    }
    private Border Card(string title, string? description, params UIElement[] children)
    {
        var panel = new StackPanel(); panel.Children.Add(T(Loc.Tr(title), 15, weight: FontWeights.SemiBold));
        foreach (var child in children) panel.Children.Add(child);
        return new Border { Background = (Brush)FindResource("Panel"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new(1), CornerRadius = new(9), Padding = new(18), Margin = new(0, 0, 0, 12), Child = panel, ToolTip = description is null ? null : Loc.Tr(description) };
    }
    private WrapPanel Buttons(params WpfButton[] buttons) { var p = new WrapPanel(); foreach (var b in buttons) p.Children.Add(b); return p; }
    private WpfButton ButtonSwitch(string label, bool value, Action<bool> changed, bool enabled = true, Func<bool>? readValue = null)
    {
        var current = value;
        var labelText = Loc.Tr(label);
        var button = B(labelText + (current ? Loc.T("：开", "：On") : Loc.T("：关", "：Off")), () => { });
        button.IsEnabled = enabled;
        void Update() { current = readValue?.Invoke() ?? current; button.Content = labelText + (current ? Loc.T("：开", "：On") : Loc.T("：关", "：Off")); button.Foreground = current ? Accent : Muted; AutomationProperties.SetName(button, (string)button.Content); }
        button.Click += (_, _) => Safe(() => { if (building) return; var next = !(readValue?.Invoke() ?? current); changed(next); current = next; Update(); });
        if (readValue != null) RegisterSoundControl(button, Update);
        Update(); return button;
    }
    private CheckBox Toggle(string label, bool value, Action<bool> changed, bool enabled = true)
    {
        var c = new CheckBox { Content = new TextBlock { Text = Loc.Tr(label), TextWrapping = TextWrapping.Wrap }, IsChecked = value, IsEnabled = enabled };
        c.Click += (_, _) => { if (!building) changed(c.IsChecked == true); }; return c;
    }
    private StackPanel Parameter(string label, double min, double max, double value, Action<double> set, string unit = "", double step = 0.1, bool enabled = true, Func<double>? readValue = null)
    {
        var labelText = Loc.Tr(label);
        var p = new StackPanel { Margin = new(0, 2, 0, 12), IsEnabled = enabled };
        string Format(double number) => number.ToString(unit == "%" ? "0" : "0.##", CultureInfo.CurrentCulture);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new(93) });
        grid.Children.Add(T(labelText + (unit.Length > 0 ? " / " + unit : ""), 12, Muted, margin: new(0)));
        var box = new TextBox { Text = Format(value), HorizontalContentAlignment = HorizontalAlignment.Right, MinWidth = 78 };
        AutomationProperties.SetName(box, labelText + Loc.T("数值", " value")); Grid.SetColumn(box, 1); grid.Children.Add(box); p.Children.Add(grid);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), SmallChange = step, LargeChange = step * 10 };
        AutomationProperties.SetName(slider, labelText);
        var sync = false;
        var committed = value;
        var displayed = box.Text;
        slider.ValueChanged += (_, _) =>
        {
            if (sync || building) return;
            var v = Math.Round(slider.Value / step) * step; v = Math.Clamp(v, min, max);
            displayed = Format(v); box.Text = displayed; if (Math.Abs(v - committed) > 0.000001) { committed = v; set(v); }
        };
        void Commit()
        {
            if (building || box.Text == displayed) return;
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && double.IsFinite(v) && v >= min && v <= max)
            { sync = true; slider.Value = v; sync = false; if (Math.Abs(v - committed) > 0.000001) { committed = v; set(v); } displayed = box.Text; box.BorderBrush = (Brush)FindResource("Line"); }
            else { box.BorderBrush = Amber; Notice(Loc.F("{0}请输入 {1} 至 {2} 的有效数值。", "Enter a valid {0} value from {1} to {2}.", labelText, min, max)); }
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        if (readValue != null) RegisterSoundControl(p, () =>
        {
            var latest = readValue();
            if (Math.Abs(latest - committed) < .000001) return;
            sync = true; committed = latest; slider.Value = Math.Clamp(latest, min, max); displayed = Format(latest); box.Text = displayed; sync = false;
        });
        p.Children.Add(slider); return p;
    }
    private void RegisterSoundControl(FrameworkElement element, Action refresh)
    {
        soundControlRefreshers.Add(refresh);
        element.Loaded += (_, _) => { if (!soundControlRefreshers.Contains(refresh)) soundControlRefreshers.Add(refresh); };
        element.Unloaded += (_, _) => soundControlRefreshers.Remove(refresh);
    }
    private void ShowTextDialog(string title, string text, string? actionText = null, Action? action = null)
    {
        var dialog = new Window { Owner = this, Title = "Mic Studio · " + Loc.Tr(title), Width = Math.Min(780, SystemParameters.WorkArea.Width - 40), Height = Math.Min(620, SystemParameters.WorkArea.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterOwner, MinWidth = 420, MinHeight = 300 };
        var dock = new DockPanel { Margin = new(22) }; var row = new WrapPanel { Margin = new(0, 15, 0, 0) }; DockPanel.SetDock(row, Dock.Bottom);
        if (actionText != null) row.Children.Add(B(actionText, () => { dialog.Close(); action?.Invoke(); }, true));
        row.Children.Add(B("关闭", dialog.Close)); dock.Children.Add(row);
        dock.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Microsoft YaHei UI, Segoe UI"), Padding = new(14) });
        dialog.Content = dock; dialog.ShowDialog();
    }
    private string? Prompt(string title, string initial)
    {
        string? result = null;
        var dialogTitle = Loc.Tr(title);
        var d = new Window { Owner = this, Title = dialogTitle, Width = 430, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var p = new StackPanel { Margin = new(24) }; p.Children.Add(T(dialogTitle, 18)); var input = new TextBox { Text = initial, MaxLength = 60, Margin = new(0, 10, 0, 18) }; p.Children.Add(input);
        p.Children.Add(Buttons(B("保存", () => { if (!string.IsNullOrWhiteSpace(input.Text)) { result = input.Text.Trim(); d.Close(); } }, true), B("取消", d.Close)));
        d.Content = p; d.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; d.ShowDialog(); return result;
    }
    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private void EnsureTray()
    {
        if (tray != null) return;
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/MicStudio;component/Assets/mic-studio.ico"))
            ?? throw new InvalidOperationException(Loc.T("缺少 Mic Studio 图标资源。", "The Mic Studio icon resource is missing."));
        using (var stream = resource.Stream)
        using (var source = new System.Drawing.Icon(stream, 32, 32))
            trayIcon = (System.Drawing.Icon)source.Clone();
        tray = new() { Text = Loc.T("Mic Studio · 后台运行", "Mic Studio · background"), Icon = trayIcon, Visible = true };
        BuildTrayMenu();
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }
    private void BuildTrayMenu()
    {
        if (tray == null) return;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("打开 Mic Studio", "Open Mic Studio"), null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        trayProcessingItem = new System.Windows.Forms.ToolStripMenuItem("", null, (_, _) => Dispatcher.Invoke(ToggleProcessing));
        menu.Items.Add(trayProcessingItem);
        menu.Items.Add(Loc.T("停止所有本地音频", "Stop all local audio"), null, (_, _) => Dispatcher.Invoke(StopAudio));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("退出", "Exit"), null, (_, _) => Dispatcher.Invoke(ExitApplication));
        menu.Opening += (_, _) => UpdateTrayMenu();
        var previous = tray.ContextMenuStrip; tray.ContextMenuStrip = menu; previous?.Dispose();
        UpdateTrayMenu();
    }
    private void UpdateTrayMenu()
    {
        if (tray == null) return;
        tray.Text = engine.IsRunning
            ? Loc.T("Mic Studio · 正在处理本地音频", "Mic Studio · processing local audio")
            : Loc.T("Mic Studio · 后台运行", "Mic Studio · background");
        if (trayProcessingItem == null) return;
        trayProcessingItem.Text = engine.IsRunning ? Loc.T("暂停处理", "Pause processing") : Loc.T("继续处理", "Resume processing");
        trayProcessingItem.Enabled = engine.IsRunning || SelectedInput is { IsActive: true };
    }
    private void RestartElevated()
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) { Notice("当前已在管理员权限下运行。"); return; }
        vm.Save(); StopAudio();
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory }); forceClose = true; Close(); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { Notice("UAC 已取消。草稿已保留，可继续普通权限操作。"); }
    }
    private void ApplyTheme()
    {
        if (State.Theme == "light")
        {
            Resources["Bg"] = new SolidColorBrush(Color.FromRgb(243, 246, 245)); Resources["Panel"] = Brushes.White; Resources["Ink"] = new SolidColorBrush(Color.FromRgb(26, 45, 39)); Resources["Muted"] = new SolidColorBrush(Color.FromRgb(91, 111, 105));
            Resources["PanelHover"] = new SolidColorBrush(Color.FromRgb(230, 238, 233)); Resources["Line"] = new SolidColorBrush(Color.FromRgb(201, 214, 207)); Resources["Accent"] = new SolidColorBrush(Color.FromRgb(24, 116, 85)); Resources["Amber"] = new SolidColorBrush(Color.FromRgb(143, 89, 27)); Resources["PrimaryInk"] = Brushes.White;
            Resources["SidebarBg"] = new SolidColorBrush(Color.FromRgb(226, 235, 229)); Resources["SidebarInk"] = new SolidColorBrush(Color.FromRgb(49, 68, 59)); Resources["NavSelectedBg"] = new SolidColorBrush(Color.FromRgb(200, 230, 214)); Resources["NoticeBg"] = new SolidColorBrush(Color.FromRgb(223, 240, 230));
        }
        else foreach (var key in new[] { "Bg", "Panel", "Ink", "Muted", "PanelHover", "Line", "Accent", "Amber", "PrimaryInk", "SidebarBg", "SidebarInk", "NavSelectedBg", "NoticeBg" }) Resources.Remove(key);
        Background = (Brush)FindResource("Bg"); Foreground = Ink;
        CaptureFooter.Foreground = engine.IsRunning ? Accent : (Brush)FindResource("SidebarInk");
    }
}
