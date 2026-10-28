using MicStudio.Core;

namespace MicStudio.App;

public partial class MainWindow
{
    private void BuildEqualizer()
    {
        Header("", "均衡器", "调整后实时生效。详细参数放在高级功能中。");
        var controls = new Grid(); controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); controls.ColumnDefinitions.Add(new ColumnDefinition());
        var master = ButtonSwitch("EQ 均衡", Draft.EqEnabled, value => vm.Change(s => s.EqEnabled = value), readValue: () => Draft.EqEnabled);
        master.VerticalAlignment = VerticalAlignment.Bottom; master.Margin = new Thickness(0, 0, 12, 10); controls.Children.Add(master);
        var selector = BuildPresetSelector(); Grid.SetColumn(selector, 1); controls.Children.Add(selector); Add(controls);
        curve = new EqCurve { Settings = () => Draft, Rate = () => SampleRate, SelectedBand = selectedBand, AllowEditing = false, Height = 170 };
        curve.BandSelected += i => { selectedBand = i; BuildBandEditor(); };
        curve.BandDragged += (i, f, g) =>
        {
            vm.Change(s =>
            {
                s.EqEnabled = true; s.Bands[i].Enabled = true; s.Bands[i].FrequencyHz = f;
                if (s.Bands[i].Type is not (EqFilterType.HighPass or EqFilterType.LowPass)) s.Bands[i].GainDb = g;
            }, true);
            BuildBandEditor();
        };
        var gains = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        for (var i = 1; i < Draft.Bands.Count - 1; i++)
        {
            var index = i;
            var gain = Parameter(Draft.Bands[i].Name, -12, 12, Draft.Bands[i].GainDb, value =>
                vm.Change(s => { s.EqEnabled = true; s.Bands[index].Enabled = true; s.Bands[index].GainDb = value; }, true), "dB", readValue: () => Draft.Bands[index].GainDb);
            gain.Margin = new Thickness(0, 2, 12, 12); gains.Children.Add(gain);
        }
        var sound = Card("音色", "旧主题中关闭的频段会保留原状态；调节该频段即启用。", gains);
        var response = Card("声音曲线", "线性 EQ 理论响应，不是真实频谱。", curve);
        var ordinary = new Grid(); ordinary.ColumnDefinitions.Add(new ColumnDefinition()); ordinary.ColumnDefinitions.Add(new ColumnDefinition());
        ordinary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); ordinary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ordinary.Children.Add(sound); ordinary.Children.Add(response);
        bool? sideBySide = null;
        void ArrangeOrdinary()
        {
            var wide = ordinary.ActualWidth >= 800; if (sideBySide == wide) return; sideBySide = wide;
            Grid.SetColumnSpan(sound, wide ? 1 : 2); Grid.SetColumnSpan(response, wide ? 1 : 2);
            Grid.SetColumn(response, wide ? 1 : 0); Grid.SetRow(response, wide ? 0 : 1);
            sound.Margin = new Thickness(0, 0, wide ? 12 : 0, 12);
        }
        ordinary.SizeChanged += (_, _) => ArrangeOrdinary(); Add(ordinary);
        var advancedContent = new StackPanel();
        var bands = new WrapPanel();
        for (var i = 0; i < Draft.Bands.Count; i++)
        {
            var index = i;
            bands.Children.Add(B($"{i + 1} · {Draft.Bands[i].Name}", () =>
            {
                selectedBand = index;
                if (curve != null) { curve.SelectedBand = index; curve.InvalidateVisual(); }
                BuildBandEditor();
            }));
        }
        advancedContent.Children.Add(bands);
        bandEditor = new StackPanel(); advancedContent.Children.Add(bandEditor); BuildBandEditor();
        advancedContent.Children.Add(Card("增益与余量", "提升 EQ 后可降低输出补偿，给数字处理保留余量。",
            Parameter("输入增益", -24, 30, Draft.InputGainDb, v => vm.Change(s => s.InputGainDb = v, true), "dB", readValue: () => Draft.InputGainDb),
            Parameter("输出补偿", -24, 0, Draft.OutputGainDb, v => vm.Change(s => s.OutputGainDb = v, true), "dB", readValue: () => Draft.OutputGainDb)));
        var advanced = new Expander { Header = "高级功能", Foreground = Ink, Content = advancedContent, Margin = new Thickness(0, 8, 0, 14) };
        advanced.Expanded += (_, _) => { if (curve != null) { curve.AllowEditing = true; curve.InvalidateVisual(); } };
        advanced.Collapsed += (_, _) => { if (curve != null) { curve.AllowEditing = false; curve.InvalidateVisual(); } };
        Add(advanced);
    }

    private void BuildBandEditor()
    {
        if (bandEditor is null) return;
        var wasBuilding = building; building = true;
        try
        {
            bandEditor.Children.Clear();
            selectedBand = Math.Clamp(selectedBand, 0, Draft.Bands.Count - 1);
            var i = selectedBand; var band = Draft.Bands[i];
            void ChangeBand(Action<EqBand> change) => vm.Change(s => { s.EqEnabled = true; s.Bands[i].Enabled = true; change(s.Bands[i]); }, true);
            var frequency = Parameter("频率", 20, Math.Min(band.Type == EqFilterType.HighPass ? 250 : 20000, SampleRate * .49 - 1), band.FrequencyHz, value => ChangeBand(b => b.FrequencyHz = value), "Hz", 1, readValue: () => Draft.Bands[i].FrequencyHz);
            var gain = band.Type is EqFilterType.HighPass or EqFilterType.LowPass ? null : Parameter("增益", -12, 12, band.GainDb, value => ChangeBand(b => b.GainDb = value), "dB", readValue: () => Draft.Bands[i].GainDb);
            var content = new StackPanel(); content.Children.Add(frequency);
            if (gain != null) content.Children.Add(gain);
            content.Children.Add(Parameter("Q · 带宽", .2, 10, band.Q, value => ChangeBand(b => b.Q = value), "", .01, readValue: () => Draft.Bands[i].Q));
            bandEditor.Children.Add(Card($"{i + 1} · {band.Name}", band.Enabled ? "调节即生效，可拖动曲线。" : "此频段来自旧主题，调节即启用。", content));
        }
        finally { building = wasBuilding; }
    }
}
