using MicStudio.Core;
using MicStudio.WindowsAudio;
using NAudio.Wave;

internal static class LoopTuningChecks
{
    private const int Rate = 48000;

    internal static int Run()
    {
        var count = 0;
        void Test(string name, Action body)
        {
            body();
            count++;
            Console.WriteLine("PASS " + name);
        }

        Test("Looping stereo preserves complete frames and position across uneven reads", () =>
        {
            const int clipFrames = 725;
            var clip = new float[clipFrames * 2];
            for (var frame = 0; frame < clipFrames; frame++)
            {
                clip[frame * 2] = .2f;
                clip[frame * 2 + 1] = -.1f;
            }
            var whole = new LoopTuningSampleProvider(clip, Rate, 2, Settings(), false);
            var pieces = new LoopTuningSampleProvider(clip, Rate, 2, Settings(), false);
            var expected = Read(whole, clip.Length * 3);
            var actual = Enumerable.Repeat(1f, expected.Length + 2).ToArray();
            var partial = 0;
            while (partial < expected.Length)
            {
                var take = Math.Min(186, expected.Length - partial);
                Require(pieces.Read(actual, partial + 1, take) == take);
                partial += take;
            }
            Require(actual[0] == 1 && actual[^1] == 1 && expected.SequenceEqual(actual.Skip(1).Take(expected.Length)));
            Require(expected.Take(clip.Length).SequenceEqual(expected.Skip(clip.Length).Take(clip.Length)));
            Require(expected[0] == 0 && expected[clip.Length - 1] == 0);
            Require(expected[700] == .2f && expected[701] == -.1f);
        });

        Test("Changing loop DSP applies the new gain while keeping the current position", () =>
        {
            var clip = Enumerable.Range(0, Rate).Select(index => .03f + index * .000004f).ToArray();
            var loop = new LoopTuningSampleProvider(clip, Rate, 1, Settings(), false);
            var prefix = Read(loop, 2711);
            Near(prefix[^1], clip[prefix.Length - 1], 1e-6);
            loop.UpdateSettings(Settings(6));
            var updated = Read(loop, 8192);
            Near(updated[2400], clip[prefix.Length + 2400] * DspProcessor.DbToLinear(6), 5e-6);
            Near(updated[^1], clip[prefix.Length + updated.Length - 1] * DspProcessor.DbToLinear(6), 1e-6);
        });

        Test("Loop A/B switches between RMS matching and audible gain during playback", () =>
        {
            var clip = Enumerable.Range(0, Rate * 2)
                .Select(i => (float)(.08 * Math.Sin(2 * Math.PI * 1000 * i / Rate))).ToArray();
            double MeasuredGain(LoopTuningSampleProvider loop)
            {
                loop.SetProcessed(true);
                Read(loop, 9600); // Allow the branch crossfade and comparison gains to settle.
                var processed = Read(loop, 4800);
                loop.SetProcessed(false);
                Read(loop, 960); // Allow the branch crossfade to settle.
                var reference = Read(loop, 4800);
                return 20 * Math.Log10(Rms(processed) / Rms(reference));
            }

            var loop = new LoopTuningSampleProvider(clip, Rate, 1, Settings(6), true);
            Near(MeasuredGain(loop), 0, .005);
            loop.SetLoudnessMatching(false);
            Near(MeasuredGain(loop), 6, .005);
            loop.SetLoudnessMatching(true);
            Near(MeasuredGain(loop), 0, .005);

            var suggestion = new LoopTuningSampleProvider(clip, Rate, 1, Settings(6), false);
            Near(MeasuredGain(suggestion), 6, .005);
        });

        Test("Loop A/B reference follows the processed DSP latency across the loop boundary", () =>
        {
            var clip = Enumerable.Range(0, 2900).Select(index => .04f + index * .00001f).ToArray();
            var settings = Settings();
            settings.Denoise.Enabled = true;
            var latency = new DspProcessor(Rate, 1, settings).AlgorithmLatencySamples;
            Require(latency > 0);
            var loop = new LoopTuningSampleProvider(clip, Rate, 1, settings, false);
            loop.SetProcessed(false);
            Read(loop, clip.Length + latency + 410);
            var reference = Read(loop, 1024);
            Near(reference[0], clip[410], 1e-6);
            Near(reference[^1], clip[1433], 1e-6);
        });

        Test("Loop samples stay finite and below the comparison ceiling after excessive source peaks", () =>
        {
            var clip = Enumerable.Repeat(4f, Rate / 50).ToArray();
            clip[500] = float.NaN;
            clip[501] = float.NegativeInfinity;
            var loop = new LoopTuningSampleProvider(clip, Rate, 1, Settings(6), false);
            var output = Read(loop, clip.Length * 2);
            Require(output.All(sample => float.IsFinite(sample) && Math.Abs(sample) <= (float)PlaybackComparisonLevels.SampleCeiling));
            Require(output[500] == 0 && output[501] == 0);
            Near(output[400], PlaybackComparisonLevels.SampleCeiling, 1e-6);
        });

        Test("Loop source loads the WAV without rewriting it and honors cancellation before file access", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)))
                {
                    var samples = Enumerable.Repeat(.125f, 1920).ToArray();
                    writer.WriteSamples(samples, 0, samples.Length);
                }
                var original = File.ReadAllBytes(path);
                var loop = LoopTuningSampleProvider.Load(path, Settings(), false, CancellationToken.None);
                Near(Read(loop, 960)[500], .125, 1e-6);
                Require(original.SequenceEqual(File.ReadAllBytes(path)));
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                Throws<OperationCanceledException>(() =>
                    LoopTuningSampleProvider.Load(path + ".absent", Settings(), false, canceled.Token));
            }
            finally { File.Delete(path); }
        });

        Test("Loop tuning rejects unconfirmed headphones, missing source and inactive sessions before opening playback", () =>
        {
            using var engine = new AudioEngine();
            Throws<ArgumentException>(() => engine.StartLoopTuningAsync("", true, Settings()).GetAwaiter().GetResult());
            Throws<InvalidOperationException>(() =>
                engine.StartLoopTuningAsync("fixture-output", false, Settings()).GetAwaiter().GetResult());
            Throws<InvalidOperationException>(() =>
                engine.StartLoopTuningAsync("fixture-output", true, Settings()).GetAwaiter().GetResult());
            engine.StopPlayback();
            Require(!engine.IsRunning && !engine.IsRecording && !engine.IsPlaying && engine.LoopTuningSessionId == null);
            Require(!engine.TrySetLoopTuningProcessed(true, 1) && !engine.TryUpdateLoopTuningSettings(Settings(), 1));
            Require(!engine.TrySetLoopTuningLoudnessMatching(false, 1));
        });
        return count;
    }

    private static AudioSettings Settings(double gain = 0) =>
        new() { InputGainDb = gain, EqEnabled = false, Limiter = new() { Enabled = false } };

    private static float[] Read(ISampleProvider provider, int samples)
    {
        var buffer = new float[samples];
        Require(provider.Read(buffer, 0, samples) == samples);
        return buffer;
    }

    private static double Rms(float[] buffer) => Math.Sqrt(buffer.Average(sample => (double)sample * sample));

    private static void Near(double actual, double expected, double tolerance) =>
        Require(Math.Abs(actual - expected) <= tolerance, $"Expected {expected}, measured {actual}.");

    private static void Require(bool condition, string message = "Loop tuning assertion failed.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
