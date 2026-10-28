using System.Buffers.Binary;
using System.Text.Json;
using MicStudio.Core;
using MicStudio.WindowsAudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

if (args.Contains("--input-volume"))
{
    using var devices = new AudioDeviceService();
    var input = devices.EnumerateInputs(false).First(x => x.IsDefaultCommunications && !x.IsCableReceive);
    var volume = devices.GetInputVolume(input.Id);
    Console.WriteLine(JsonSerializer.Serialize(new { input.Name, Capturing = false, WritesPerformed = false, Volume = volume }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Contains("--capture"))
{
    var index = Array.IndexOf(args, "--capture");
    if (index + 1 == args.Length) throw new ArgumentException("--capture requires an explicit endpoint ID or six-character device hash.");
    using var devices = new AudioDeviceService();
    var input = devices.EnumerateInputs(false).Single(x => x.Id == args[index + 1] || x.ShortId.Equals(args[index + 1], StringComparison.OrdinalIgnoreCase));
    Console.WriteLine("Explicit physical capture requested: 3 seconds, levels only, no recording or output.");
    using var engine = new AudioEngine(devices);
    var max = -160d;
    engine.MeterUpdated += (_, value) => max = Math.Max(max, value.InputPeakDb);
    engine.Start(new() { InputDeviceId = input.Id });
    Thread.Sleep(3000);
    engine.Stop();
    Console.WriteLine($"Captured peak: {max:F2} dBFS. Not proof of APO/Discord processing.");
    return;
}

if (args.Contains("--cable-smoke"))
{
    CableSmoke();
    return;
}

if (args.Contains("--live-update-smoke"))
{
    LiveUpdateSmoke();
    return;
}

if (args.Contains("--resolve-cable"))
{
    using var devices = new AudioDeviceService();
    var route = CableRouteResolver.Resolve(devices.EnumerateInputs(), devices.EnumerateOutputs(), "expired-diagnostic-id");
    Console.WriteLine(JsonSerializer.Serialize(new { route.IsReady, route.Status, route.Message,
        StaleIdRepaired = route.IsReady && route.Send!.Id != "expired-diagnostic-id", Capturing = false, SettingsWritten = false }));
    if (!route.IsReady) Environment.ExitCode = 1;
    return;
}

if (!args.Contains("--devices")) RunSyntheticTests();
if (args.Contains("--tests-only")) return;
using (var devices = new AudioDeviceService())
{
    var snapshot = new
    {
        Capturing = false,
        Inputs = devices.EnumerateInputs().Select(Redact).ToArray(),
        Outputs = devices.EnumerateOutputs().Select(Redact).ToArray()
    };
    Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
}

static object Redact(AudioDeviceInfo device) => new
{
    Device = device.ShortId,
    device.Name,
    device.State,
    device.IsDefaultConsole,
    device.IsDefaultCommunications,
    device.IsCableSend,
    device.IsCableReceive,
    device.SampleRate,
    device.Channels
};

static void RunSyntheticTests()
{
    var count = 0;
    Test("PCM unsigned 8-bit", () => Samples([0, 128, 255], new WaveFormat(48000, 8, 1), [-1, 0, 127f / 128]));
    Test("PCM signed 16-bit little endian", () => Samples([0, 128, 0, 0, 255, 127], new WaveFormat(48000, 16, 1), [-1, 0, 32767f / 32768]));
    Test("PCM signed 24-bit sign extension", () => Samples([0, 0, 128, 0, 0, 0, 255, 255, 127], new WaveFormat(48000, 24, 1), [-1, 0, 8388607f / 8388608]));
    Test("PCM signed 32-bit little endian", () => Samples([0, 0, 0, 128, 0, 0, 0, 0, 255, 255, 255, 127], new WaveFormat(48000, 32, 1), [-1, 0, 1]));
    Test("IEEE float and nonfinite sanitization", () =>
    {
        var values = new[] { -0.5f, 0.125f, float.NaN, float.PositiveInfinity };
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        Samples(bytes, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), [-0.5f, 0.125f, 0, 0]);
    });
    Test("Silence meters finite at -160 dBFS", () =>
    {
        var levels = AudioEngine.Levels(new float[100], 100);
        Assert(levels.Peak == -160 && levels.Rms == -160);
    });
    Test("Peak and RMS meter agreement", () =>
    {
        var levels = AudioEngine.Levels([0.5f, -0.5f], 2);
        Assert(Math.Abs(levels.Peak + 6.0206) < 0.0001 && Math.Abs(levels.Rms + 6.0206) < 0.0001);
    });
    Test("Mono maps equally to stereo", () =>
    {
        var map = new AudioEngine.ChannelAdapter(new ArraySource([0.2f, -0.4f], 1), 2);
        var values = new float[4];
        Assert(map.Read(values, 0, 4) == 4 && values.SequenceEqual([0.2f, 0.2f, -0.4f, -0.4f]));
    });
    Test("Stereo downmix averages channels", () =>
    {
        var map = new AudioEngine.ChannelAdapter(new ArraySource([0.2f, 0.4f, -0.4f, 0.4f], 2), 1);
        var values = new float[2];
        Assert(map.Read(values, 0, 2) == 2 && Math.Abs(values[0] - 0.3f) < 1e-6 && values[1] == 0);
    });
    Test("Surround unused channels contain silence", () =>
    {
        var map = new AudioEngine.ChannelAdapter(new ArraySource([0.2f, 0.4f], 2), 6);
        var values = new float[6];
        Assert(map.Read(values, 0, 6) == 6 && values.SequenceEqual([0.2f, 0.4f, 0, 0, 0, 0]));
    });
    Test("Monitor trim and sample ceiling", () =>
    {
        var monitor = new AudioEngine.SafeMonitor(new ArraySource([1, -1, 100, -100], 1));
        var values = new float[4];
        Assert(monitor.Read(values, 0, 4) == 4 && Math.Abs(values[0] - 0.25118864) < 1e-6 && values[2] == 0.95f && values[3] == -0.95f);
    });
    Test("Bounded queue reports resets and keeps newest packet", () =>
    {
        var resets = 0;
        var queue = new BoundedRenderBuffer(48000, 1, () => resets++);
        var bytes = new byte[queue.Provider.BufferLength];
        queue.Add(bytes, bytes.Length);
        var packet = new byte[1920]; packet[0] = 42;
        queue.Add(packet, packet.Length);
        var output = new byte[1920];
        Assert(resets == 1 && queue.Provider.BufferedBytes == packet.Length);
        Assert(queue.Provider.Read(output, 0, output.Length) == output.Length && output[0] == 42);
    });
    Test("Bounded queue rejects oversized and incomplete frames", () =>
    {
        var queue = new BoundedRenderBuffer(48000, 2, () => { });
        Throws<IOException>(() => queue.Add(new byte[queue.Provider.BufferLength + 8], queue.Provider.BufferLength + 8));
        Throws<ArgumentException>(() => queue.Add(new byte[4], 4));
    });
    Test("Empty render queue returns actual silence", () =>
    {
        var queue = new BoundedRenderBuffer(48000, 1, () => { });
        var output = Enumerable.Repeat((byte)0xff, 128).ToArray();
        Assert(queue.Provider.Read(output, 0, output.Length) == output.Length && output.All(x => x == 0));
    });
    Test("Capture options enforce selection before any audio activation", () =>
    {
        using var devices = new AudioDeviceService();
        using var engine = new AudioEngine(devices);
        Throws<ArgumentException>(() => engine.Start(new() { InputDeviceId = "" }));
        Throws<InvalidOperationException>(() => engine.Start(new() { InputDeviceId = "unused", MonitorOutputDeviceId = "unused" }));
        Assert(!engine.IsRunning && !engine.IsRecording && !engine.IsPlaying);
    });
    Test("System input controls reject invalid requests before touching an endpoint", () =>
    {
        using var devices = new AudioDeviceService();
        Throws<ArgumentException>(() => devices.GetInputVolume(""));
        Throws<ArgumentOutOfRangeException>(() => devices.SetInputVolume("unused", double.NaN));
        Throws<ArgumentOutOfRangeException>(() => devices.SetInputVolume("unused", -0.1));
        Throws<ArgumentOutOfRangeException>(() => devices.SetInputVolume("unused", 1.1));
    });
    Test("Concurrent stop and dispose is idempotent without capture", () =>
    {
        using var devices = new AudioDeviceService();
        var engine = new AudioEngine(devices);
        Parallel.For(0, 20, _ => engine.Stop());
        Parallel.For(0, 8, _ => engine.Dispose());
        Assert(!engine.IsRunning && !engine.IsRecording && !engine.IsPlaying);
    });
    Test("Live update never activates stopped or stale audio sessions", () =>
    {
        using var devices = new AudioDeviceService();
        using var engine = new AudioEngine(devices);
        var oldSession = engine.SessionId;
        Assert(!engine.TryUpdateSettings(NoEqSettings(), oldSession));
        engine.Stop();
        Assert(!engine.TryUpdateSettings(NoEqSettings(), oldSession));
        engine.UpdateSettings(NoEqSettings());
        Throws<ArgumentNullException>(() => engine.TryUpdateSettings(null!, engine.SessionId));
        Assert(!engine.IsRunning && !engine.IsRecording && !engine.IsPlaying);
    });
    Test("Endpoint cable direction is explicit", () =>
    {
        Assert(AudioDeviceService.IsCableInput("CABLE Input (VB-Audio Virtual Cable)"));
        Assert(!AudioDeviceService.IsCableInput("CABLE Output (VB-Audio Virtual Cable)"));
        Assert(AudioDeviceService.IsCableOutput("CABLE Output (VB-Audio Virtual Cable)"));
        Assert(AudioDeviceService.IsVirtualCableEndpoint("CABLE In 16ch (VB-Audio Virtual Cable)"));
    });
    Test("Cable auto route selects a standard matching pair without audio activation", () =>
    {
        var route = CableRouteResolver.Resolve([CableEndpoint("receive", "CABLE Output (VB-Audio Virtual Cable)", false)],
            [CableEndpoint("send", "CABLE Input (VB-Audio Virtual Cable)", true)]);
        Assert(route.IsReady && route.Send!.Id == "send" && route.Receive!.Id == "receive");
    });
    Test("Cable auto route preserves a valid saved non-default pair", () =>
    {
        var route = CableRouteResolver.Resolve(
            [CableEndpoint("r", "CABLE Output", false), CableEndpoint("ra", "CABLE-A Output", false)],
            [CableEndpoint("s", "CABLE Input", true), CableEndpoint("sa", "CABLE-A Input", true)], "sa");
        Assert(route.IsReady && route.Send!.Id == "sa" && route.Receive!.Id == "ra");
    });
    Test("Cable auto route replaces stale IDs and ignores enumeration order", () =>
    {
        var inputs = new[] { CableEndpoint("rb", "CABLE-B Output", false), CableEndpoint("ra", "CABLE-A Output", false), CableEndpoint("r", "CABLE Output", false) };
        var outputs = new[] { CableEndpoint("sb", "CABLE-B Input", true), CableEndpoint("sa", "CABLE-A Input", true), CableEndpoint("s", "CABLE Input", true) };
        var route = CableRouteResolver.Resolve(inputs, outputs, "removed-id");
        var reversed = CableRouteResolver.Resolve(inputs.Reverse().ToArray(), outputs.Reverse().ToArray(), "removed-id");
        Assert(route.Send!.Id == "s" && route.Receive!.Id == "r" && route == reversed);
    });
    Test("Cable auto route ignores unavailable saved send and unmatched preferred pair", () =>
    {
        var inputs = new[] { CableEndpoint("r", "CABLE Output", false), CableEndpoint("rb", "CABLE-B Output", false) };
        var outputs = new[] { CableEndpoint("s", "CABLE Input", true), CableEndpoint("sa", "CABLE-A Input", true), CableEndpoint("sb", "CABLE-B Input", true, "Unplugged") };
        Assert(CableRouteResolver.Resolve(inputs, outputs, "sb").Send!.Id == "s");
        Assert(CableRouteResolver.Resolve(inputs, outputs, "sa").Send!.Id == "s");
    });
    Test("Cable auto route never crosses cable families or channel labels", () =>
    {
        var crossed = CableRouteResolver.Resolve([CableEndpoint("ra", "CABLE-A Output", false)], [CableEndpoint("sb", "CABLE-B Input", true)]);
        var channels = CableRouteResolver.Resolve([CableEndpoint("r", "CABLE Output", false)], [CableEndpoint("s16", "CABLE In 16ch", true)]);
        Assert(crossed.Status == CableRouteStatus.UnmatchedEndpoints && !crossed.IsReady && channels.Status == CableRouteStatus.UnmatchedEndpoints);
    });
    Test("Cable auto route excludes physical devices and inconsistent direction flags", () =>
    {
        var physical = new AudioDeviceInfo("physical", "CABLE Input", "Active", true, true, false, false, 48000, 2);
        var wrongDirection = CableEndpoint("wrong", "CABLE Output", true);
        var route = CableRouteResolver.Resolve([CableEndpoint("r", "CABLE Output", false)], [physical, wrongDirection]);
        Assert(!route.IsReady && route.Status == CableRouteStatus.SendUnavailable && route.Send is null && route.Receive is null);
    });
    Test("Cable auto route reports missing inactive and unmatched endpoints distinctly", () =>
    {
        Assert(CableRouteResolver.Resolve([], []).Status == CableRouteStatus.NotDetected);
        Assert(CableRouteResolver.Resolve([CableEndpoint("r", "CABLE Output", false)], [CableEndpoint("s", "CABLE Input", true, "Disabled")]).Status == CableRouteStatus.SendUnavailable);
        Assert(CableRouteResolver.Resolve([CableEndpoint("r", "CABLE Output", false, "NotPresent")], [CableEndpoint("s", "CABLE Input", true)]).Status == CableRouteStatus.ReceiveUnavailable);
    });
    Test("Cable auto route handles C D and 16-channel names without substring replacement", () =>
    {
        var route = CableRouteResolver.Resolve([CableEndpoint("rd", "  cable-d OUT 16ch (Input word in driver description)", false)],
            [CableEndpoint("sd", "CABLE-D In 16ch (Output word in driver description)", true)]);
        Assert(route.IsReady && route.Send!.Id == "sd" && route.Receive!.Id == "rd");
        Assert(CableRouteResolver.IsSendName("CABLE-C Input (VB-Audio Cable C)") && CableRouteResolver.IsReceiveName("CABLE-C Output (VB-Audio Cable C)"));
    });
    Test("Cable name parser rejects misleading partial labels", () =>
    {
        foreach (var name in new[] { "Virtual CABLE Input", "CABLE InputPlus", "CABLE-AA Input", "CABLE Input_fake", "Speakers", "CABLE Out 16channels" })
            Assert(!CableRouteResolver.IsSendName(name) && !CableRouteResolver.IsReceiveName(name));
    });
    Test("Cable auto route does not guess across duplicate receive endpoints", () =>
    {
        var route = CableRouteResolver.Resolve([CableEndpoint("r1", "CABLE Output", false), CableEndpoint("r2", "CABLE Output", false)],
            [CableEndpoint("s", "CABLE Input", true)], "s");
        Assert(!route.IsReady && route.Status == CableRouteStatus.AmbiguousEndpoints);
    });
    Test("Cable auto route chooses a complete fallback over an ambiguous family", () =>
    {
        var route = CableRouteResolver.Resolve([CableEndpoint("r1", "CABLE Output", false), CableEndpoint("r2", "CABLE Output", false), CableEndpoint("ra", "CABLE-A Output", false)],
            [CableEndpoint("s", "CABLE Input", true), CableEndpoint("sa", "CABLE-A Input", true)]);
        Assert(route.IsReady && route.Send!.Id == "sa" && route.Receive!.Id == "ra");
    });
    Test("Offline same-source RMS match writes distinct files and preserves source", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav");
        CreateSyntheticWav(input);
        var original = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(input));
        var settings = NoEqSettings();
        settings.Bands[4].Enabled = true; settings.Bands[4].FrequencyHz = 1000; settings.Bands[4].GainDb = 8;
        var result = OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), settings);
        var reference = ReadFloatFile(result.ReferencePath); var wet = ReadFloatFile(result.ProcessedPath);
        var dryLevels = AudioEngine.Levels(reference, reference.Length); var wetLevels = AudioEngine.Levels(wet, wet.Length);
        Assert(result.LoudnessMatched && reference.Length == wet.Length && reference.Length == 48000);
        Assert(Math.Abs(dryLevels.Rms - wetLevels.Rms) < 0.001 && dryLevels.Peak <= -0.999 && wetLevels.Peak <= -0.999);
        Assert(result.ReferenceGainDb <= 0.001 && result.ProcessedGainDb <= 0.001);
        Assert(original.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(input))));
        Assert(reference.Zip(wet).Sum(x => Math.Abs(x.First - x.Second)) > 1);
    }));
    Test("Offline unnormalized DSP gain changes the real WAV", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input);
        var settings = NoEqSettings(); settings.InputGainDb = -6;
        var result = OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), settings, false);
        Assert(!result.LoudnessMatched && Math.Abs(result.ProcessedRmsDb - result.ReferenceRmsDb + 6) < 0.001);
    }));
    Test("Offline denoise delay is compensated without length mismatch", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input);
        var settings = NoEqSettings(); settings.Denoise.Enabled = true;
        var result = OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), settings);
        Assert(result.AlgorithmLatencySamples == 512 && ReadFloatFile(result.ReferencePath).Length == ReadFloatFile(result.ProcessedPath).Length);
    }));
    foreach (var rate in new[] { 8000, 16000 })
        Test($"Offline default EQ processes {rate} Hz WAV without changing saved settings", () => WithSyntheticDirectory(directory =>
        {
            var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input, sampleRate: rate);
            var original = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(input));
            var settings = new AudioSettings();
            var originalSettings = JsonSerializer.Serialize(settings, JsonDefaults.Options);
            var result = rate == 8000
                ? OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), settings)
                : OfflineAudioProcessor.ProcessFileAsync(input, Path.Combine(directory, "wet.wav"), settings).GetAwaiter().GetResult();
            var reference = ReadFloatFile(result.ReferencePath); var wet = ReadFloatFile(result.ProcessedPath);
            using var output = new WaveFileReader(result.ProcessedPath);
            Assert(result.SampleRate == rate && output.WaveFormat.SampleRate == rate && result.Channels == 1);
            Assert(reference.Length == rate && wet.Length == rate && wet.All(float.IsFinite));
            Assert(result.Duration == TimeSpan.FromSeconds(1) && result.ProcessedRmsDb > -60 && result.ProcessedPeakDb <= -0.999);
            Assert(result.Warning.Contains("低通", StringComparison.Ordinal) && result.Warning.Contains("原设置未修改", StringComparison.Ordinal));
            Assert(rate != 8000 || result.Warning.Contains("高架", StringComparison.Ordinal));
            Assert(JsonSerializer.Serialize(settings, JsonDefaults.Options) == originalSettings);
            Assert(original.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(input))));
        }));
    Test("Offline silence is finite and not falsely loudness-matched", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input, amplitude: 0);
        var result = OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), NoEqSettings());
        Assert(!result.LoudnessMatched && result.ReferenceRmsDb == -160 && result.ProcessedRmsDb == -160);
    }));
    Test("Offline refuses overwrites and same source paths", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input);
        Throws<ArgumentException>(() => OfflineAudioProcessor.ProcessFile(input, input, NoEqSettings()));
        var output = Path.Combine(directory, "existing.wav"); File.WriteAllText(output, "preserve");
        Throws<IOException>(() => OfflineAudioProcessor.ProcessFile(input, output, NoEqSettings()));
        Assert(File.ReadAllText(output) == "preserve");
    }));
    Test("Offline enforces the 120-second limit", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input, sampleRate: 8000, seconds: 121);
        Throws<ArgumentException>(() => OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), NoEqSettings()));
        Assert(!File.Exists(Path.Combine(directory, "wet.wav")));
    }));
    Test("Offline cancellation removes only generated temporary files", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Throws<OperationCanceledException>(() => OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), NoEqSettings(), cancellationToken: canceled.Token));
        Assert(Directory.GetFiles(directory).Length == 1 && File.Exists(input));
    }));
    Console.WriteLine($"PASS {count} WindowsAudio synthetic/idle tests. No microphone capture, recording, or playback was started.");

    void Test(string name, Action body) { body(); count++; Console.WriteLine($"PASS {name}"); }
}

