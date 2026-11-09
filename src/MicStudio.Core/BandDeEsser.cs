namespace MicStudio.Core;

/// <summary>Linked, zero-lookahead attenuation of the sibilance band.</summary>
internal sealed class BandDeEsser
{
    private const double Damping = 1 / 0.9;
    private const double MaximumReductionDb = 12;
    private const double MinimumReduction = 0.000001;

    private struct Channel
    {
        public double Integrator1;
        public double Integrator2;
        public double BandSample;
    }

    private readonly int sampleRate;
    private readonly Channel[] channels;
    private readonly double detectorAttack, detectorRelease, reductionAttack, reductionRelease, frequencyRamp;
    private double bandPower, fullPower, reduction;
    private double strength, thresholdPower, currentWarp, targetWarp;
    private double a1, a2, a3;

    public BandDeEsser(int sampleRate, int channelCount, DeEsserSettings settings, bool bypass)
    {
        this.sampleRate = sampleRate;
        channels = new Channel[channelCount];
        detectorAttack = Coefficient(2);
        detectorRelease = Coefficient(20);
        reductionAttack = Coefficient(1);
        reductionRelease = Coefficient(30);
        frequencyRamp = Coefficient(5);
        Configure(settings, bypass);
    }

    public bool IsActive => strength > 0 || reduction >= MinimumReduction;

    public void Configure(DeEsserSettings settings, bool bypass)
    {
        var wasActive = IsActive;
        strength = bypass ? 0 : settings.StrengthPercent / 100;
        thresholdPower = Math.Pow(10, settings.ThresholdDb / 10);
        targetWarp = Math.Tan(Math.PI * settings.FrequencyForSampleRate(sampleRate) / sampleRate);
        if (bypass || !wasActive || !IsActive)
        {
            Array.Clear(channels);
            bandPower = fullPower = reduction = 0;
            SetFilter(targetWarp);
        }
    }

    public double Process(double[] frame, double framePeak)
    {
        if (currentWarp != targetWarp)
        {
            var next = targetWarp + (currentWarp - targetWarp) * frequencyRamp;
            SetFilter(Math.Abs(next - targetWarp) < 1e-8 ? targetWarp : next);
        }

        double bandPeak = 0;
        for (var index = 0; index < channels.Length; index++)
        {
            // The state-variable band-pass can track a moving center while retaining its integrators.
            ref var channel = ref channels[index];
            var input = frame[index] - channel.Integrator2;
            var band = a1 * channel.Integrator1 + a2 * input;
            var low = channel.Integrator2 + a2 * channel.Integrator1 + a3 * input;
            channel.Integrator1 = WithoutDenormal(2 * band - channel.Integrator1);
            channel.Integrator2 = WithoutDenormal(2 * low - channel.Integrator2);
            channel.BandSample = Damping * band;
            bandPeak = Math.Max(bandPeak, Math.Abs(channel.BandSample));
        }

        bandPower = FollowPower(bandPeak, bandPower);
        fullPower = FollowPower(framePeak, fullPower);
        double targetReduction = 0;
        if (strength > 0 && bandPower > thresholdPower)
        {
            // Require meaningful energy in the selected band as well as an absolute level.
            // This avoids engaging when a loud vowel only leaks a little energy into the band.
            var relativePower = bandPower / Math.Max(1e-30, fullPower);
            var dominance = Math.Clamp((relativePower - 0.04) / 0.16, 0, 1);
            var excessDb = 10 * Math.Log10(bandPower / thresholdPower);
            var reductionDb = Math.Min(MaximumReductionDb, excessDb * 0.75) * strength * dominance;
            targetReduction = 1 - DspProcessor.DbToLinear(-reductionDb);
        }
        var smoothing = targetReduction > reduction ? reductionAttack : reductionRelease;
        reduction = targetReduction + (reduction - targetReduction) * smoothing;
        if (reduction < MinimumReduction)
        {
            if (targetReduction == 0) reduction = 0;
            return framePeak;
        }

        // Subtract only the selected band and link the gain without mixing channel samples.
        framePeak = 0;
        for (var index = 0; index < channels.Length; index++)
        {
            frame[index] -= channels[index].BandSample * reduction;
            framePeak = Math.Max(framePeak, Math.Abs(frame[index]));
        }
        return framePeak;
    }

    private void SetFilter(double warp)
    {
        currentWarp = warp;
        a1 = 1 / (1 + warp * (warp + Damping));
        a2 = warp * a1;
        a3 = warp * a2;
    }

    private double FollowPower(double peak, double previous)
    {
        var power = peak * peak;
        var coefficient = power > previous ? detectorAttack : detectorRelease;
        var result = power + (previous - power) * coefficient;
        return result < 1e-36 ? 0 : result;
    }

    private double Coefficient(double milliseconds) => Math.Exp(-1000 / (milliseconds * sampleRate));
    private static double WithoutDenormal(double sample) => Math.Abs(sample) < 1e-25 ? 0 : sample;
}
