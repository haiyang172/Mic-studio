using MicStudio.Core;

int passed = 0, failed = 0;
void Test(string name, Action test)
{
    try { test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
}
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
void Near(double actual, double expected, double tolerance, string message) => Require(Math.Abs(actual - expected) <= tolerance, $"{message}: {actual:F5} vs {expected:F5}");
void Reject(Action action, string message) { bool rejected = false; try { action(); } catch { rejected = true; } Require(rejected, message); }
AudioSettings Flat() { var s = new AudioSettings(); s.Limiter.Enabled = false; return s; }
float[] Tone(double frequency, double amplitude, int length = 96000) => Enumerable.Range(0, length).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / 48000))).ToArray();
double Rms(float[] data, int start = 24000) => Math.Sqrt(data.Skip(start).Select(v => (double)v * v).Average());

Test("Eight bands and all factory presets validate", () =>
{
    Require(new AudioSettings().Bands.Count == 8, "Band count");
    foreach (var p in BuiltInPresets.All) p.Validate();
});
Test("Factory presets never share mutable settings", () =>
{
    var a = BuiltInPresets.All; a[0].Settings.InputGainDb = 20; a[1].Settings.Bands[0].FrequencyHz = 200;
    Near(BuiltInPresets.All[0].Settings.InputGainDb, 0, 0, "Factory gain immutable");
    Near(BuiltInPresets.All[1].Settings.Bands[0].FrequencyHz, 80, 0, "Factory band immutable");
});
Test("Settings validation rejects non-finite and unsafe values", () =>
{
    var s = Flat(); s.InputGainDb = double.NaN; Reject(() => s.Validate(), "NaN accepted");
    s = Flat(); s.Bands[3].Q = double.PositiveInfinity; Reject(() => s.Validate(), "Infinity accepted");
    s = Flat(); s.InputGainDb = 31; Reject(() => s.Validate(), "Gain out of range accepted");
    s = Flat(); s.Bands[3].Type = EqFilterType.HighPass; Reject(() => s.Validate(), "Wrong type accepted");
    s = Flat(); s.Bands[7].Enabled = true; Reject(() => s.Validate(16000), "Nyquist violation accepted");
});
Test("Bypass passes exact interleaved samples without limiting", () =>
{
    var s = new AudioSettings { Bypass = true, InputGainDb = 30 };
    float[] input = [0.1f, -0.4f, 1.2f, -1.4f, 0, 0]; var copy = (float[])input.Clone();
    new DspProcessor(48000, 2, s).Process(input, 0, input.Length);
    Require(input.SequenceEqual(copy), "Bypass changed samples");
});
Test("Gain applies measured 6 dB amplitude change", () =>
{
    var s = Flat(); s.InputGainDb = 6;
    var data = Tone(1000, .05); double before = Rms(data);
    new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
    Near(20 * Math.Log10(Rms(data) / before), 6, .0001, "Measured gain");
});
Test("Peak EQ measured center gain matches analytic response", () =>
{
    var s = Flat(); s.Bands[4].Enabled = true; s.Bands[4].FrequencyHz = 1500; s.Bands[4].GainDb = 6;
    var data = Tone(1500, .02); var before = Rms(data);
    new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
    Near(20 * Math.Log10(Rms(data) / before), 6, .01, "Measured EQ");
    Near(DspProcessor.EqResponseDb(s, 1500), 6, .0001, "Theory EQ");
});
Test("High-pass removes rumble while preserving voice band", () =>
{
    var s = Flat(); s.Bands[0].Enabled = true;
    foreach (var hz in new[] { 25, 1000 })
    {
        var data = Tone(hz, .02); var before = Rms(data);
        new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
        var measured = 20 * Math.Log10(Rms(data) / before);
        Near(measured, DspProcessor.EqResponseDb(s, hz), .01, "HP measured vs theory");
        Require(hz == 25 ? measured < -19 : measured > -.01, "HP response invalid");
    }
});
Test("Shelves attain selected gain away from transition", () =>
{
    var s = Flat(); s.Bands[1].Enabled = true; s.Bands[1].GainDb = 6;
    Require(DspProcessor.EqResponseDb(s, 20) > 5.8, "Low shelf missing boost");
    s.Bands[1].Enabled = false; s.Bands[6].Enabled = true; s.Bands[6].GainDb = -6;
    Require(DspProcessor.EqResponseDb(s, 20000) < -5.8, "High shelf missing cut");
});
Test("Stereo filters do not bleed to a silent channel", () =>
{
    var s = BuiltInPresets.All[1].Settings; s.Limiter.Enabled = false;
    var stereo = new float[96000]; for (int i = 0; i < stereo.Length; i += 2) stereo[i] = (float)(.1 * Math.Sin(i));
    new DspProcessor(48000, 2, s).Process(stereo, 0, stereo.Length);
    Require(stereo.Where((_, i) => i % 2 == 1).All(v => v == 0), "Stereo bleed");
});
Test("Compressor matches 4 to 1 steady peak transfer", () =>
{
    var s = Flat(); s.Compressor.Enabled = true; s.Compressor.ThresholdDb = -24; s.Compressor.Ratio = 4; s.Compressor.AttackMs = .1; s.Compressor.ReleaseMs = 2000;
    var data = Enumerable.Repeat((float)DspProcessor.DbToLinear(-4), 96000).ToArray();
    new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
    Near(DspProcessor.LinearToDb(Math.Abs(data[^1])), -19, .001, "Compressed level");
});
Test("Unity ratio and silence cannot produce NaN", () =>
{
    var s = Flat(); s.Compressor.Enabled = true; s.Compressor.Ratio = 1; s.Gate.Enabled = true; s.Gate.Ratio = 1;
    var data = new float[48000]; new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
    Require(data.All(v => v == 0), "Silence unstable");
});
Test("Gentle expander attenuates quiet input without hard gate", () =>
{
    var s = Flat(); s.Gate.Enabled = true; s.Gate.ThresholdDb = -40; s.Gate.RangeDb = 18; s.Gate.Ratio = 2;
    var data = Enumerable.Repeat(.001f, 144000).ToArray(); new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
    Near(DspProcessor.LinearToDb(data[^1] / .001), -18, .02, "Expander attenuation");
    Require(data.All(v => v > 0), "Hard muted tail");
});
Test("Expander hold preserves initial quiet tail", () =>
{
    var s = Flat(); s.Gate.Enabled = true; s.Gate.HoldMs = 200; s.Gate.ReleaseMs = 300;
    var data = Enumerable.Repeat(.1f, 4800).Concat(Enumerable.Repeat(.0001f, 4800)).ToArray();
    new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
    Near(data[^1], .0001, .000001, "Hold tail");
});
Test("Limiter contains abrupt positive and negative transients", () =>
{
    var s = new AudioSettings { InputGainDb = 30 }; var random = new Random(4);
    var data = Enumerable.Range(0, 48000).Select(_ => (float)(random.NextDouble() * 4 - 2)).ToArray();
    var processor = new DspProcessor(48000, 1, s); processor.Process(data, 0, data.Length);
    Require(data.All(v => float.IsFinite(v) && Math.Abs(v) <= DspProcessor.DbToLinear(-1) + 1e-6), "Limiter ceiling exceeded");
});
Test("Noise suppression attenuates actual low-level broadband noise", () =>
{
    var s = Flat(); s.Denoise.Enabled = true; s.Denoise.Strength = 1; s.Denoise.NoiseFloorDb = -40;
    var random = new Random(5); var data = Enumerable.Range(0, 96000).Select(_ => (float)((random.NextDouble() * 2 - 1) * .001)).ToArray();
    var before = Rms(data); var processor = new DspProcessor(48000, 1, s); processor.Process(data, 0, data.Length);
    Require(20 * Math.Log10(Rms(data) / before) < -20, "Spectral suppression inactive");
    Require(processor.AlgorithmLatencySamples == 512, "Algorithm latency reporting");
});
Test("Zero-strength STFT reconstructs at exactly 512 sample delay", () =>
{
    var s = Flat(); s.Denoise.Enabled = true; s.Denoise.Strength = 0;
    var data = Tone(997, .15); var original = (float[])data.Clone(); new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
    double error = Enumerable.Range(24000, 20000).Select(i => Math.Abs(data[i] - original[i - 512])).Max();
    Require(error < 1e-6, "STFT reconstruction error or wrong delay: " + error);
});
Test("Noise suppression retains strong speech-band tone", () =>
{
    var s = Flat(); s.Denoise.Enabled = true; s.Denoise.Strength = .55; s.Denoise.NoiseFloorDb = -55;
    var data = Tone(1000, .15); var before = Rms(data); new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
    Require(20 * Math.Log10(Rms(data) / before) > -.2, "Strong tone excessively suppressed");
});
Test("Processing is invariant to capture buffer chunk size", () =>
{
    var s = BuiltInPresets.All[2].Settings; s.Denoise.Enabled = true; s.Compressor.Enabled = true; s.Gate.Enabled = true;
    var a = Tone(1300, .15); var b = (float[])a.Clone();
    new DspProcessor(48000, 1, s).Process(a, 0, a.Length);
    var processor = new DspProcessor(48000, 1, s);
    for (int offset = 0; offset < b.Length; offset += 193) processor.Process(b, offset, Math.Min(193, b.Length - offset));
    Require(a.SequenceEqual(b), "Buffer boundary changes audio");
});
Test("Real delay generates an echo at configured sample offset", () =>
{
    var s = Flat(); s.Creative.DelayEnabled = true; s.Creative.DelayMs = 100; s.Creative.DelayMix = .5; s.Creative.DelayFeedback = 0;
    var data = new float[9600]; data[0] = .2f; new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
    Near(data[0], .1, .00001, "Dry pulse"); Near(data[4800], .1, .00001, "Echo pulse"); Require(data.Count(v => v != 0) == 2, "Unexpected echo");
});
Test("Creative modules all produce finite non-identical output", () =>
{
    foreach (var effect in new[] { "saturation", "robot", "reverb" })
    {
        var s = Flat(); s.Creative.SaturationEnabled = effect == "saturation"; s.Creative.RobotEnabled = effect == "robot"; s.Creative.ReverbEnabled = effect == "reverb";
        var data = Tone(500, .2); var before = (float[])data.Clone(); new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
        Require(data.All(float.IsFinite) && !data.SequenceEqual(before), "Effect inactive: " + effect);
    }
});
Test("Preset portable roundtrip excludes IDs and paths", () =>
{
    var preset = BuiltInPresets.All[2]; preset.Id = "private-id";
    var json = PresetStore.ExportJson(preset); var imported = PresetStore.ImportJson(json);
    Require(!json.Contains("private-id") && !json.Contains("isBuiltIn") && !json.Contains("device"), "Private properties exported");
    Require(imported.Name == preset.Name && !imported.IsBuiltIn && imported.Id != preset.Id, "Imported identity");
    Near(imported.Settings.Bands[5].GainDb, 2.5, 0, "Portable settings");
});
Test("Preset import rejects arbitrary commands and unsupported versions", () =>
{
    var good = PresetStore.ExportJson(BuiltInPresets.All[0]);
    Reject(() => PresetStore.ImportJson(good.Replace("\"name\":", "\"command\": \"Include: evil.txt\", \"name\":")), "Unknown property accepted");
    Reject(() => PresetStore.ImportJson(good.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99")), "Unsupported version accepted");
    Reject(() => PresetStore.ImportJson("{}"), "Missing schema accepted");
    Reject(() => PresetStore.ImportJson(new string(' ', 256 * 1024 + 1)), "Oversize accepted");
});
Test("Settings atomically save, restore backup, and preserve corrupt copy", () =>
{
    string folder = Path.Combine(Path.GetTempPath(), "MicStudio-CoreTests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        string path = Path.Combine(folder, "settings.json"); var a = Flat(); a.InputGainDb = 3; SettingsStore.Save(path, a);
        a.InputGainDb = 6; SettingsStore.Save(path, a); Near(SettingsStore.Load<AudioSettings>(path).Value.InputGainDb, 6, 0, "Save");
        File.WriteAllText(path, "broken"); var loaded = SettingsStore.Load<AudioSettings>(path);
        Near(loaded.Value.InputGainDb, 3, 0, "Backup restore"); Require(loaded.Warning is not null && Directory.GetFiles(folder, "*.corrupt-*").Length == 1, "Recovery warning/copy missing");
    }
    finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
});
Test("Non-finite capture samples sanitized before DSP", () =>
{
    float[] data = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0, .1f];
    new DspProcessor(48000, 1, new AudioSettings()).Process(data, 0, data.Length);
    Require(data.All(float.IsFinite), "NaN propagated");
});
Test("Gain updates ramp while preserving spectral overlap history", () =>
{
    var s = Flat(); s.Denoise.Enabled = true; s.Denoise.Strength = 0;
    var processor = new DspProcessor(48000, 1, s);
    var warmup = Enumerable.Repeat(.1f, 4800).ToArray(); processor.Process(warmup, 0, warmup.Length);
    s.OutputGainDb = -12; processor.UpdateSettings(s);
    var changed = Enumerable.Repeat(.1f, 4800).ToArray(); processor.Process(changed, 0, changed.Length);
    Require(changed[0] > .09f, "Gain jumped or overlap reset to silence");
    Require(changed.All(v => v > .024f), "Spectral buffer dropout on gain update");
    Near(changed[^1], .1 * DspProcessor.DbToLinear(-12), .00001, "Gain ramp settled");
});
Test("Settings update retains pending creative echo history", () =>
{
    var s = Flat(); s.Creative.DelayEnabled = true; s.Creative.DelayMs = 100; s.Creative.DelayMix = .5; s.Creative.DelayFeedback = 0;
    var processor = new DspProcessor(48000, 1, s); var first = new float[1000]; first[0] = .2f; processor.Process(first, 0, first.Length);
    processor.UpdateSettings(s); var rest = new float[5000]; processor.Process(rest, 0, rest.Length);
    Near(rest[3800], .1, .00001, "Echo discarded by settings update");
});
Test("Explicit Reset discards audio history", () =>
{
    var s = Flat(); s.Creative.DelayEnabled = true; s.Creative.DelayMs = 100; s.Creative.DelayMix = .5;
    var processor = new DspProcessor(48000, 1, s); var first = new float[1000]; first[0] = .2f; processor.Process(first, 0, first.Length);
    processor.Reset(); var silence = new float[10000]; processor.Process(silence, 0, silence.Length);
    Require(silence.All(v => v == 0), "Reset retained old recording content");
});
Test("Finite extreme samples stay finite with limiter disabled", () =>
{
    var s = Flat(); s.InputGainDb = 30; float[] data = [float.MaxValue, -float.MaxValue];
    new DspProcessor(48000, 1, s).Process(data, 0, data.Length);
    Require(data.All(float.IsFinite), "Float conversion overflow");
});

Console.WriteLine($"Core synthetic verification: {passed} passed, {failed} failed. No microphone, APO, Discord or subjective audio acceptance performed.");
return failed == 0 ? 0 : 1;