static AudioDeviceInfo CableEndpoint(string id, string name, bool send, string state = "Active") =>
    new(id, name, state, false, false, send, !send, 48000, 2);

static void WithSyntheticDirectory(Action<string> body)
{
    var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var directory = Path.GetFullPath(Path.Combine(root, "micstudio-audio-test-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(directory);
    try { body(directory); }
    finally
    {
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directory).StartsWith("micstudio-audio-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Synthetic test cleanup path is outside its dedicated temporary directory.");
        Directory.Delete(directory, true);
    }
}
static void CreateSyntheticWav(string path, int sampleRate = 48000, int seconds = 1, double amplitude = 0.2)
{
    using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
    var data = new float[sampleRate];
    for (var i = 0; i < data.Length; i++) data[i] = (float)(amplitude * (Math.Sin(2 * Math.PI * 997 * i / sampleRate) + 0.5 * Math.Sin(2 * Math.PI * 3000 * i / sampleRate)));
    for (var second = 0; second < seconds; second++) writer.WriteSamples(data, 0, data.Length);
}
static float[] ReadFloatFile(string path)
{
    using var reader = new WaveFileReader(path);
    var source = reader.ToSampleProvider();
    var samples = new List<float>(); var buffer = new float[4096]; int read;
    while ((read = source.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.Take(read));
    return samples.ToArray();
}

static void Samples(byte[] bytes, WaveFormat format, float[] expected)
{
    var actual = new float[expected.Length];
    AudioEngine.Decode(bytes, actual, format);
    for (var i = 0; i < expected.Length; i++) Assert(Math.Abs(actual[i] - expected[i]) < 1e-6, $"sample {i}");
}
static void Assert(bool condition, string message = "assertion") { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

static AudioSettings NoEqSettings()
{
    var settings = new AudioSettings();
    foreach (var band in settings.Bands) band.Enabled = false;
    return settings;
}

static void LiveUpdateSmoke()
{
    using var devices = new AudioDeviceService();
    var send = devices.EnumerateOutputs(false).FirstOrDefault(x => x.IsCableSend);
    var receive = devices.EnumerateInputs(false).FirstOrDefault(x => x.IsCableReceive && send != null
        && x.Name.Equals(send.Name.Replace("Input", "Output", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase));
    if (send == null || receive == null)
        throw new InvalidOperationException("Matching CABLE Input/Output endpoints are required. No fallback device will be opened.");
    Console.WriteLine("Live settings smoke: synthetic 997 Hz at -36 dBFS, virtual CABLE only. No physical microphone, speaker, monitor, or file is used.");
    using var enumerator = new MMDeviceEnumerator();
    using var output = enumerator.GetDevice(send.Id);
    using var player = new WasapiOut(output, AudioClientShareMode.Shared, true, 40);
    var mix = output.AudioClient.MixFormat;
    player.Init(new ContinuousTone(mix.SampleRate, mix.Channels).ToWaveProvider());
    using var engine = new AudioEngine(devices);
    var readings = new System.Collections.Concurrent.ConcurrentQueue<AudioMeter>();
    engine.MeterUpdated += (_, value) => readings.Enqueue(value);
    var settings = NoEqSettings(); settings.Limiter.Enabled = false;
    engine.Start(new() { InputDeviceId = receive.Id, ProcessLocally = true, Settings = settings });
    var session = engine.SessionId;
    try
    {
        player.Play();
        WaitForResponse("baseline", 0);
        settings.InputGainDb = -6;
        Update(settings);
        WaitForResponse("live input gain", -6);
        settings.EqEnabled = true;
        settings.Bands[4].Enabled = true;
        settings.Bands[4].FrequencyHz = 997;
        settings.Bands[4].GainDb = 6;
        Update(settings);
        WaitForResponse("live EQ gain", 0);
        settings.EqEnabled = false;
        Update(settings);
        WaitForResponse("live EQ master off", -6);
        Assert(!engine.TryUpdateSettings(settings, session - 1), "A stale session must reject its update.");
        Assert(engine.IsRunning && engine.SessionId == session, "A stale update changed the running session.");
        for (var revision = 0; revision < 60; revision++)
        {
            settings.InputGainDb = -12 + revision % 12;
            Update(settings);
        }
        settings.InputGainDb = -3;
        Update(settings);
        WaitForResponse("latest of rapid serialized changes", -3);
        Assert(!engine.IsRecording && !engine.IsPlaying && engine.OutputBufferResets == 0);
        Console.WriteLine($"PASS live updates used one capture session ({session}) without restart, recording, or monitor.");
    }
    finally
    {
        engine.Stop();
        player.Stop();
    }
    Assert(!engine.TryUpdateSettings(settings, session) && !engine.IsRunning,
        "An update after stop must remain stopped.");

    void Update(AudioSettings revision)
    {
        readings.Clear();
        Assert(engine.TryUpdateSettings(revision, session), "The current session rejected a live update.");
        Assert(engine.SessionId == session && engine.IsRunning, "Live settings restarted capture.");
    }

    void WaitForResponse(string label, double expectedDb)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        var stable = 0;
        var measured = double.NaN;
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            while (readings.TryDequeue(out var meter))
            {
                Assert(meter.SessionId == session && engine.SessionId == session && engine.IsRunning,
                    "The live update changed the capture session.");
                if (meter.InputRmsDb < -70) { stable = 0; continue; }
                measured = meter.OutputRmsDb - meter.InputRmsDb;
                stable = Math.Abs(measured - expectedDb) < 0.08 ? stable + 1 : 0;
                if (stable >= 3)
                {
                    Console.WriteLine($"PASS {label}: {measured:F3} dB (expected {expectedDb:F1}); session unchanged.");
                    return;
                }
            }
            Thread.Sleep(20);
        }
        throw new Exception($"{label}: expected {expectedDb:F1} dB, measured {measured:F3} dB; no stable response in 3 seconds.");
    }
}

static void CableSmoke()
{
    using var devices = new AudioDeviceService();
    var send = devices.EnumerateOutputs(false).FirstOrDefault(x => x.IsCableSend);
    var receive = devices.EnumerateInputs(false).FirstOrDefault(x => x.IsCableReceive && send != null
        && x.Name.Equals(send.Name.Replace("Input", "Output", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase));
    if (send == null || receive == null) throw new InvalidOperationException("Matching CABLE Input/Output endpoints are required. No fallback device will be opened.");
    Console.WriteLine("Synthetic CABLE-only smoke: 997 Hz, -36 dBFS, 1 second. No physical microphone, speaker, or file is used.");
    using var enumerator = new MMDeviceEnumerator();
    using var input = enumerator.GetDevice(receive.Id);
    using var output = enumerator.GetDevice(send.Id);
    using var capture = new WasapiCapture(input, false, 20);
    var captureFormat = capture.WaveFormat;
    double sumSin = 0, sumCos = 0, power = 0;
    long frames = 0;
    var sync = new object();
    capture.DataAvailable += (_, e) =>
    {
        var values = new float[e.BytesRecorded / (captureFormat.BitsPerSample / 8)];
        AudioEngine.Decode(e.Buffer.AsSpan(0, e.BytesRecorded), values, captureFormat);
        lock (sync)
            for (var i = 0; i + captureFormat.Channels <= values.Length; i += captureFormat.Channels)
            {
                var sample = values[i];
                var phase = 2 * Math.PI * 997 * frames / captureFormat.SampleRate;
                sumSin += sample * Math.Sin(phase); sumCos += sample * Math.Cos(phase); power += sample * sample; frames++;
            }
    };
    using var player = new WasapiOut(output, AudioClientShareMode.Shared, true, 40);
    var mix = output.AudioClient.MixFormat;
    player.Init(new Tone(mix.SampleRate, mix.Channels).ToWaveProvider());
    capture.StartRecording();
    Thread.Sleep(150);
    player.Play();
    Thread.Sleep(1200);
    player.Stop();
    capture.StopRecording();
    capture.Dispose();
    double rms, toneAmplitude;
    lock (sync)
    {
        rms = Math.Sqrt(power / Math.Max(1, frames));
        toneAmplitude = 2 * Math.Sqrt(sumSin * sumSin + sumCos * sumCos) / Math.Max(1, frames);
    }
    Console.WriteLine($"Received frames: {frames}; RMS: {20 * Math.Log10(Math.Max(1e-8, rms)):F2} dBFS; 997 Hz projection: {20 * Math.Log10(Math.Max(1e-8, toneAmplitude)):F2} dBFS.");
    Assert(frames > captureFormat.SampleRate / 2 && toneAmplitude > 0.001, "CABLE synthetic tone was not detected.");
    Console.WriteLine("PASS synthetic CABLE route. This does not validate physical microphone, APO, Discord, perceived quality, or end-to-end latency.");

    using var engine = new AudioEngine(devices);
    using var measured = new ManualResetEventSlim();
    var gainDifference = double.NaN;
    engine.MeterUpdated += (_, value) =>
    {
        if (value.InputPeakDb < -60) return;
        gainDifference = value.OutputPeakDb - value.InputPeakDb;
        if (Math.Abs(gainDifference + 6) < 0.05) measured.Set();
    };
    var settings = NoEqSettings(); settings.InputGainDb = -6;
    settings.Limiter.Enabled = false;
    engine.Start(new() { InputDeviceId = receive.Id, ProcessLocally = true, Settings = settings });
    using var secondOutput = enumerator.GetDevice(send.Id);
    using var secondPlayer = new WasapiOut(secondOutput, AudioClientShareMode.Shared, true, 40);
    var secondMix = secondOutput.AudioClient.MixFormat;
    secondPlayer.Init(new Tone(secondMix.SampleRate, secondMix.Channels).ToWaveProvider());
    secondPlayer.Play();
    Assert(measured.Wait(2500), "Application capture-to-DSP synthetic -6 dB path did not match expected response.");
    Parallel.For(0, 12, _ => engine.Stop());
    secondPlayer.Stop();
    Assert(!engine.IsRunning && !engine.IsRecording);
    Console.WriteLine($"PASS application AudioEngine virtual capture + native DSP: measured {gainDifference:F3} dB change.");
    for (var repeat = 0; repeat < 8; repeat++)
    {
        engine.Start(new() { InputDeviceId = receive.Id });
        engine.Stop();
    }
    Assert(!engine.IsRunning && !engine.IsRecording);
    Console.WriteLine("PASS virtual capture concurrent stop and 8 immediate start/stop races. No physical microphone, recording, or monitor used.");
}

sealed class ArraySource(float[] samples, int channels) : ISampleProvider
{
    private int _position;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
    public int Read(float[] buffer, int offset, int count)
    {
        var read = Math.Min(count, samples.Length - _position);
        Array.Copy(samples, _position, buffer, offset, read); _position += read;
        return read;
    }
}

sealed class Tone(int sampleRate, int channels) : ISampleProvider
{
    private int _frame;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    public int Read(float[] buffer, int offset, int count)
    {
        var frames = Math.Min(count / channels, sampleRate - _frame);
        for (var f = 0; f < frames; f++, _frame++)
            for (var c = 0; c < channels; c++) buffer[offset + f * channels + c] = (float)(0.01584893 * Math.Sin(2 * Math.PI * 997 * _frame / sampleRate));
        return frames * channels;
    }
}

sealed class ContinuousTone(int sampleRate, int channels) : ISampleProvider
{
    private long _frame;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / channels;
        for (var f = 0; f < frames; f++, _frame++)
            for (var c = 0; c < channels; c++)
                buffer[offset + f * channels + c] = (float)(0.01584893 * Math.Sin(2 * Math.PI * 997 * _frame / sampleRate));
        return frames * channels;
    }
}
