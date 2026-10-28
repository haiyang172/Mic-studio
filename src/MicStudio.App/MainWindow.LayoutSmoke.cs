using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using MicStudio.Core;

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
                ShowPage(page); await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render); await Task.Delay(50);
                var rightOverflow = PageContent.ActualWidth > PageScroll.ViewportWidth + 3;
                var buttons = Descendants<System.Windows.Controls.Button>(PageContent).Count();
                PageScroll.ScrollToEnd(); await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
                var reachable = PageScroll.ExtentHeight <= PageScroll.ViewportHeight + 3 || Math.Abs(PageScroll.VerticalOffset - PageScroll.ScrollableHeight) < 3;
                PageScroll.ScrollToTop(); await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
                results.Add(new { size.W, size.H, Page = pageNames[page], PageContent.ActualWidth, PageScroll.ViewportWidth, PageScroll.ExtentHeight, PageScroll.ViewportHeight, ScrollReachable = reachable, RightOverflow = rightOverflow, Buttons = buttons });
                if (!reachable || rightOverflow) throw new InvalidOperationException("窗口内容无法完整访问：" + pageNames[page]);
                var visual = (Visual)Content;
                var bitmap = new RenderTargetBitmap((int)((FrameworkElement)Content).ActualWidth, (int)((FrameworkElement)Content).ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"{size.W}x{size.H}-{page + 1}.png")); encoder.Save(file);
            }
        }
        File.WriteAllText(Path.Combine(directory, "layout-report.json"), JsonSerializer.Serialize(new { Method = "Actual WPF window layout at 3 DIP sizes, all 7 pages; OS DPI unchanged. Manual UI interaction separate.", Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        State.Theme = "light"; ApplyTheme(); Width = 1280; Height = 820;
        foreach (var page in new[] { 0, 2, 4, 6 })
        {
            ShowPage(page); await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
            var light = new RenderTargetBitmap((int)((FrameworkElement)Content).ActualWidth, (int)((FrameworkElement)Content).ActualHeight, 96, 96, PixelFormats.Pbgra32); light.Render((Visual)Content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(light));
            using var file = File.Create(Path.Combine(directory, $"light-{page + 1}.png")); encoder.Save(file);
        }
        forceClose = true;
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
        var undoButton = Descendants<System.Windows.Controls.Button>(PageContent).First(b => b.Content as string == "撤销");
        undoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(Math.Abs(Draft.InputGainDb) < .001, "Undo button restores draft");
        var redoButton = Descendants<System.Windows.Controls.Button>(PageContent).First(b => b.Content as string == "重做");
        redoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(Math.Abs(Draft.InputGainDb - 6.5) < .001, "Redo button restores numeric value");
        ShowPage(1); UpdateLayout();
        var toggle = Descendants<CheckBox>(PageContent).First(c => (c.Content as TextBlock)?.Text == "启用此频段");
        toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(Draft.Bands[selectedBand].Enabled, "EQ enabled checkbox controls actual band");
        LoadPreset(BuiltInPresets.All.Single(p => p.Id == "natural"));
        Check(State.SelectedPresetId == "natural" && Draft.Bands[0].Enabled, "Preset loads actual draft without audio");
        ShowPage(4); UpdateLayout();
        Check(Descendants<System.Windows.Controls.Button>(PageContent).Any(b => b.Content as string == "重新下载"), "Driver re-download command is reachable");
        var saved = State.CableOutputId; State.CableOutputId = null; StartAudio(true); State.CableOutputId = saved;
        Check(!engine.IsRunning, "Missing cable cannot silently start capture-only");
        ApplyDraft(); Check(!engine.IsRunning, "Apply while stopped never opens microphone");
        vm.Replace(new AudioSettings()); State.SelectedPresetId = "flat"; NoticeBar.Visibility = Visibility.Collapsed;
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
