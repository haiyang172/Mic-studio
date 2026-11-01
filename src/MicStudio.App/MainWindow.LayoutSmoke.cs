using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using MicStudio.Core;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    internal async Task RunLayoutSmoke(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        RunControlEventChecks(directory);
        var results = new List<object>();
        // No capture, monitoring, recording, configuration writes, or OS DPI changes.
        foreach (var size in new[] { (W: 1280, H: 820), (W: 900, H: 600), (W: 600, H: 500) })
        {
            Width = Math.Min(size.W, SystemParameters.WorkArea.Width); Height = Math.Min(size.H, SystemParameters.WorkArea.Height);
            for (var page = 0; page < 7; page++)
            {
                ShowPage(page);
                foreach (var details in page is 1 or 2 ? new[] { false, true } : new[] { false })
                {
                foreach (var expander in Descendants<Expander>(PageContent).Where(e => e.Header as string == "高级功能")) expander.IsExpanded = details;
                await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render); await Task.Delay(50);
                var rightOverflow = PageContent.ActualWidth > PageScroll.ViewportWidth + 3;
                var buttons = Descendants<System.Windows.Controls.Button>(PageContent).Count();
                PageScroll.ScrollToEnd(); await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
                var reachable = PageScroll.ExtentHeight <= PageScroll.ViewportHeight + 3 || Math.Abs(PageScroll.VerticalOffset - PageScroll.ScrollableHeight) < 3;
                PageScroll.ScrollToTop(); await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
                results.Add(new { size.W, size.H, Page = PageNames[page], Advanced = details, PageContent.ActualWidth, PageScroll.ViewportWidth, PageScroll.ExtentHeight, PageScroll.ViewportHeight, ScrollReachable = reachable, RightOverflow = rightOverflow, Buttons = buttons });
                if (!reachable || rightOverflow) throw new InvalidOperationException($"窗口内容无法完整访问：{PageNames[page]} {size.W}x{size.H} advanced={details}; width={PageContent.ActualWidth:0.0}, viewport={PageScroll.ViewportWidth:0.0}; scroll={reachable}");
                var visual = (Visual)Content;
                var bitmap = new RenderTargetBitmap((int)((FrameworkElement)Content).ActualWidth, (int)((FrameworkElement)Content).ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"{size.W}x{size.H}-{page + 1}{(details ? "-advanced" : "")}.png")); encoder.Save(file);
                }
            }
        }
        File.WriteAllText(Path.Combine(directory, "layout-report.json"), JsonSerializer.Serialize(new { Method = "Actual WPF window layout at 3 DIP sizes, all 7 pages plus expanded EQ/effects; OS DPI unchanged. Manual UI interaction separate.", Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        State.Theme = "light"; ApplyTheme(); Width = 1280; Height = 820;
        foreach (var page in new[] { 0, 2, 4, 6 })
        {
            ShowPage(page); await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
            var light = new RenderTargetBitmap((int)((FrameworkElement)Content).ActualWidth, (int)((FrameworkElement)Content).ActualHeight, 96, 96, PixelFormats.Pbgra32); light.Render((Visual)Content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(light));
            using var file = File.Create(Path.Combine(directory, $"light-{page + 1}.png")); encoder.Save(file);
        }
        RunEnglishUiCheck(directory);
        forceClose = true;
    }

    /// <summary>
    /// Walks the real English UI and records any remaining Chinese text. This is a
    /// presentation check only; it does not start audio or change user files.
    /// </summary>
    private void RunEnglishUiCheck(string directory)
    {
        var missing = new List<object>();
        var pages = new List<object>();
        void Inspect(string? text, string where)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.Any(IsHan)) return;
            missing.Add(new { where, text });
        }
        static bool IsHan(char c) => c is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';
        SetLanguage(UiText.English);
        NoticeBar.Visibility = Visibility.Collapsed;
        for (var page = 0; page < 7; page++)
        {
            ShowPage(page);
            foreach (var details in page is 1 or 2 ? new[] { false, true } : new[] { false })
            {
                foreach (var expander in Descendants<Expander>(PageContent).Where(e => e.Header as string == Loc.T("高级功能", "Advanced"))) expander.IsExpanded = details;
                UpdateLayout();
                foreach (var text in Descendants<TextBlock>(PageContent))
                {
                    if (text.DataContext is AudioDeviceInfo) continue;
                    Inspect(text.Text, $"page {page} advanced={details} TextBlock");
                }
                foreach (var button in Descendants<System.Windows.Controls.Button>(PageContent)) Inspect(button.Content as string, $"page {page} advanced={details} Button");
                foreach (var check in Descendants<CheckBox>(PageContent)) Inspect((check.Content as TextBlock)?.Text, $"page {page} advanced={details} CheckBox");
                foreach (var expander in Descendants<Expander>(PageContent)) Inspect(expander.Header as string, $"page {page} advanced={details} Expander");
            }
            pages.Add(new { page, Name = Loc.PageName(page) });
        }
        Inspect(Title, "window title");
        Inspect(InputDeviceLabel.Text, "input label");
        Inspect(ModeLabel.Text, "mode label");
        Inspect(EffectLabel.Text, "effect label");
        Inspect(BypassButton.Content as string, "effects switch");
        Inspect(CaptureFooter.Text, "capture footer");
        Inspect(ConfigFooter.Text, "config footer");
        foreach (System.Windows.Controls.Button button in Navigation.Children)
            if (button.Content is StackPanel panel && panel.Children.Count > 1 && panel.Children[1] is TextBlock label) Inspect(label.Text, "navigation");
        for (var index = 0; index < ModePicker.Items.Count; index++) Inspect((ModePicker.Items[index] as ComboBoxItem)?.Content as string, "mode item " + index);
        if (NoticeBar.Visibility == Visibility.Visible) Inspect(NoticeText.Text, "notice");

        Width = 1280; Height = 820;
        State.Theme = "dark"; ApplyTheme();
        var englishLayouts = new List<object>();
        var englishFailures = 0;
        foreach (var size in new[] { (W: 1280, H: 820), (W: 900, H: 600), (W: 600, H: 500) })
        {
            Width = Math.Min(size.W, SystemParameters.WorkArea.Width); Height = Math.Min(size.H, SystemParameters.WorkArea.Height);
            for (var page = 0; page < 7; page++)
            {
                foreach (var details in page is 1 or 2 ? new[] { false, true } : new[] { false })
                {
                    ShowPage(page);
                    foreach (var expander in Descendants<Expander>(PageContent).Where(e => e.Header as string == Loc.T("高级功能", "Advanced"))) expander.IsExpanded = details;
                    UpdateLayout();
                    var rightOverflow = PageContent.ActualWidth > PageScroll.ViewportWidth + 3;
                    PageScroll.ScrollToEnd(); UpdateLayout();
                    var reachable = PageScroll.ExtentHeight <= PageScroll.ViewportHeight + 3 || Math.Abs(PageScroll.VerticalOffset - PageScroll.ScrollableHeight) < 3;
                    PageScroll.ScrollToTop(); UpdateLayout();
                    if (rightOverflow || !reachable) englishFailures++;
                    englishLayouts.Add(new { size.W, size.H, Page = Loc.PageName(page), Advanced = details, RightOverflow = rightOverflow, ScrollReachable = reachable });
                    if (size.W == 1280 && !details)
                    {
                        var bitmap = new RenderTargetBitmap((int)((FrameworkElement)Content).ActualWidth, (int)((FrameworkElement)Content).ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render((Visual)Content);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(directory, $"en-{page + 1}.png")); encoder.Save(file);
                    }
                }
            }
        }
        Width = 1280; Height = 820;
        foreach (var page in new[] { 0, 1, 2, 3, 4, 5, 6 }) { ShowPage(page); UpdateLayout(); foreach (var text in Descendants<TextBlock>(PageContent)) { if (text.DataContext is AudioDeviceInfo) continue; Inspect(text.Text, $"page {page} screenshot TextBlock"); } foreach (var button in Descendants<System.Windows.Controls.Button>(PageContent)) Inspect(button.Content as string, $"page {page} screenshot Button"); }
        SetLanguage(UiText.Chinese);
        File.WriteAllText(Path.Combine(directory, "english-ui.json"), JsonSerializer.Serialize(new
        {
            Method = "Actual WPF pages rebuilt in en-US; all TextBlock/Button/CheckBox/Expander labels and shell text inspected for Han characters. Device names supplied by the OS are excluded.",
            Pages = pages,
            ChineseTextCount = missing.Count,
            Missing = missing,
            Layouts = englishLayouts,
            LayoutFailures = englishFailures
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void RunControlEventChecks(string directory)
    {
        var checks = new List<string>();
        void Check(bool valid, string name) { if (!valid) throw new InvalidOperationException("WPF control check failed: " + name); checks.Add(name); }
        Check(!engine.IsRunning && !engine.IsRecording && !monitor, "Startup does not open audio");
        Check(StudioViewModel.DataDirectory.Contains("test-state-", StringComparison.Ordinal), "UI diagnostic workspace is always isolated");
        calibrating = true; calibrationPeaks.Add(-30); suggestedGain = 18; calibrationTimer.Start(); CancelCalibration();
        Check(!calibrating && !calibrationTimer.IsEnabled && calibrationPeaks.Count == 0 && suggestedGain == null, "System input changes cancel mixed-baseline calibration");
        State.Mode = 1; ModePicker.SelectedIndex = 1; vm.Replace(new AudioSettings()); ShowPage(0); UpdateLayout();
        var gain = Descendants<TextBox>(PageContent).Single(t => AutomationProperties.GetName(t) == "输入增益数值");
        gain.Focus(); gain.Text = "6.5";
        gain.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        Check(Math.Abs(Draft.InputGainDb - 6.5) < .001, "Numeric gain Enter commits actual UI handler");
        gain.Text = "NaN";
        gain.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        Check(Math.Abs(Draft.InputGainDb - 6.5) < .001, "Non-finite numeric input rejected");
        gain.Text = "6.5";
        Check(!Descendants<System.Windows.Controls.Button>(PageContent).Any(b => b.Content as string is "应用" or "撤销" or "重做"), "Ordinary toolbar only offers restore defaults");
        Check(Descendants<System.Windows.Controls.Button>(PageContent).Count(b => AutomationProperties.GetName(b) is "开始处理" or "停止") == 1, "Single player control while stopped");
        BypassButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(Draft.Bypass && BypassButton.Content as string == "效果开关：关", "Effects off correctly enables DSP bypass without microphone mute");
        BypassButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(!Draft.Bypass && BypassButton.Content as string == "效果开关：开", "Effects on correctly disables DSP bypass");
        ShowPage(1); UpdateLayout();
        Check(!Descendants<CheckBox>(PageContent).Any(c => (c.Content as TextBlock)?.Text == "启用此频段"), "No per-band activation prerequisite");
        var advanced = Descendants<Expander>(PageContent).Single(e => e.Header as string == "高级功能");
        Check(!advanced.IsExpanded && curve is { AllowEditing: false }, "EQ details collapsed and curve read-only by default");
        advanced.IsExpanded = true;
        Check(curve is { AllowEditing: true }, "Advanced expansion enables curve editing");
        advanced.IsExpanded = false;
        Check(curve is { AllowEditing: false }, "Closing advanced leaves parameters and disables curve dragging");
        var eqToggle = Descendants<System.Windows.Controls.Button>(PageContent).Single(b => (b.Content as string)?.StartsWith("EQ 均衡：") == true);
        eqToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(!Draft.EqEnabled, "Master EQ off controls the actual setting");
        var eqGain = Descendants<Slider>(PageContent).Single(s => AutomationProperties.GetName(s) == Draft.Bands[1].Name);
        eqGain.Value = 2;
        Check(Draft.EqEnabled && Draft.Bands[1].Enabled && Math.Abs(Draft.Bands[1].GainDb - 2) < .001, "Ordinary gain directly enables and edits the band");
        eqToggle = Descendants<System.Windows.Controls.Button>(PageContent).Single(b => (b.Content as string)?.StartsWith("EQ 均衡：") == true);
        Check(eqToggle.Content as string == "EQ 均衡：开", "EQ switch refreshes after another control enables EQ");
        var nonEq = Draft.Clone();
        LoadEqPreset(BuiltInPresets.All.Single(p => p.Id == "natural"));
        var nonEqAfter = Draft.Clone(); nonEqAfter.Bands = nonEq.Bands; nonEqAfter.EqEnabled = nonEq.EqEnabled;
        Check(SettingsJson(nonEqAfter) == SettingsJson(nonEq), "EQ theme preserves gain and all other processing settings");
        eqGain = Descendants<Slider>(PageContent).Single(s => AutomationProperties.GetName(s) == Draft.Bands[1].Name); eqGain.Value = 3;
        var eqPicker = Descendants<ComboBox>(PageContent).Single(c => AutomationProperties.GetName(c) == "EQ 主题");
        Check((eqPicker.SelectedItem as ComboBoxItem)?.Content as string == "自定义" && CurrentPresetName() == "自定义", "Editing a theme refreshes both preset indicators to custom");
        LoadPreset(BuiltInPresets.All.Single(p => p.Id == "natural"));
        Check(State.SelectedPresetId == "natural" && Draft.Bands[0].Enabled, "Preset loads actual draft without audio");
        Check(CurrentPresetName() == "自然", "Unmodified preset shows its name");
        vm.Change(s => { s.Creative.RobotEnabled = true; s.Creative.DelayEnabled = true; s.Creative.DelayMix = .23; }); State.CreativeThemeId = "custom"; ShowPage(2); UpdateLayout();
        var autoNoise = Descendants<CheckBox>(PageContent).Single(c => (c.Content as TextBlock)?.Text == "自动跟踪底噪");
        autoNoise.IsChecked = true; autoNoise.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(Draft.Denoise.AutoNoise, "Automatic noise tracking toggle reaches the draft");
        var voicePriority = Descendants<CheckBox>(PageContent).Single(c => (c.Content as TextBlock)?.Text == "语音优先（保留轻声与尾音）");
        voicePriority.IsChecked = true; voicePriority.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(Draft.Denoise.VoicePriority, "Voice priority toggle reaches the draft");
        var creativePicker = Descendants<ComboBox>(PageContent).Single(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Content as string == "氢气音"));
        var heliumIndex = creativePicker.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => i.Content as string == "氢气音");
        creativePicker.SelectedIndex = heliumIndex;
        Check(Math.Abs(Draft.Creative.PitchSemitones) > 0 && State.CreativeThemeId == "helium", "Helium theme applies a real pitch shift");
        creativePicker.SelectedItem = creativePicker.Items.OfType<ComboBoxItem>().Single(i => i.Content as string == "电话 / 电台");
        Check(Draft.Creative.PhoneEnabled && Draft.Creative.PitchSemitones == 0, "Phone theme enables the band-shaped voice and clears pitch");
        vm.Change(s => { s.Creative.RobotEnabled = true; s.Creative.DelayEnabled = true; s.Creative.DelayMix = .23; }); State.CreativeThemeId = "custom"; ShowPage(2); UpdateLayout();
        var ratioBeforeFocus = Draft.Compressor.Ratio;
        var strength = Descendants<TextBox>(PageContent).Where(t => AutomationProperties.GetName(t) == "强度数值").Skip(1).First();
        strength.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        Check(Draft.Compressor.Ratio == ratioBeforeFocus, "Rounded percentage display never changes precise settings without an edit");
        var creativeToggle = Descendants<System.Windows.Controls.Button>(PageContent).Single(b => (b.Content as string)?.StartsWith("创意声音：") == true);
        creativeToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(!Draft.Creative.RobotEnabled && !Draft.Creative.DelayEnabled, "Creative master disables the active combination");
        creativeToggle = Descendants<System.Windows.Controls.Button>(PageContent).Single(b => (b.Content as string)?.StartsWith("创意声音：") == true);
        creativeToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(Draft.Creative.RobotEnabled && Draft.Creative.DelayEnabled && Math.Abs(Draft.Creative.DelayMix - .23) < .001 && State.CreativeThemeId == "custom", "Creative off/on preserves custom combination and parameters");
        var personal = new SoundPreset { Id = "smoke-personal", Name = "保留个人主题", Settings = Draft.Clone() }; State.Presets.Add(personal);
        var inputId = State.InputDeviceId; var cableId = State.CableOutputId; var theme = State.Theme; var trayOption = State.MinimizeToTray;
        var reset = Descendants<System.Windows.Controls.Button>(PageContent).Single(b => b.Content as string == "恢复默认"); reset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(SettingsJson(Draft) == SettingsJson(AudioSettings.CreateDefaultForSampleRate(SampleRate)) && CurrentPresetName() == "无", "Restore defaults resets audio settings and preset status");
        Check(State.Presets.Contains(personal) && State.InputDeviceId == inputId && State.CableOutputId == cableId && State.Theme == theme && State.MinimizeToTray == trayOption, "Restore preserves personal themes, devices and preferences");
        vm.Change(s => { s.EqEnabled = false; s.Bands[4].Enabled = false; s.Bands[4].GainDb = 12; }); vm.Save();
        var reloaded = new StudioViewModel();
        Check(!reloaded.State.Draft.EqEnabled && !reloaded.State.Draft.Bands[4].Enabled && reloaded.State.Draft.Bands[4].GainDb == 12, "Persistence preserves master EQ and old disabled boost");
        ShowPage(4); UpdateLayout();
        Check(Descendants<System.Windows.Controls.Button>(PageContent).Any(b => b.Content as string == "重新下载"), "Driver re-download command is reachable");
        var realInputs = inputs; var realOutputs = outputs; var savedCable = State.CableOutputId; var savedMic = State.InputDeviceId;
        inputs = [new("test-receive", "CABLE Output (VB-Audio Virtual Cable)", "Active", false, false, false, true, 48000, 2)];
        outputs = [new("test-send", "CABLE Input (VB-Audio Virtual Cable)", "Active", false, false, true, false, 48000, 2)];
        State.CableOutputId = "expired-send";
        var resolvedRoute = SelectCableRoute();
        Check(resolvedRoute.IsReady && State.CableOutputId == "test-send" && State.InputDeviceId == savedMic && !engine.IsRunning, "Auto connection repairs stale cable ID without changing or opening microphone");
        inputs = []; resolvedRoute = SelectCableRoute();
        Check(!resolvedRoute.IsReady && State.CableOutputId == "test-send" && !engine.IsRunning, "Absent receiver keeps saved route without capture-only fallback");
        cableConnectionWarning = resolvedRoute.Message; Notice(cableConnectionWarning);
        inputs = [new("test-receive-new", "CABLE Output (VB-Audio Virtual Cable)", "Active", false, false, false, true, 48000, 2)];
        outputs = [new("test-send-new", "CABLE Input (VB-Audio Virtual Cable)", "Active", false, false, true, false, 48000, 2)];
        Check(SelectCableRoute().IsReady && State.CableOutputId == "test-send-new" && !engine.IsRunning && NoticeBar.Visibility == Visibility.Collapsed, "New endpoints are automatically matched and old warning cleared after reappearance");
        inputs = realInputs; outputs = realOutputs; State.CableOutputId = savedCable;
        State.InputDeviceId = "missing-explicit-microphone"; StartAudio(true); State.InputDeviceId = savedMic; RefreshDevices();
        Check(!engine.IsRunning, "Automatic cable connection cannot substitute an unavailable selected microphone");
        FlushLiveSettings(); Check(!engine.IsRunning && !engine.IsRecording && !monitor, "Edits, presets, restore and pending live flush never open stopped audio");
        State.Presets.Remove(personal); RestoreDefaults(); ShowPage(0); NoticeBar.Visibility = Visibility.Collapsed;

        Check(State.MinimizeToTray && State.SchemaVersion == WorkspaceState.CurrentSchemaVersion, "New workspace closes to tray by default");
        ShowPage(4); UpdateLayout();
        var echoBefore = State.EchoCancellationEnabled;
        var echoButton = Descendants<System.Windows.Controls.Button>(PageContent).Single(b => (b.Content as string)?.StartsWith("外放回声消除：") == true);
        echoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(State.EchoCancellationEnabled != echoBefore && !engine.IsRunning, "Echo preference changes without starting capture or playback reference");
        echoButton = Descendants<System.Windows.Controls.Button>(PageContent).Single(b => (b.Content as string)?.StartsWith("外放回声消除：") == true);
        echoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); vm.Save();
        Check(new StudioViewModel().State.EchoCancellationEnabled == echoBefore, "Echo preference persists independently of sound presets");
        var nativeWorkspace = new WorkspaceState { Mode = 2 }; nativeWorkspace.Normalize();
        Check(nativeWorkspace.Mode == 2, "Workspace accepts the explicit native APO preview mode");
        ModePicker.SelectedIndex = 2; StartAudio();
        Check(!engine.IsRunning && nativeWriter == null && !nativeLeaseTimer.IsEnabled, "Unavailable or untrusted native APO cannot open audio or claim an active controller");
        ModePicker.SelectedIndex = 1; NoticeBar.Visibility = Visibility.Collapsed;
        var legacy = new WorkspaceState { SchemaVersion = 1, MinimizeToTray = false, Language = "xx-XX" };
        legacy.Normalize();
        Check(legacy.SchemaVersion == 2 && legacy.MinimizeToTray && legacy.Language == UiText.Chinese, "P1 workspace migrates to tray default and a supported language");

        State.LaunchAtLogin = true; State.AutoStartProcessing = true; State.Language = UiText.Chinese; vm.Save();
        var preferenceReload = new StudioViewModel();
        Check(preferenceReload.State.LaunchAtLogin && preferenceReload.State.AutoStartProcessing, "Startup preferences persist separately from audio parameters");
        State.LaunchAtLogin = false; State.AutoStartProcessing = false; vm.Save();

        ShowPage(6); UpdateLayout();
        var englishButton = Descendants<System.Windows.Controls.Button>(PageContent).Single(b => b.Content as string == "English");
        englishButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(UiText.IsEnglish && State.Language == UiText.English, "Language button switches the live interface and preference");
        vm.Save(); var languageReload = new StudioViewModel();
        Check(languageReload.State.Language == UiText.English, "Language choice survives a workspace reload");
        string NavLabel(int index) => ((Navigation.Children[index] as System.Windows.Controls.Button)?.Content as StackPanel)?.Children[1] is TextBlock text ? text.Text : "";
        Check(NavLabel(0) == "Microphone", "Navigation rebuilds in English");
        ShowPage(6); UpdateLayout();
        var chineseButton = Descendants<System.Windows.Controls.Button>(PageContent).Single(b => b.Content as string == "Chinese");
        chineseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(!UiText.IsEnglish && State.Language == UiText.Chinese && NavLabel(0) == "麦克风", "Language can be switched back without touching audio settings");

        EnsureTray();
        var activeTray = tray ?? throw new InvalidOperationException("Tray icon was not created.");
        Check(activeTray.ContextMenuStrip?.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Any(i => i.Text == "继续处理") == true, "Tray exposes resume while audio is stopped");
        Check(activeTray.ContextMenuStrip!.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Any(i => i.Text == "打开 Mic Studio"), "Tray can reopen the window");
        Check(activeTray.ContextMenuStrip.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Any(i => i.Text == "退出"), "Tray has an explicit exit, separate from closing the window");
        Show(); State.MinimizeToTray = true; Close();
        Check(!IsVisible && !closed, "Closing the window hides to tray instead of stopping the application");
        Show(); State.MinimizeToTray = false;
        activeTray.ContextMenuStrip!.Dispose(); activeTray.Dispose(); tray = null; trayIcon?.Dispose(); trayIcon = null; trayProcessingItem = null;

        File.WriteAllText(Path.Combine(directory, "control-events.json"), JsonSerializer.Serialize(new { Method = "Actual WPF controls and routed handlers in application test mode; not physical mouse/keyboard automation or microphone acceptance.", Passed = checks.Count, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
