using System.Numerics;

namespace MicStudio.Core;

/// <summary>
/// Conservative STFT Wiener suppression using a user-set white noise floor.
/// This is real frequency-domain attenuation, not a noise gate or RNNoise.
/// Adds exactly 512 samples of algorithm buffering; not an end-to-end latency measurement.
/// </summary>
internal sealed class SpectralDenoiser
{
    public const int FrameSize = 512;
    private const int Hop = FrameSize / 4;
    private readonly double[] input = new double[FrameSize], output = new double[FrameSize], window = new double[FrameSize], gains = new double[FrameSize];
    private readonly Complex[] spectrum = new Complex[FrameSize];
    private int cursor, sinceFrame;

    public SpectralDenoiser()
    {
        for (int i = 0; i < FrameSize; i++)
        {
            window[i] = Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FrameSize));
            gains[i] = 1;
        }
    }

    public double Process(double sample, DenoiseSettings settings)
    {
        double result = output[cursor];
        output[cursor] = 0;
        input[cursor] = sample;
        cursor = (cursor + 1) % FrameSize;
        if (++sinceFrame != Hop) return result;
        sinceFrame = 0;
        for (int i = 0; i < FrameSize; i++) spectrum[i] = input[(cursor + i) % FrameSize] * window[i];
        Fft(spectrum, false);
        // Expected per-bin white noise power after windowing: variance * sum(window²).
        double noisePower = Math.Pow(10, settings.NoiseFloorDb / 10) * FrameSize / 2;
        double minGain = Math.Pow(10, -24 * settings.Strength / 20);
        for (int i = 0; i < FrameSize; i++)
        {
            var power = spectrum[i].Magnitude * spectrum[i].Magnitude;
            var wiener = Math.Sqrt(Math.Max(0, 1 - noisePower / Math.Max(power, 1e-25)));
            var target = Math.Max(minGain, 1 - settings.Strength + settings.Strength * wiener);
            gains[i] = 0.6 * gains[i] + 0.4 * target;
            spectrum[i] *= gains[i];
        }
        Fft(spectrum, true);
        // At 75% overlap, summed sqrt-Hann analysis/synthesis weights equal 2.
        for (int i = 0; i < FrameSize; i++) output[(cursor + i) % FrameSize] += spectrum[i].Real * window[i] / 2;
        return result;
    }

    private static void Fft(Complex[] values, bool inverse)
    {
        int n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / length);
            for (int start = 0; start < n; start += length)
            {
                Complex w = 1;
                for (int j = 0; j < length / 2; j++)
                {
                    var even = values[start + j]; var odd = values[start + j + length / 2] * w;
                    values[start + j] = even + odd; values[start + j + length / 2] = even - odd;
                    w *= step;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) values[i] /= n;
    }
}
