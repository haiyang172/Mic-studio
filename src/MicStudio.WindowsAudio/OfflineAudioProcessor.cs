using MicStudio.Core;
using NAudio.Wave;

namespace MicStudio.WindowsAudio;

public sealed record OfflineComparisonResult(string ReferencePath, string ProcessedPath, int SampleRate,
    int Channels, TimeSpan Duration, double ReferenceGainDb, double ProcessedGainDb,
    double ReferenceRmsDb, double ProcessedRmsDb, double ReferencePeakDb, double ProcessedPeakDb,
    bool LoudnessMatched, int AlgorithmLatencySamples, string Warning);

/// <summary>
/// Processes an existing short WAV without opening an audio device. Outputs a new
/// reference and processed pair, with whole-file RMS matching and a sample peak ceiling.
/// RMS is not ITU integrated loudness; sample peaks are not intersample true peaks.
/// </summary>
public static class OfflineAudioProcessor
{
    public static Task<OfflineComparisonResult> ProcessFileAsync(string inputPath, string outputPath,
        AudioSettings settings, bool matchLoudness = true, string? referencePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.Clone();
        return Task.Run(() => ProcessFile(inputPath, outputPath, snapshot, matchLoudness, referencePath, cancellationToken), cancellationToken);
    }

    public static OfflineComparisonResult ProcessFile(string inputPath, string outputPath,
        AudioSettings settings, bool matchLoudness = true, string? referencePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        inputPath = Path.GetFullPath(inputPath);
        outputPath = Path.GetFullPath(outputPath);
        referencePath = Path.GetFullPath(referencePath ?? Path.Combine(Path.GetDirectoryName(outputPath)!,
            Path.GetFileNameWithoutExtension(outputPath) + ".reference.wav"));
        if (inputPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase)
            || inputPath.Equals(referencePath, StringComparison.OrdinalIgnoreCase)
            || outputPath.Equals(referencePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("输入、参考和处理后录音必须使用不同的文件名。");
        if (File.Exists(outputPath) || File.Exists(referencePath)) throw new IOException("输出文件已经存在，请使用新的文件名。原录音不会被覆盖。");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
        var temporary = new List<string>();
        string TemporaryNear(string path)
        {
            var file = Path.Combine(Path.GetDirectoryName(path)!, ".micstudio-" + Guid.NewGuid().ToString("N") + ".wav");
            temporary.Add(file); return file;
        }
        var unscaledWet = TemporaryNear(outputPath);
        var finalWet = TemporaryNear(outputPath);
        var finalReference = TemporaryNear(referencePath);
        try
        {
            using var input = new WaveFileReader(inputPath);
            var rate = input.WaveFormat.SampleRate;
            var channels = input.WaveFormat.Channels;
            if (channels is < 1 or > 8 || rate is < 8000 or > 384000 || input.TotalTime.TotalSeconds > 120 || input.TotalTime <= TimeSpan.Zero)
                throw new ArgumentException("离线比较支持 1–8 声道、8–384 kHz、最长 120 秒的非空 WAV。");
            settings.Validate(rate);
            var processor = new DspProcessor(rate, channels, settings);
            var latencySamples = processor.AlgorithmLatencySamples;
            var skip = latencySamples * channels;
            var source = input.ToSampleProvider();
            var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
            var buffer = new float[2048 * channels];
            var dryStats = new Statistics();
            var wetStats = new Statistics();
            long sampleCount = 0;
            using (var writer = CreateWriter(unscaledWet, format))
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (read % channels != 0) throw new InvalidDataException("WAV 音频包含不完整的采样帧。");
                    sampleCount += read;
                    if (sampleCount > (long)rate * channels * 120) throw new InvalidDataException("WAV 超过 120 秒上限。");
                    Sanitize(buffer, read);
                    dryStats.Add(buffer, 0, read);
                    processor.Process(buffer, 0, read);
                    var offset = Math.Min(skip, read); skip -= offset;
                    var count = read - offset;
                    if (count > 0) { writer.WriteSamples(buffer, offset, count); wetStats.Add(buffer, offset, count); }
                }
                var tail = latencySamples * channels;
                while (tail > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(buffer.Length, tail);
                    Array.Clear(buffer, 0, count);
                    processor.Process(buffer, 0, count);
                    var offset = Math.Min(skip, count); skip -= offset;
                    var remaining = count - offset;
                    if (remaining > 0) { writer.WriteSamples(buffer, offset, remaining); wetStats.Add(buffer, offset, remaining); }
                    tail -= count;
                }
            }
            if (sampleCount == 0 || dryStats.Count != wetStats.Count) throw new InvalidDataException("音频为空或无法对齐算法缓冲。");
            const double ceiling = 0.8912509381337456; // -1 dBFS sample peak, not true peak.
            var referenceGain = Math.Min(1, ceiling / Math.Max(ceiling, dryStats.Peak));
            var wetGain = Math.Min(1, ceiling / Math.Max(ceiling, wetStats.Peak));
            var matched = matchLoudness && dryStats.Rms > 1e-9 && wetStats.Rms > 1e-9;
            if (matched)
            {
                // Both gains only attenuate. The safer attainable common RMS is used,
                // so matching never amplifies one signal into clipping.
                var target = Math.Min(dryStats.Rms * referenceGain, wetStats.Rms * wetGain);
                referenceGain = target / dryStats.Rms;
                wetGain = target / wetStats.Rms;
            }
            WriteScaled(inputPath, finalReference, referenceGain, cancellationToken);
            WriteScaled(unscaledWet, finalWet, wetGain, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // No existing files are ever replaced. Each completed file is moved on the
            // same volume; if a name is taken concurrently, fail rather than overwrite.
            File.Move(finalReference, referencePath, false);
            File.Move(finalWet, outputPath, false);
            return new(referencePath, outputPath, rate, channels,
                TimeSpan.FromSeconds((double)sampleCount / rate / channels), Db(referenceGain), Db(wetGain),
                Db(dryStats.Rms * referenceGain), Db(wetStats.Rms * wetGain),
                Db(dryStats.Peak * referenceGain), Db(wetStats.Peak * wetGain), matched, latencySamples,
                "同一次录音的离线比较；使用整段 RMS 对齐，并非 LUFS。−1 dBFS 为采样峰值上限，并非真峰值。输入可能已含系统效果，请使用本地 DSP 前采集的测试源；创意效果尾音按原录音时长截断。"
                + (matchLoudness && !matched ? " 一份音频接近静音，未进行 RMS 匹配。" : ""));
        }
        finally
        {
            foreach (var path in temporary)
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WriteScaled(string inputPath, string outputPath, double gain, CancellationToken cancellation)
    {
        using var input = new WaveFileReader(inputPath);
        var source = input.ToSampleProvider();
        using var output = CreateWriter(outputPath, WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, source.WaveFormat.Channels));
        var buffer = new float[2048 * source.WaveFormat.Channels];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++) buffer[i] = float.IsFinite(buffer[i]) ? (float)(buffer[i] * gain) : 0;
            output.WriteSamples(buffer, 0, read);
        }
    }
    private static void Sanitize(float[] samples, int count)
    { for (var i = 0; i < count; i++) if (!float.IsFinite(samples[i])) samples[i] = 0; }
    private static WaveFileWriter CreateWriter(string path, WaveFormat format)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try { return new(stream, format); } catch { stream.Dispose(); throw; }
    }
    private static double Db(double value) => value > 1e-8 ? 20 * Math.Log10(value) : -160;
    private sealed class Statistics
    {
        public long Count { get; private set; }
        public double Peak { get; private set; }
        private double _power;
        public double Rms => Count == 0 ? 0 : Math.Sqrt(_power / Count);
        public void Add(float[] samples, int offset, int count)
        {
            for (var i = offset; i < offset + count; i++)
            {
                if (!float.IsFinite(samples[i])) throw new InvalidDataException("处理后的音频包含无效采样。");
                var sample = (double)samples[i]; Peak = Math.Max(Peak, Math.Abs(sample)); _power += sample * sample;
            }
            Count += count;
        }
    }
}
