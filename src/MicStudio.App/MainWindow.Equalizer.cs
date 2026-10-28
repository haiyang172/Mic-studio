using MicStudio.Core;

namespace MicStudio.App;

public partial class MainWindow
{
    private void BuildEqualizer()
    {
        Header("SHAPE YOUR SOUND / 02", "细调每一个频段。", "八段参数均衡器。曲线表示线性 EQ 理论响应，未包含动态、降噪和真实频谱。");
        curve = new EqCurve { Settings = () => Draft, Rate = () => SampleRate, SelectedBand = selectedBand };
        curve.BandSelected += i => { selectedBand = i; BuildBandEditor(); };
        curve.BandDragged += (i, f, g) => { vm.Change(s => { s.Bands[i].FrequencyHz = f; if (s.Bands[i].Type is not (EqFilterType.HighPass or EqFilterType.LowPass)) s.Bands[i].GainDb = g; }, true); BuildBandEditor(); };
        var toolbar = Buttons(B("一键平直 EQ", () => { vm.Change(s => s.Bands = AudioSettings.CreateDefaultBands()); ShowPage(1); }), B("复位选中频段", () => { var i = selectedBand; vm.Change(s => s.Bands[i] = AudioSettings.CreateDefaultBands()[i]); ShowPage(1); }));
        Add(Card("EQ 理论响应", $"对数频率 · 当前采样率 {SampleRate / 1000.0:0.#} kHz · 增益坐标 ±18 dB", curve, toolbar));
        var bands = new WrapPanel();
        for (var i = 0; i < Draft.Bands.Count; i++) { var index = i; var b = B($"{i + 1} · {Draft.Bands[i].Name}", () => { selectedBand = index; if (curve != null) { curve.SelectedBand = index; curve.InvalidateVisual(); } BuildBandEditor(); }); bands.Children.Add(b); }
        Add(bands); bandEditor = new StackPanel(); Add(bandEditor); BuildBandEditor();
        AdaptiveCards(Card("输入数字增益", "改变此值后需主动应用。", Parameter("输入增益", -24, 30, Draft.InputGainDb, v => vm.Change(s => s.InputGainDb = v, true), "dB")), Card("输出补偿", "给滤波提升留出数字余量。", Parameter("输出补偿", -24, 0, Draft.OutputGainDb, v => vm.Change(s => s.OutputGainDb = v, true), "dB")));
        var maxBoost = Enumerable.Range(0, 200).Select(i => DspProcessor.EqResponseDb(Draft, 20 * Math.Pow(Math.Min(20000, SampleRate * .48) / 20, i / 199.0), SampleRate)).Max();
        Add(T($"EQ 最大提升：{maxBoost:0.0} dB", 12, Amber));
    }
    private void BuildBandEditor()
    {
        if (bandEditor is null) return;
        var wasBuilding = building; building = true; bandEditor.Children.Clear(); var i = selectedBand; var band = Draft.Bands[i];
        var toggle = Toggle("启用此频段", band.Enabled, value => vm.Change(s => s.Bands[i].Enabled = value));
        var frequency = Parameter("频率", 20, Math.Min(band.Type == EqFilterType.HighPass ? 250 : 20000, SampleRate * .49 - 1), band.FrequencyHz, value => vm.Change(s => s.Bands[i].FrequencyHz = value, true), "Hz", 1);
        var gain = band.Type is EqFilterType.HighPass or EqFilterType.LowPass ? T("此滤波类型没有增益参数。", 11, Muted) as UIElement : Parameter("增益", -12, 12, band.GainDb, value => vm.Change(s => s.Bands[i].GainDb = value, true), "dB");
        bandEditor.Children.Add(Card($"{i + 1} · {band.Name}", "拖动曲线可调频率和增益；也可使用下面的键盘数值输入。", toggle, frequency, gain, Parameter("Q · 带宽", .2, 10, band.Q, value => vm.Change(s => s.Bands[i].Q = value, true), "", .01)));
        building = wasBuilding;
    }
}
