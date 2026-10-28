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
    Test("Endpoint cable direction is explicit", () =>
    {
        Assert(AudioDeviceService.IsCableInput("CABLE Input (VB-Audio Virtual Cable)"));
        Assert(!AudioDeviceService.IsCableInput("CABLE Output (VB-Audio Virtual Cable)"));
        Assert(AudioDeviceService.IsCableOutput("CABLE Output (VB-Audio Virtual Cable)"));
        Assert(AudioDeviceService.IsVirtualCableEndpoint("CABLE In 16ch (VB-Audio Virtual Cable)"));
    });
    Test("Offline same-source RMS match writes distinct files and preserves source", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav");
        CreateSyntheticWav(input);
        var original = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(input));
        var settings = new AudioSettings();
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
        var result = OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), new() { InputGainDb = -6 }, false);
        Assert(!result.LoudnessMatched && Math.Abs(result.ProcessedRmsDb - result.ReferenceRmsDb + 6) < 0.001);
    }));
    Test("Offline denoise delay is compensated without length mismatch", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input);
        var settings = new AudioSettings(); settings.Denoise.Enabled = true;
        var result = OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), settings);
        Assert(result.AlgorithmLatencySamples == 512 && ReadFloatFile(result.ReferencePath).Length == ReadFloatFile(result.ProcessedPath).Length);
    }));
    Test("Offline silence is finite and not falsely loudness-matched", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input, amplitude: 0);
        var result = OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), new());
        Assert(!result.LoudnessMatched && result.ReferenceRmsDb == -160 && result.ProcessedRmsDb == -160);
    }));
    Test("Offline refuses overwrites and same source paths", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input);
        Throws<ArgumentException>(() => OfflineAudioProcessor.ProcessFile(input, input, new()));
        var output = Path.Combine(directory, "existing.wav"); File.WriteAllText(output, "preserve");
        Throws<IOException>(() => OfflineAudioProcessor.ProcessFile(input, output, new()));
        Assert(File.ReadAllText(output) == "preserve");
    }));
    Test("Offline enforces the 120-second limit", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input, sampleRate: 8000, seconds: 121);
        Throws<ArgumentException>(() => OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), new()));
        Assert(!File.Exists(Path.Combine(directory, "wet.wav")));
    }));
    Test("Offline cancellation removes only generated temporary files", () => WithSyntheticDirectory(directory =>
    {
        var input = Path.Combine(directory, "source.wav"); CreateSyntheticWav(input);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Throws<OperationCanceledException>(() => OfflineAudioProcessor.ProcessFile(input, Path.Combine(directory, "wet.wav"), new(), cancellationToken: canceled.Token));
        Assert(Directory.GetFiles(directory).Length == 1 && File.Exists(input));
    }));
    Console.WriteLine($"PASS {count} WindowsAudio synthetic/idle tests. No microphone capture, recording, or playback was started.");

    void Test(string name, Action body) { body(); count++; Console.WriteLine($"PASS {name}"); }
}

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
    var settings = new AudioSettings { InputGainDb = -6 };
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
