using System.Globalization;
using MicStudio.Core;

namespace MicStudio.App;

public sealed class EqCurve : FrameworkElement
{
    public Func<AudioSettings> Settings { get; init; } = () => new();
    public Func<int> Rate { get; init; } = () => 48000;
    public int SelectedBand { get; set; } = 4;
    public bool AllowEditing { get; set; }
    public event Action<int>? BandSelected;
    public event Action<int, double, double>? BandDragged;
    private bool dragging;
    public EqCurve()
    {
        Height = 260; MinWidth = 180; Focusable = true; ClipToBounds = true;
        MouseLeftButtonDown += (_, e) =>
        {
            if (!AllowEditing) return;
            var point = e.GetPosition(this); var bands = Settings().CreateProcessingSnapshot(Rate(), out var adjustedBands).Bands;
            SelectedBand = Enumerable.Range(0, bands.Count).OrderBy(i => Math.Abs(X(bands[i].FrequencyHz) - point.X)).First();
            BandSelected?.Invoke(SelectedBand); Focus(); dragging = true; CaptureMouse(); InvalidateVisual(); e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (!AllowEditing || !dragging) return;
            var point = e.GetPosition(this); var band = Settings().Bands[SelectedBand];
            var freq = Math.Pow(10, Math.Log10(20) + Math.Clamp((point.X - 42) / Math.Max(1, ActualWidth - 60), 0, 1) * Math.Log10(MaxFrequency / 20));
            freq = Math.Clamp(freq, 20, Math.Min(20000, Rate() * .49 - 1)); if (band.Type == EqFilterType.HighPass) freq = Math.Min(freq, 250);
            var gain = Math.Clamp((ActualHeight / 2 - point.Y) / Math.Max(1, ActualHeight - 42) * 36, -12, 12);
            BandDragged?.Invoke(SelectedBand, Math.Round(freq), Math.Round(gain, 1)); InvalidateVisual();
        };
        MouseLeftButtonUp += (_, _) => { dragging = false; ReleaseMouseCapture(); };
        LostMouseCapture += (_, _) => dragging = false;
        KeyDown += (_, e) => { if (AllowEditing && e.Key is (Key.Left or Key.Right)) { SelectedBand = Math.Clamp(SelectedBand + (e.Key == Key.Right ? 1 : -1), 0, 7); BandSelected?.Invoke(SelectedBand); InvalidateVisual(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(this, "理论均衡器响应曲线；展开高级功能后可编辑");
    }
    private double MaxFrequency => Math.Min(20000, Rate() * .48);
    private double X(double frequency) => 42 + Math.Log10(Math.Clamp(frequency, 20, MaxFrequency) / 20) / Math.Log10(MaxFrequency / 20) * Math.Max(1, ActualWidth - 60);
    private double Y(double db) => ActualHeight / 2 - Math.Clamp(db, -18, 18) / 36 * Math.Max(1, ActualHeight - 42);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var rate = Rate(); var settings = Settings().CreateProcessingSnapshot(rate, out _); var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(47, 59, 62)), 1); var muted = new SolidColorBrush(Color.FromRgb(135, 154, 159)); var mint = new SolidColorBrush(Color.FromRgb(116, 227, 193));
        void Label(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, muted, dpi), new(x, y));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(20, 26, 29)), null, new(0, 0, ActualWidth, ActualHeight), 7, 7);
        foreach (var db in new[] { -12, -6, 0, 6, 12 }) { var y = Y(db); dc.DrawLine(grid, new(40, y), new(ActualWidth - 14, y)); Label(db > 0 ? "+" + db : db.ToString(), 7, y - 6); }
        foreach (var freq in new[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 }.Where(f => f <= MaxFrequency)) { var x = X(freq); dc.DrawLine(grid, new(x, 18), new(x, ActualHeight - 27)); if (ActualWidth > 600 || freq is 20 or 100 or 1000 or 10000) Label(freq >= 1000 ? freq / 1000 + "k" : freq.ToString(), x - 8, ActualHeight - 19); }
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i <= 360; i++)
            {
                var f = 20 * Math.Pow(MaxFrequency / 20, i / 360.0); var value = DspProcessor.EqResponseDb(settings, f, rate);
                var point = new Point(X(f), Y(value)); if (i == 0) ctx.BeginFigure(point, false, false); else ctx.LineTo(point, true, false);
            }
        }
        dc.DrawGeometry(null, new Pen(mint, 2), geometry);
        if (!AllowEditing) return;
        for (var i = 0; i < settings.Bands.Count; i++)
        {
            var b = settings.Bands[i]; var point = new Point(X(b.FrequencyHz), Y(b.Type is EqFilterType.HighPass or EqFilterType.LowPass ? 0 : b.GainDb));
            dc.DrawEllipse(settings.EqEnabled && b.Enabled ? mint : new SolidColorBrush(Color.FromRgb(72, 85, 88)), new Pen(i == SelectedBand ? Brushes.White : mint, i == SelectedBand ? 2 : 1), point, i == SelectedBand ? 6 : 4, i == SelectedBand ? 6 : 4);
        }
    }
}
