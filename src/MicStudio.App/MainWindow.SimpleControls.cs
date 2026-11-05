using System.Windows.Automation;

namespace MicStudio.App;

public partial class MainWindow
{
    private bool connectionDetailsExpanded;
    private ComboBox? visibleProcessingMode;
    private void ApplyDialogTheme(Window dialog)
    {
        foreach (var key in new[] { "Bg", "Panel", "Ink", "Muted", "PanelHover", "Line", "Accent", "Amber", "PrimaryInk", "AccentSoft", "MeterTrack" })
            dialog.Resources[key] = FindResource(key);
    }

    private UIElement ChoiceGrid(IEnumerable<UIElement> choices, double minimumWidth = 150)
    {
        var panel = new WrapPanel { Margin = new(0, 4, -12, 0) };
        foreach (var choice in choices) panel.Children.Add(choice);
        void Arrange()
        {
            if (panel.ActualWidth <= 0) return;
            var columns = Math.Max(1, (int)Math.Floor(panel.ActualWidth / (minimumWidth + 12)));
            var width = Math.Max(1, panel.ActualWidth / columns - 12);
            foreach (FrameworkElement choice in panel.Children) choice.Width = width;
        }
        panel.SizeChanged += (_, _) => Arrange();
        return panel;
    }

    private System.Windows.Controls.Button ThemeChoice(string label, string glyph, string caption, Action action, Func<bool> selected, bool enabled = true)
    {
        var button = B(label, action);
        button.IsEnabled = enabled; button.Padding = new(15); button.Margin = new(0, 0, 12, 12); button.MinHeight = 102;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = new("Segoe MDL2 Assets"), FontSize = 23, Foreground = Accent, Margin = new(0, 0, 0, 12) });
        var name = T(label, 14, weight: FontWeights.SemiBold, margin: new(0));
        name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = label; content.Children.Add(name);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            var hint = T(caption, 12, Muted, margin: new(0, 6, 0, 0)); hint.LineHeight = 18; hint.MaxHeight = 36; hint.TextTrimming = TextTrimming.CharacterEllipsis; hint.ToolTip = caption; content.Children.Add(hint);
        }
        button.Content = content;
        void Refresh()
        {
            var active = selected();
            button.Background = (Brush)FindResource(active ? "AccentSoft" : "PanelHover");
            button.BorderBrush = active ? Accent : (Brush)FindResource("Line"); name.Foreground = active ? Accent : Ink;
            AutomationProperties.SetItemStatus(button, active ? Loc.T("已选择", "Selected") : Loc.T("未选择", "Not selected"));
        }
        RegisterSoundControl(button, Refresh); Refresh(); return button;
    }

    private UIElement ProcessingModeControls()
    {
        var modes = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 420 };
        foreach (ComboBoxItem mode in ModePicker.Items) modes.Items.Add(new ComboBoxItem { Content = mode.Content });
        modes.SelectedIndex = State.Mode;
        visibleProcessingMode = modes;
        AutomationProperties.SetName(modes, Loc.T("处理方式", "Processing mode"));
        AutomationProperties.SetAutomationId(modes, "processing-mode");
        modes.SelectionChanged += (_, _) =>
        {
            if (building || modes.SelectedIndex < 0) return;
            var restoreFocus = modes.IsKeyboardFocusWithin;
            ModePicker.SelectedIndex = modes.SelectedIndex;
            if (restoreFocus) Dispatcher.BeginInvoke(() => { if (!closed && currentPage == 4) visibleProcessingMode?.Focus(); }, DispatcherPriority.Input);
        };
        var panel = new StackPanel(); panel.Children.Add(T(Loc.T("处理方式", "Processing mode"), 12, Muted)); panel.Children.Add(modes);
        panel.Children.Add(T(Loc.T("日常使用虚拟输入；系统 APO 需要单独部署与验证。", "Use virtual input for everyday processing. System APO needs separate deployment and verification."), 12, Muted, margin: new(0, 10, 0, 0)));
        var advanced = new Expander { Header = Loc.T("高级连接", "Advanced connection"), Content = panel, Foreground = Ink, Margin = new(0, 8, 0, 12), IsExpanded = connectionDetailsExpanded };
        advanced.Expanded += (_, _) => connectionDetailsExpanded = true;
        advanced.Collapsed += (_, _) => connectionDetailsExpanded = false;
        return advanced;
    }
}
