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
    private readonly string[] pageNames = ["麦克风", "均衡器", "效果", "预设", "设备", "诊断", "设置"];
    private readonly string[] pageIcons = ["\uE80F", "\uE9E9", "\uE9D9", "\uE8D4", "\uE7F4", "\uE9D9", "\uE713"];
    private int currentPage;
    private bool building, refreshing, closed;
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
    private bool forceClose;
    private string applyState = "设置已就绪";
    private static string ProductVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.2.1";

    private Brush Ink => (Brush)FindResource("Ink");
    private Brush Muted => (Brush)FindResource("Muted");
    private Brush Accent => (Brush)FindResource("Accent");
    private Brush Amber => (Brush)FindResource("Amber");
    private bool LocalMode => State.Mode == 1;
    private AudioDeviceInfo? SelectedInput => inputs.FirstOrDefault(d => d.Id == State.InputDeviceId);
    private int SampleRate => engine is { IsRunning: true } && engine.SampleRate > 0 ? engine.SampleRate : SelectedInput?.SampleRate is > 0 ? SelectedInput.SampleRate : 48000;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = vm;
        Width = Math.Min(1280, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 24);
        engine = new(devices);
        driverManager.DownloadProgressChanged += progress => Dispatcher.BeginInvoke(() => { if (!closed && driverStatusText != null) driverStatusText.Text = progress.Percent is { } percent ? $"下载中 {percent}%" : $"已下载 {progress.BytesReceived / 1024} KB"; });
        applied = Draft.Clone();
        apo = ApoInstallation.Detect();
        if (vm.IsFirstRun && !apo.Found) State.Mode = 1;
        if (!LocalMode) applyState = "APO 需在设备页更新";
        for (var i = 0; i < pageNames.Length; i++)
        {
            var page = i;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = pageIcons[i], FontFamily = new("Segoe MDL2 Assets"), Width = 25, FontSize = 16 });
            content.Children.Add(new TextBlock { Text = pageNames[i], Margin = new(8, 0, 0, 0) });
            var button = new WpfButton { Content = content, Style = (Style)FindResource("Nav"), ToolTip = pageNames[i] };
            AutomationProperties.SetName(button, pageNames[i]);
            button.Click += (_, _) => ShowPage(page);
            Navigation.Children.Add(button);
        }
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
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); try { vm.Save(); UpdateChrome(); } catch (Exception ex) { Notice("保存失败：" + ex.Message); } };
        devices.DevicesChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (!closed) { RefreshDevices(); if (currentPage is 4 or 5) ShowPage(currentPage); } });
        engine.MeterUpdated += (_, m) => Dispatcher.BeginInvoke(() => { if (!closed && engine.IsRunning && m.SessionId == engine.SessionId) UpdateMeters(m); });
        engine.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() =>
        {
            if (closed || status.IsRunning != engine.IsRunning || status.SessionId != engine.SessionId) return;
            if (status.ErrorCode != null) Notice(status.Message + "  " + status.ErrorCode);
            if (!status.IsRunning) { activeCableSendId = null; liveTimer.Stop(); applyState = LocalMode ? "设置已就绪" : "APO 需在设备页更新"; lastMeter = null; ClearMeters(); if (calibrating) { calibrating = false; calibrationTimer.Stop(); calibrationPeaks.Clear(); suggestedGain = null; } }
            UpdateChrome(); UpdateLiveLabels();
        });
        engine.RecordingStopped += (_, r) => Dispatcher.BeginInvoke(() => { if (!closed) { Notice($"录音已保存在本机（{r.Duration.TotalSeconds:0.0} 秒），未上传。停止采集后可用耳机回放。"); UpdateLiveLabels(); } });
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
            if (State.MinimizeToTray && !forceClose) { e.Cancel = true; Hide(); EnsureTray(); return; }
            closed = true; saveTimer.Stop(); liveTimer.Stop(); calibrationTimer.Stop();
            lifetime.Cancel();
            try { vm.Save(); } catch { }
            engine.Dispose(); devices.Dispose(); tray?.ContextMenuStrip?.Dispose(); tray?.Dispose(); trayIcon?.Dispose(); driverManager.Dispose();
        };
        ApplyTheme(); ShowPage(0); UpdateCompact();
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
        BypassButton.Content = Draft.Bypass ? "效果开关：关" : "效果开关：开";
        AutomationProperties.SetName(BypassButton, (string)BypassButton.Content);
        BypassButton.Foreground = Draft.Bypass ? Amber : Accent;
        ConfigFooter.Text = applyState;
        CaptureFooter.Text = engine.IsRunning ? (LocalMode ? "●  处理中" : "●  测量中") : SelectedInput is { IsActive: true } ? "已停止" : "设备不可用";
        CaptureFooter.Foreground = engine.IsRunning ? Accent : (Brush)FindResource("SidebarInk");
        if (playerButton != null)
        {
            playerButton.Content = engine.IsRunning ? "■  停止" : LocalMode ? "▶  开始处理" : "▶  开始测量";
            AutomationProperties.SetName(playerButton, engine.IsRunning ? "停止" : LocalMode ? "开始处理" : "开始测量");
            playerButton.IsEnabled = engine.IsRunning || SelectedInput is { IsActive: true };
        }
        DiscordFooter.Text = State.DiscordVerifiedAt is { } time ? "Discord 人工确认 · " + time.ToLocalTime().ToString("MM-dd HH:mm") : "Discord 待验证";
        DiscordFooter.Foreground = State.DiscordVerifiedAt is null ? Amber : Accent;
        if (presetBadge != null)
        {
            presetBadge.Text = "当前预设：" + CurrentPresetName();
        }
        var wasBuilding = building; building = true;
        try { foreach (var refresh in soundControlRefreshers.ToArray()) refresh(); }
        finally { building = wasBuilding; }
    }
    private void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }
    private void InvalidateDiscord() { State.DiscordVerifiedAt = null; UpdateChrome(); }
    private void Notice(string text) { NoticeText.Text = text; NoticeBar.Visibility = Visibility.Visible; }
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
                list.Add(new(State.InputDeviceId, "已保存的麦克风 · 设备已断开", "Disconnected", false, false, false, false, 0, 0));
            InputPicker.ItemsSource = list;
            InputPicker.SelectedItem = list.FirstOrDefault(x => x.Id == State.InputDeviceId);
            SelectCableRoute();
        }
        catch (Exception ex) { Notice($"设备枚举失败：{ex.Message}（0x{ex.HResult:X8}）。可在连接页检查 Windows 麦克风访问设置。管理员权限并非唯一原因。"); }
        finally { refreshing = false; }
    }

    private void StartAudio(bool cable = false)
    {
        Safe(() =>
        {
            // A driver reinstall can recreate endpoint IDs while the app is open.
            // Resolve the current pair before a user starts, not just at first launch.
            if (cable) RefreshDevices();
            if (SelectedInput is not { IsActive: true } input) throw new InvalidOperationException("请选择已连接的麦克风；不会自动改用其他设备。");
            var snapshot = Draft.CreateProcessingSnapshot(SampleRate, out var adjusted);
            if (LocalMode && apo.Found)
            {
                // Known own hook could run twice. Refuse instead of asserting an unverified raw capture path.
                var main = Path.Combine(apo.ConfigDirectory, "config.txt");
                if (File.Exists(main) && File.ReadAllText(main).Contains(ApoConfigService.BeginMarker, StringComparison.Ordinal))
                    throw new InvalidOperationException("检测到 Mic Studio 系统 APO 挂接。请先在连接页移除自有挂接，再使用本地 DSP，避免重复处理。其他 APO 效果也需自行检查。");
            }
            if (cable && !LocalMode) throw new InvalidOperationException("发送虚拟线路需切换到本地 DSP 路线。");
            if (cable)
            {
                var route = SelectCableRoute();
                if (!route.IsReady) throw new InvalidOperationException(route.Message);
            }
            liveTimer.Stop();
            engine.Start(new CaptureOptions { InputDeviceId = input.Id, ProcessLocally = LocalMode, Settings = snapshot, CableOutputDeviceId = cable ? State.CableOutputId : null, MonitorOutputDeviceId = monitor ? monitorId : null, MonitorConfirmedHeadphones = headphones });
            activeCableSendId = cable ? State.CableOutputId : null;
            applied = snapshot; applyState = LocalMode ? "实时调音" : "APO 音频待验证"; UpdateChrome();
            if (adjusted.Count > 0) Notice("已按当前设备采样率适配 EQ：" + string.Join("、", adjusted) + "。原预设参数已保留。");
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
            var extra = excluded.Count > 0 ? "当前路线仅应用增益与 EQ。以下本地效果不写入 APO：" + string.Join("、", excluded) + "。\n\n" : "";
            ShowTextDialog("APO 配置预览", extra + plan.Message + "\n\n" + plan.PreviewText, plan.CanApply ? "应用增益与 EQ" : null, () =>
            {
                var result = new ApoConfigService(apo).Apply(plan);
                Notice(result.Message);
                if (result.Success) { applyState = "APO 配置已保存 · 音频待验证"; applied = Draft.Clone(); InvalidateDiscord(); }
                if (result.NeedsElevation) Notice(result.Message + " 请在设置页选择“请求管理员启动”，然后重新预览并应用。");
                UpdateChrome();
            });
        });
    }

    private void Header(string eyebrow, string title, string description, bool apply = true)
    {
        Add(T(pageNames[currentPage], 24, Ink, FontWeights.SemiBold, new(0, 0, 0, 18)));
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
    private TextBlock T(string text, double size = 13, Brush? brush = null, FontWeight? weight = null, Thickness? margin = null) => new() { Text = text, FontSize = Math.Max(12, size), Foreground = brush ?? Ink, FontWeight = weight ?? FontWeights.Normal, Margin = margin ?? new(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private WpfButton B(string text, Action action, bool primary = false)
    {
        var b = new WpfButton { Content = text, Style = (Style)FindResource(primary ? "Primary" : typeof(WpfButton)) };
        b.Click += (_, _) => Safe(action); AutomationProperties.SetName(b, text); return b;
    }
    private Border Card(string title, string? description, params UIElement[] children)
    {
        var panel = new StackPanel(); panel.Children.Add(T(title, 15, weight: FontWeights.SemiBold));
        foreach (var child in children) panel.Children.Add(child);
        return new Border { Background = (Brush)FindResource("Panel"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new(1), CornerRadius = new(9), Padding = new(18), Margin = new(0, 0, 0, 12), Child = panel, ToolTip = description };
    }
    private WrapPanel Buttons(params WpfButton[] buttons) { var p = new WrapPanel(); foreach (var b in buttons) p.Children.Add(b); return p; }
    private WpfButton ButtonSwitch(string label, bool value, Action<bool> changed, bool enabled = true, Func<bool>? readValue = null)
    {
        var current = value;
        var button = B(label + (current ? "：开" : "：关"), () => { });
        button.IsEnabled = enabled;
        void Update() { current = readValue?.Invoke() ?? current; button.Content = label + (current ? "：开" : "：关"); button.Foreground = current ? Accent : Muted; AutomationProperties.SetName(button, (string)button.Content); }
        button.Click += (_, _) => Safe(() => { if (building) return; var next = !(readValue?.Invoke() ?? current); changed(next); current = next; Update(); });
        if (readValue != null) RegisterSoundControl(button, Update);
        Update(); return button;
    }
    private CheckBox Toggle(string label, bool value, Action<bool> changed, bool enabled = true)
    {
        var c = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = value, IsEnabled = enabled };
        c.Click += (_, _) => { if (!building) changed(c.IsChecked == true); }; return c;
    }
    private StackPanel Parameter(string label, double min, double max, double value, Action<double> set, string unit = "", double step = 0.1, bool enabled = true, Func<double>? readValue = null)
    {
        var p = new StackPanel { Margin = new(0, 2, 0, 12), IsEnabled = enabled };
        string Format(double number) => number.ToString(unit == "%" ? "0" : "0.##", CultureInfo.CurrentCulture);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new(93) });
        grid.Children.Add(T(label + (unit.Length > 0 ? " / " + unit : ""), 12, Muted, margin: new(0)));
        var box = new TextBox { Text = Format(value), HorizontalContentAlignment = HorizontalAlignment.Right, MinWidth = 78 };
        AutomationProperties.SetName(box, label + "数值"); Grid.SetColumn(box, 1); grid.Children.Add(box); p.Children.Add(grid);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), SmallChange = step, LargeChange = step * 10 };
        AutomationProperties.SetName(slider, label);
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
            else { box.BorderBrush = Amber; Notice($"{label}请输入 {min} 至 {max} 的有效数值。"); }
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
        var dialog = new Window { Owner = this, Title = "Mic Studio · " + title, Width = Math.Min(780, SystemParameters.WorkArea.Width - 40), Height = Math.Min(620, SystemParameters.WorkArea.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterOwner, MinWidth = 420, MinHeight = 300 };
        var dock = new DockPanel { Margin = new(22) }; var row = new WrapPanel { Margin = new(0, 15, 0, 0) }; DockPanel.SetDock(row, Dock.Bottom);
        if (actionText != null) row.Children.Add(B(actionText, () => { dialog.Close(); action?.Invoke(); }, true));
        row.Children.Add(B("关闭", dialog.Close)); dock.Children.Add(row);
        dock.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Microsoft YaHei UI"), Padding = new(14) });
        dialog.Content = dock; dialog.ShowDialog();
    }
    private string? Prompt(string title, string initial)
    {
        string? result = null;
        var d = new Window { Owner = this, Title = title, Width = 430, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var p = new StackPanel { Margin = new(24) }; p.Children.Add(T(title, 18)); var input = new TextBox { Text = initial, MaxLength = 60, Margin = new(0, 10, 0, 18) }; p.Children.Add(input);
        p.Children.Add(Buttons(B("保存", () => { if (!string.IsNullOrWhiteSpace(input.Text)) { result = input.Text.Trim(); d.Close(); } }, true), B("取消", d.Close)));
        d.Content = p; d.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; d.ShowDialog(); return result;
    }
    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private void EnsureTray()
    {
        if (tray != null) return;
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/MicStudio;component/Assets/mic-studio.ico"))
            ?? throw new InvalidOperationException("缺少 Mic Studio 图标资源。");
        using (var stream = resource.Stream)
        using (var source = new System.Drawing.Icon(stream, 32, 32))
            trayIcon = (System.Drawing.Icon)source.Clone();
        tray = new() { Text = "Mic Studio · 本地音频状态见主窗口", Icon = trayIcon, Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip(); menu.Items.Add("打开 Mic Studio", null, (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
        menu.Items.Add("停止所有本地音频", null, (_, _) => Dispatcher.Invoke(StopAudio));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() => { forceClose = true; Close(); })); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Show(); Activate(); });
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
