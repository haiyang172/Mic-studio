using System.Numerics;

namespace MicStudio.Core;

/// <summary>RBJ cookbook biquad, normalized, transposed direct form II.</summary>
public sealed class Biquad
{
    private readonly double b0, b1, b2, a1, a2;
    private double z1, z2;

    public Biquad(EqBand band, int sampleRate)
    {
        var w = 2 * Math.PI * Math.Min(band.FrequencyHz, sampleRate * 0.49) / sampleRate;
        var c = Math.Cos(w); var s = Math.Sin(w); var alpha = s / (2 * band.Q);
        var A = Math.Pow(10, band.GainDb / 40); var sqrtA = Math.Sqrt(A);
        double b0n, b1n, b2n, a0n, a1n, a2n;
        switch (band.Type)
        {
            case EqFilterType.HighPass:
                b0n = (1 + c) / 2; b1n = -(1 + c); b2n = b0n; a0n = 1 + alpha; a1n = -2 * c; a2n = 1 - alpha; break;
            case EqFilterType.LowPass:
                b0n = (1 - c) / 2; b1n = 1 - c; b2n = b0n; a0n = 1 + alpha; a1n = -2 * c; a2n = 1 - alpha; break;
            case EqFilterType.LowShelf:
                b0n = A * ((A + 1) - (A - 1) * c + 2 * sqrtA * alpha); b1n = 2 * A * ((A - 1) - (A + 1) * c); b2n = A * ((A + 1) - (A - 1) * c - 2 * sqrtA * alpha);
                a0n = (A + 1) + (A - 1) * c + 2 * sqrtA * alpha; a1n = -2 * ((A - 1) + (A + 1) * c); a2n = (A + 1) + (A - 1) * c - 2 * sqrtA * alpha; break;
            case EqFilterType.HighShelf:
                b0n = A * ((A + 1) + (A - 1) * c + 2 * sqrtA * alpha); b1n = -2 * A * ((A - 1) + (A + 1) * c); b2n = A * ((A + 1) + (A - 1) * c - 2 * sqrtA * alpha);
                a0n = (A + 1) - (A - 1) * c + 2 * sqrtA * alpha; a1n = 2 * ((A - 1) - (A + 1) * c); a2n = (A + 1) - (A - 1) * c - 2 * sqrtA * alpha; break;
            default:
                b0n = 1 + alpha * A; b1n = -2 * c; b2n = 1 - alpha * A; a0n = 1 + alpha / A; a1n = -2 * c; a2n = 1 - alpha / A; break;
        }
        b0 = b0n / a0n; b1 = b1n / a0n; b2 = b2n / a0n; a1 = a1n / a0n; a2 = a2n / a0n;
    }

    public double Process(double input)
    {
        var output = input * b0 + z1;
        z1 = input * b1 - output * a1 + z2;
        z2 = input * b2 - output * a2;
        if (Math.Abs(z1) < 1e-25) z1 = 0;
        if (Math.Abs(z2) < 1e-25) z2 = 0;
        return output;
    }

    public double ResponseDb(double frequency, int sampleRate)
    {
        var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / sampleRate);
        var h = (b0 + b1 * z + b2 * z * z) / (1 + a1 * z + a2 * z * z);
        return 20 * Math.Log10(Math.Max(1e-20, h.Magnitude));
    }
}
