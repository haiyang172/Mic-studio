using System.Globalization;
using System.Text;
using MicStudio.Apo;
using MicStudio.Core;

const string Endpoint = "{0.0.1.00000000}.{01234567-89ab-cdef-0123-456789abcdef}";
var tests = new List<(string Name, Action Run)>();
var temporaryRoot = Path.Combine(Path.GetTempPath(), "MicStudio-ApoTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
int passed = 0;

void Assert(bool condition, string detail = "assertion failed") { if (!condition) throw new Exception(detail); }
void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected ArgumentException"); }
AudioSettings EqOnly() { var settings = new AudioSettings(); settings.Limiter.Enabled = false; return settings; }
(ApoConfigService Service, string Config) NewService(string? text = "# existing settings\r\n", Encoding? encoding = null)
{
    var directory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    if (text is not null) File.WriteAllText(Path.Combine(directory, "config.txt"), text, encoding ?? new UTF8Encoding(false));
    return (new(new(true, directory, directory, "test", null, null, "fixture")), directory);
}
void Add(string name, Action action) => tests.Add((name, action));

Add("only real capture endpoints accepted", () =>
{
    Assert(ApoConfigGenerator.ParseCaptureGuid(Endpoint).ToString() == "01234567-89ab-cdef-0123-456789abcdef");
    foreach (var bad in new[] { "Microphone", "all", "{0.0.0.00000000}.{01234567-89ab-cdef-0123-456789abcdef}", Endpoint + "\nDevice: all", "../config.txt", "" })
        Throws(() => ApoConfigGenerator.ParseCaptureGuid(bad));
});
Add("culture invariant numeric whitelist and captured scope", () =>
{
    var old = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var settings = EqOnly(); settings.InputGainDb = 3.25; settings.OutputGainDb = -1.5;
        settings.Bands[4].Enabled = true; settings.Bands[4].GainDb = 2.25; settings.Bands[4].Q = 1.4;
        var text = ApoConfigGenerator.Generate(settings, Endpoint);
        Assert(text.Contains("Preamp: 1.75 dB"));
        Assert(text.Contains("Gain 2.25 dB Q 1.4"));
        Assert(text.Contains("Device: {01234567-89ab-cdef-0123-456789abcdef}"));
        Assert(text.Contains("Stage: capture") && !text.Contains("post-mix"));
    }
    finally { CultureInfo.CurrentCulture = old; }
});
Add("native effects cannot silently pass as APO support", () =>
{
    Throws(() => ApoConfigGenerator.Generate(new AudioSettings(), Endpoint));
    var settings = EqOnly(); settings.Denoise.Enabled = true;
    Assert(ApoConfigGenerator.GetUnsupportedEffects(settings).Contains("频谱降噪"));
    Throws(() => ApoConfigGenerator.Generate(settings, Endpoint));
    settings.Bypass = true;
    Assert(!ApoConfigGenerator.Generate(settings, Endpoint).Contains("Preamp:"));
});
Add("nonfinite and Nyquist-violating values rejected", () =>
{
    var settings = EqOnly(); settings.InputGainDb = double.NaN;
    Throws(() => ApoConfigGenerator.Generate(settings, Endpoint));
    settings.InputGainDb = 0; settings.Bands[7].Enabled = true;
    Throws(() => ApoConfigGenerator.Generate(settings, Endpoint, 16000));
});
Add("preview is read only", () =>
{
    var (service, directory) = NewService();
    var before = File.ReadAllBytes(Path.Combine(directory, "config.txt"));
    var plan = service.Preview(EqOnly(), Endpoint);
    Assert(plan.CanApply, plan.Message);
    Assert(File.ReadAllBytes(Path.Combine(directory, "config.txt")).SequenceEqual(before));
    Assert(!Directory.Exists(Path.Combine(directory, "MicStudio")));
});
Add("atomic apply preserves external bytes and default scopes", () =>
{
    const string original = "# custom device config\r\nDevice: Speakers\r\nChannel: L\r\nPreamp: -3 dB\r\n";
    var (service, directory) = NewService(original);
    var result = service.Apply(service.Preview(EqOnly(), Endpoint));
    Assert(result.Success, result.Message);
    var text = File.ReadAllText(Path.Combine(directory, "config.txt"));
    Assert(text.StartsWith(original));
    Assert(text.Contains("Device: all\r\nStage: capture\r\nChannel: all\r\nInclude: MicStudio/eq-"));
    Assert(text.Contains("Device: all\r\nStage: post-mix capture\r\nChannel: all"));
    Assert(Directory.GetFiles(Path.Combine(directory, "MicStudio", "snapshots")).Length == 1);
    Assert(service.RemoveOwnedConfiguration().Success);
    Assert(File.ReadAllText(Path.Combine(directory, "config.txt")) == original);
});
Add("UTF-8 BOM UTF-16 BOM and LF are preserved", () =>
{
    foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
    {
        var (service, directory) = NewService("# 自有之外的配置\nPreamp: -2 dB\n", encoding);
        var path = Path.Combine(directory, "config.txt"); var original = File.ReadAllBytes(path);
        Assert(service.Apply(service.Preview(EqOnly(), Endpoint)).Success);
        var modified = File.ReadAllBytes(path);
        Assert(modified.AsSpan().StartsWith(original));
        Assert(service.RemoveOwnedConfiguration().Success);
        Assert(File.ReadAllBytes(path).SequenceEqual(original));
    }
});
Add("snapshot hash detects external race", () =>
{
    var (service, directory) = NewService(); var path = Path.Combine(directory, "config.txt");
    var plan = service.Preview(EqOnly(), Endpoint);
    File.AppendAllText(path, "Preamp: -6 dB\r\n");
    var external = File.ReadAllBytes(path);
    var result = service.Apply(plan);
    Assert(!result.Success && result.Message.Contains("冲突"));
    Assert(File.ReadAllBytes(path).SequenceEqual(external));
});
Add("unknown nested config scripts Peace and plugin ownership block apply", () =>
{
    foreach (var content in new[] { "Include: custom.txt\n", "If: stage == capture\n", "VSTPlugin: Library x.dll\n", "# Peace settings\n", "Preamp: `2+2` dB\n" })
    {
        var (service, _) = NewService(content);
        Assert(!service.Preview(EqOnly(), Endpoint).CanApply, content);
    }
});
Add("applying a second plan replaces only own hook", () =>
{
    var (service, directory) = NewService();
    Assert(service.Apply(service.Preview(EqOnly(), Endpoint)).Success);
    var settings = EqOnly(); settings.InputGainDb = 4;
    Assert(service.Apply(service.Preview(settings, Endpoint)).Success);
    var text = File.ReadAllText(Path.Combine(directory, "config.txt"));
    Assert(text.Split(ApoConfigService.BeginMarker).Length == 2);
    Assert(Directory.GetFiles(Path.Combine(directory, "MicStudio"), "eq-*.txt").Length == 2);
});
Add("externally edited managed fragment is preserved and blocks apply", () =>
{
    var (service, directory) = NewService();
    Assert(service.Apply(service.Preview(EqOnly(), Endpoint)).Success);
    var fragment = Directory.GetFiles(Path.Combine(directory, "MicStudio"), "eq-*.txt").Single();
    File.AppendAllText(fragment, "\nPreamp: -4 dB\n");
    Assert(!service.Preview(EqOnly(), Endpoint).CanApply);
    Assert(File.ReadAllText(fragment).EndsWith("Preamp: -4 dB\n"));
});
Add("fragment race after preview is detected before commit", () =>
{
    var (service, directory) = NewService(); var path = Path.Combine(directory, "config.txt");
    Assert(service.Apply(service.Preview(EqOnly(), Endpoint)).Success);
    var settings = EqOnly(); settings.InputGainDb = 2;
    var plan = service.Preview(settings, Endpoint);
    var fragment = Directory.GetFiles(Path.Combine(directory, "MicStudio"), "eq-*.txt").Single();
    File.AppendAllText(fragment, "# external edit\n");
    var before = File.ReadAllBytes(path);
    var result = service.Apply(plan);
    Assert(!result.Success && result.Message.Contains("冲突"));
    Assert(File.ReadAllBytes(path).SequenceEqual(before));
});
Add("rollback retains external edits and never replays old backup", () =>
{
    const string original = "# original\n";
    var (service, directory) = NewService(original); var path = Path.Combine(directory, "config.txt");
    Assert(service.Apply(service.Preview(EqOnly(), Endpoint)).Success);
    File.AppendAllText(path, "# external note added after apply\n");
    Assert(service.RemoveOwnedConfiguration().Success);
    Assert(File.ReadAllText(path) == original + "# external note added after apply\n");
});
Add("external commands after owned block require scope review", () =>
{
    var (service, directory) = NewService(); var path = Path.Combine(directory, "config.txt");
    Assert(service.Apply(service.Preview(EqOnly(), Endpoint)).Success);
    File.AppendAllText(path, "Preamp: -3 dB\n");
    Assert(!service.Preview(EqOnly(), Endpoint).CanApply);
    Assert(service.RemoveOwnedConfiguration().Success);
    Assert(File.ReadAllText(path).Contains("Preamp: -3 dB"));
});
Add("missing malformed and duplicate hooks are safe", () =>
{
    var (service, directory) = NewService(ApoConfigService.BeginMarker + "\n");
    Assert(!service.Preview(EqOnly(), Endpoint).CanApply);
    Assert(!service.RemoveOwnedConfiguration().Success);
    Assert(File.ReadAllText(Path.Combine(directory, "config.txt")) == ApoConfigService.BeginMarker + "\n");
});
Add("previously absent config is generated and removed without leftovers", () =>
{
    var (service, directory) = NewService(null);
    Assert(service.Apply(service.Preview(EqOnly(), Endpoint)).Success);
    Assert(service.RemoveOwnedConfiguration().Success);
    Assert(File.ReadAllBytes(Path.Combine(directory, "config.txt")).Length == 0);
});
Add("unrecognized encoding rejects without mutation", () =>
{
    var (service, directory) = NewService(); var path = Path.Combine(directory, "config.txt");
    File.WriteAllBytes(path, [0x23, 0x20, 0x81, 0xFF]);
    Assert(!service.Preview(EqOnly(), Endpoint).CanApply);
    Assert(File.ReadAllBytes(path).SequenceEqual(new byte[] { 0x23, 0x20, 0x81, 0xFF }));
});
Add("oversized config remains untouched", () =>
{
    var (service, directory) = NewService(); var path = Path.Combine(directory, "config.txt");
    File.WriteAllText(path, "# " + new string('x', 1024 * 1024));
    var before = new FileInfo(path).Length;
    var plan = service.Preview(EqOnly(), Endpoint);
    Assert(!plan.CanApply && plan.Message.Contains("1 MiB"));
    Assert(new FileInfo(path).Length == before);
});
Add("malformed settings produce rejection instead of UI exception", () =>
{
    var (service, _) = NewService();
    var settings = EqOnly(); settings.Denoise = null!;
    Assert(!service.Preview(settings, Endpoint).CanApply);
    Assert(!service.Preview(null!, Endpoint).CanApply);
});

try
{
    foreach (var test in tests)
    {
        try { test.Run(); passed++; Console.WriteLine("PASS " + test.Name); }
        catch (Exception ex) { Console.WriteLine("FAIL " + test.Name + ": " + ex); }
    }
    Console.WriteLine($"APO synthetic configuration tests: {passed}/{tests.Count} passed. No system APO configuration or audio devices were changed.");
    Environment.ExitCode = passed == tests.Count ? 0 : 1;
}
finally
{
    // Delete only the freshly created, verified fixture subtree.
    var resolved = Path.GetFullPath(temporaryRoot);
    var expected = Path.GetFullPath(Path.GetTempPath());
    if (resolved.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("MicStudio-ApoTests-", StringComparison.Ordinal))
        Directory.Delete(resolved, true);
}
