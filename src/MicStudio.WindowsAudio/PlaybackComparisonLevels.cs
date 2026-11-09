using NAudio.Wave;

namespace MicStudio.WindowsAudio;

internal static class PlaybackComparisonLevels
{
    internal const double SampleCeiling = 0.8912509381337456; // -1 dBFS sample peak.

    internal static double PeakGain(double peak) => SampleCeiling / Math.Max(SampleCeiling, peak);

    internal static (double Reference, double Processed, bool Matched) CalculateGains(
        double referenceRms, double referencePeak, double processedRms, double processedPeak, bool match)
    {
        var referenceGain = PeakGain(referencePeak);
        var processedGain = PeakGain(processedPeak);
        if (!match || referenceRms <= 1e-9 || processedRms <= 1e-9)
            return (referenceGain, processedGain, false);

        // Choose the quieter attainable RMS so both adjustments only attenuate.
        var target = Math.Min(referenceRms * referenceGain, processedRms * processedGain);
        return (target / referenceRms, target / processedRms, true);
    }

    // Resampling may overshoot the source's sample peak ceiling.
    internal sealed class OutputCeiling(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            const float limit = (float)SampleCeiling;
            for (var i = offset; i < offset + read; i++)
                buffer[i] = float.IsFinite(buffer[i]) ? Math.Clamp(buffer[i], -limit, limit) : 0;
            return read;
        }
    }
}
