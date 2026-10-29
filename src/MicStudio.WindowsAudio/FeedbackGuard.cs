using System.Numerics;

namespace MicStudio.WindowsAudio;

/// <summary>
/// Conservative output circuit breaker for sustained loud, narrow-band feedback.
/// This is not echo cancellation. A trigger stays muted until the stream is restarted.
/// </summary>
internal sealed class FeedbackGuard
{
    private readonly int sampleRate, channels, size, hop;
    private readonly double[] history, window;
    private readonly Complex[] spectrum;
    private int cursor, filled, sinceAnalysis, candidateFrames, highFrames, loudFrames;
    private double candidateFrequency, candidateMinimumDb = 0;
    public bool IsMuted { get; private set; }
    public double FrequencyHz { get; private set; }
    public double TriggerPeakDb { get; private set; }

    public FeedbackGuard(int sampleRate, int channels)
    {
        if (sampleRate is < 8000 or > 384000 || channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.sampleRate = sampleRate; this.channels = channels;
        size = 256; while (size < sampleRate / 47) size *= 2;
        hop = size / 2;
        history = new double[size]; window = new double[size]; spectrum = new Complex[size];
        for (var i = 0; i < size; i++) window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / size);
    }

    /// <returns>True only on the packet where protection first latched.</returns>
    public bool Process(float[] samples, int count)
    {
        if (count < 0 || count > samples.Length || count % channels != 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (IsMuted) { Array.Clear(samples, 0, count); return false; }
        for (var frame = 0; frame < count; frame += channels)
        {
            // Follow the strongest channel without cancelling antiphase stereo.
            double sample = 0;
            for (var c = 0; c < channels; c++)
            {
                var value = float.IsFinite(samples[frame + c]) ? samples[frame + c] : 0;
                if (Math.Abs(value) > Math.Abs(sample)) sample = value;
            }
            history[cursor] = sample; cursor = (cursor + 1) % size;
            filled = Math.Min(filled + 1, size); sinceAnalysis++;
            if (filled != size || sinceAnalysis < hop) continue;
            sinceAnalysis = 0;
            if (Analyze())
            {
                IsMuted = true; Array.Clear(samples, 0, count); return true;
            }
        }
        return false;
    }

    private bool Analyze()
    {
        double sum = 0, power = 0, peak = 0;
        for (var i = 0; i < size; i++) { var v = history[i]; sum += v; power += v * v; peak = Math.Max(peak, Math.Abs(v)); }
        var rmsDb = Db(Math.Sqrt(power / size));
        if (rmsDb < -24) { ClearCandidate(); return false; }
        var mean = sum / size;
        for (var i = 0; i < size; i++) spectrum[i] = new Complex((history[(cursor + i) % size] - mean) * window[i], 0);
        Transform(spectrum);
        var first = Math.Max(1, (int)Math.Ceiling(650.0 * size / sampleRate));
        var last = Math.Min(size / 2 - 2, (int)(Math.Min(10000, sampleRate * .45) * size / sampleRate));
        double total = 0, strongest = 0; var bin = -1;
        for (var i = 1; i < size / 2; i++) total += Power(spectrum[i]);
        for (var i = first; i <= last; i++)
        {
            var energy = Power(spectrum[i - 1]) + Power(spectrum[i]) + Power(spectrum[i + 1]);
            if (energy > strongest) { strongest = energy; bin = i; }
        }
        if (bin < 0 || total < 1e-12 || strongest / total < .93) { ClearCandidate(); return false; }
        var frequency = bin * (double)sampleRate / size;
        if (candidateFrames == 0 || Math.Abs(frequency - candidateFrequency) > Math.Max(80, 2.0 * sampleRate / size))
        {
            candidateFrames = highFrames = loudFrames = 0;
            candidateFrequency = frequency; candidateMinimumDb = rmsDb;
        }
        candidateFrames += hop;
        candidateMinimumDb = Math.Min(candidateMinimumDb, rmsDb);
        highFrames = rmsDb >= -12 ? highFrames + hop : 0;
        loudFrames = rmsDb >= -8 ? loudFrames + hop : 0;
        var risingFeedback = highFrames >= sampleRate * .35 && rmsDb - candidateMinimumDb >= 6;
        var establishedLoudTone = loudFrames >= sampleRate * .8 || highFrames >= sampleRate * 1.2;
        if (!risingFeedback && !establishedLoudTone) return false;
        FrequencyHz = frequency; TriggerPeakDb = Db(peak); return true;
    }

    private void ClearCandidate() { candidateFrames = highFrames = loudFrames = 0; }
    private static double Power(Complex value) => value.Real * value.Real + value.Imaginary * value.Imaginary;
    private static double Db(double value) => 20 * Math.Log10(Math.Max(1e-8, value));

    private static void Transform(Complex[] values)
    {
        var n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var start = 0; start < n; start += length)
            {
                var phase = Complex.One;
                for (var i = 0; i < length / 2; i++)
                {
                    var even = values[start + i]; var odd = values[start + i + length / 2] * phase;
                    values[start + i] = even + odd; values[start + i + length / 2] = even - odd; phase *= step;
                }
            }
        }
    }
}
