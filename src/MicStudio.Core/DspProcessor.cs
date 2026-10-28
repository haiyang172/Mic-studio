namespace MicStudio.Core;

/// <summary>One streaming processor per capture stream. Call Process from one audio thread.</summary>
public sealed class DspProcessor
{
    private readonly int sampleRate, channels;
    private State state;
    private State? pending;
    public double LastInputPeakDb { get; private set; } = double.NegativeInfinity;
    public double LastOutputPeakDb { get; private set; } = double.NegativeInfinity;
    public double LastCompressorReductionDb { get; private set; }
    public int AlgorithmLatencySamples => state.Settings.Denoise.Enabled && !state.Settings.Bypass ? SpectralDenoiser.FrameSize : 0;
    public double AlgorithmLatencyMs => AlgorithmLatencySamples * 1000.0 / sampleRate;

    public DspProcessor(int sampleRate, int channels, AudioSettings settings)
    {
        if (channels is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(channels));
        settings.Validate(sampleRate);
        this.sampleRate = sampleRate; this.channels = channels;
        state = new State(settings.Clone(), sampleRate, channels);
    }

    public void UpdateSettings(AudioSettings settings)
    {
        settings.Validate(sampleRate);
        // Only parameter/filter snapshots are prepared here. Streaming history is adopted on the audio thread.
        Interlocked.Exchange(ref pending, new State(settings.Clone(), sampleRate, channels, initializeHistory: false));
    }

    public void Reset() => Interlocked.Exchange(ref pending, new State(state.Settings.Clone(), sampleRate, channels) { DiscardHistory = true });

    public void Process(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count || count % channels != 0) throw new ArgumentOutOfRangeException(nameof(count));
        var updated = Interlocked.Exchange(ref pending, null);
        if (updated != null) { if (!updated.DiscardHistory) updated.AdoptHistory(state, channels); state = updated; }
        var s = state; var config = s.Settings;
        double inputPeak = 0, outputPeak = 0, reduction = 0;
        for (int frame = offset; frame < offset + count; frame += channels)
        {
            s.InputGain = s.TargetInputGain + (s.InputGain - s.TargetInputGain) * s.GainRamp;
            s.OutputGain = s.TargetOutputGain + (s.OutputGain - s.TargetOutputGain) * s.GainRamp;
            double framePeak = 0;
            for (int c = 0; c < channels; c++)
            {
                double sample = float.IsFinite(buffer[frame + c]) ? buffer[frame + c] : 0;
                inputPeak = Math.Max(inputPeak, Math.Abs(sample));
                if (!config.Bypass)
                {
                    if (config.Denoise.Enabled) sample = s.Denoisers[c].Process(sample, config.Denoise);
                    sample *= s.InputGain;
                    double filterInput = sample;
                    foreach (var filter in s.Filters[c]) sample = filter.Process(sample);
                    if (s.FilterFadeRemaining > 0 && s.PreviousFilters != null)
                    {
                        double previous = filterInput;
                        foreach (var filter in s.PreviousFilters[c]) previous = filter.Process(previous);
                        double progress = 1 - s.FilterFadeRemaining / (double)s.FilterFadeFrames;
                        sample = previous + (sample - previous) * progress;
                    }
                }
                s.Frame[c] = sample;
                framePeak = Math.Max(framePeak, Math.Abs(sample));
            }
            if (!config.Bypass)
            {
                double detectorCoeff = framePeak > s.Detector ? s.CompAttack : s.CompRelease;
                s.Detector = detectorCoeff * s.Detector + (1 - detectorCoeff) * framePeak;
                double detectorDb = Math.Max(-240, LinearToDb(s.Detector));
                double compDb = config.Compressor.Enabled ? Math.Min(0, (config.Compressor.ThresholdDb - detectorDb) * (1 - 1 / config.Compressor.Ratio)) : 0;
                reduction = Math.Max(reduction, -compDb);
                double targetCompGain = DbToLinear(compDb + (config.Compressor.Enabled ? config.Compressor.MakeupDb : 0));
                s.CompressorGain = targetCompGain + (s.CompressorGain - targetCompGain) * s.DynamicsRamp;
                double gain = s.CompressorGain;
                if (config.Gate.Enabled)
                {
                    s.GateDetector = Math.Max(framePeak, s.GateDetector * s.GateDetectorRelease);
                    double gateDb = Math.Max(-240, LinearToDb(s.GateDetector));
                    double target;
                    if (gateDb >= config.Gate.ThresholdDb) { s.GateHold = s.GateHoldFrames; target = 1; }
                    else if (s.GateHold > 0) { s.GateHold--; target = 1; }
                    else target = DbToLinear(Math.Max(-config.Gate.RangeDb, (gateDb - config.Gate.ThresholdDb) * (config.Gate.Ratio - 1)));
                    double gateCoeff = target > s.GateGain ? s.GateAttack : s.GateRelease;
                    s.GateGain = gateCoeff * s.GateGain + (1 - gateCoeff) * target;
                    gain *= s.GateGain;
                }
                double finalPeak = 0;
                for (int c = 0; c < channels; c++)
                {
                    double sample = s.Frame[c] * gain;
                    if (config.Creative.SaturationEnabled) sample = Math.Tanh(sample * s.Drive) / Math.Tanh(s.Drive);
                    if (config.Creative.RobotEnabled) sample *= Math.Sin(s.RobotPhase);
                    if (config.Creative.DelayEnabled)
                    {
                        int read = (s.DelayPosition + s.Delay[c].Length - s.DelayFrames) % s.Delay[c].Length;
                        var delayed = s.DelayWritten >= s.DelayFrames ? s.Delay[c][read] : 0;
                        s.Delay[c][s.DelayPosition] = sample + delayed * config.Creative.DelayFeedback;
                        sample = sample * (1 - config.Creative.DelayMix) + delayed * config.Creative.DelayMix;
                    }
                    if (config.Creative.ReverbEnabled)
                    {
                        double wet = 0;
                        for (int comb = 0; comb < 4; comb++)
                        {
                            var line = s.Reverb[c][comb];
                            int pos = s.ReverbPosition[comb];
                            double delayed = s.ReverbWritten >= line.Length ? line[pos] : 0;
                            wet += delayed; line[pos] = sample + delayed * 0.72;
                        }
                        sample = sample * (1 - config.Creative.ReverbMix) + wet * 0.25 * config.Creative.ReverbMix;
                    }
                    sample *= s.OutputGain;
                    if (!double.IsFinite(sample)) sample = 0;
                    s.Frame[c] = sample;
                    finalPeak = Math.Max(finalPeak, Math.Abs(sample));
                }
                double limiterTarget = finalPeak > s.Ceiling ? s.Ceiling / finalPeak : 1;
                if (config.Limiter.Enabled) s.LimiterGain = limiterTarget < s.LimiterGain ? limiterTarget : s.LimiterRelease * s.LimiterGain + (1 - s.LimiterRelease) * limiterTarget;
                else s.LimiterGain = 1;
                for (int c = 0; c < channels; c++) s.Frame[c] *= s.LimiterGain;
                s.RobotPhase = (s.RobotPhase + 2 * Math.PI * config.Creative.RobotFrequencyHz / sampleRate) % (2 * Math.PI);
                if (config.Creative.DelayEnabled)
                {
                    s.DelayPosition = (s.DelayPosition + 1) % s.Delay[0].Length;
                    s.DelayWritten = Math.Min(s.DelayWritten + 1, s.Delay[0].Length);
                }
                if (config.Creative.ReverbEnabled)
                {
                    for (int comb = 0; comb < 4; comb++) s.ReverbPosition[comb] = (s.ReverbPosition[comb] + 1) % s.Reverb[0][comb].Length;
                    s.ReverbWritten = Math.Min(s.ReverbWritten + 1, s.MaxReverbFrames);
                }
                if (s.FilterFadeRemaining > 0 && --s.FilterFadeRemaining == 0) s.PreviousFilters = null;
            }
            for (int c = 0; c < channels; c++)
            {
                buffer[frame + c] = (float)Math.Clamp(s.Frame[c], -float.MaxValue, float.MaxValue);
                outputPeak = Math.Max(outputPeak, Math.Abs((double)buffer[frame + c]));
            }
        }
        LastInputPeakDb = LinearToDb(inputPeak); LastOutputPeakDb = LinearToDb(outputPeak); LastCompressorReductionDb = reduction;
    }

    public static double DbToLinear(double db) => Math.Pow(10, db / 20);
    public static double LinearToDb(double linear) => linear > 0 ? 20 * Math.Log10(linear) : double.NegativeInfinity;

    /// <summary>Only the theoretical linear EQ response, without gains, dynamics or creative effects.</summary>
    public static double EqResponseDb(AudioSettings settings, double frequency, int sampleRate = 48000)
    {
        if (settings.Bypass || !settings.EqEnabled) return 0;
        double response = 0;
        foreach (var band in settings.Bands.Where(b => b.Enabled)) response += new Biquad(band, sampleRate).ResponseDb(frequency, sampleRate);
        return response;
    }

    private sealed class State
    {
        public AudioSettings Settings { get; }
        public Biquad[][] Filters { get; private set; }
        public Biquad[][]? PreviousFilters;
        public SpectralDenoiser[] Denoisers { get; private set; } = [];
        public double[] Frame { get; }
        public double[][] Delay { get; private set; } = [];
        public double[][][] Reverb { get; private set; } = [];
        public int[] ReverbPosition { get; } = new int[4];
        public int DelayPosition, DelayWritten, ReverbWritten, GateHold, FilterFadeRemaining;
        public bool DiscardHistory;
        public double RobotPhase, Detector, GateDetector, GateGain = 1, LimiterGain = 1, CompressorGain = 1, InputGain, OutputGain;
        public readonly double TargetInputGain, TargetOutputGain, GainRamp, DynamicsRamp, CompAttack, CompRelease, GateAttack, GateRelease, GateDetectorRelease, LimiterRelease, Ceiling, Drive;
        public readonly int GateHoldFrames;
        public readonly int FilterFadeFrames;
        public readonly int DelayFrames, MaxReverbFrames;

        public State(AudioSettings settings, int sampleRate, int channels, bool initializeHistory = true)
        {
            Settings = settings;
            Filters = Enumerable.Range(0, channels).Select(_ => settings.Bands.Where(b => settings.EqEnabled && b.Enabled).Select(b => new Biquad(b, sampleRate)).ToArray()).ToArray();
            Frame = new double[channels];
            DelayFrames = Math.Max(1, (int)(sampleRate * settings.Creative.DelayMs / 1000));
            double[] times = [0.0297, 0.0371, 0.0411, 0.0437];
            MaxReverbFrames = Math.Max(1, (int)(sampleRate * times[^1]));
            if (initializeHistory)
            {
                Denoisers = Enumerable.Range(0, channels).Select(_ => new SpectralDenoiser()).ToArray();
                // A fixed one-second ring supports every permitted delay without copying/allocating on parameter updates.
                Delay = Enumerable.Range(0, channels).Select(_ => new double[sampleRate]).ToArray();
                Reverb = Enumerable.Range(0, channels).Select(_ => times.Select(t => new double[Math.Max(1, (int)(sampleRate * t))]).ToArray()).ToArray();
            }
            InputGain = TargetInputGain = DbToLinear(settings.InputGainDb); OutputGain = TargetOutputGain = DbToLinear(settings.OutputGainDb);
            GainRamp = Coefficient(5, sampleRate); DynamicsRamp = Coefficient(2, sampleRate); FilterFadeFrames = Math.Max(1, sampleRate / 100);
            CompAttack = Coefficient(settings.Compressor.AttackMs, sampleRate); CompRelease = Coefficient(settings.Compressor.ReleaseMs, sampleRate);
            GateAttack = Coefficient(settings.Gate.AttackMs, sampleRate); GateRelease = Coefficient(settings.Gate.ReleaseMs, sampleRate); GateDetectorRelease = Coefficient(50, sampleRate);
            GateHoldFrames = (int)(settings.Gate.HoldMs * sampleRate / 1000);
            LimiterRelease = Coefficient(settings.Limiter.ReleaseMs, sampleRate); Ceiling = DbToLinear(settings.Limiter.CeilingDb); Drive = DbToLinear(settings.Creative.DriveDb);
        }

        public void AdoptHistory(State previous, int channels)
        {
            InputGain = previous.InputGain; OutputGain = previous.OutputGain;
            Detector = previous.Detector; GateDetector = previous.GateDetector;
            GateGain = previous.GateGain; GateHold = Math.Min(previous.GateHold, GateHoldFrames);
            LimiterGain = previous.LimiterGain; CompressorGain = previous.CompressorGain; RobotPhase = previous.RobotPhase;
            Denoisers = previous.Denoisers;
            if (Settings.Denoise.Enabled && !previous.Settings.Denoise.Enabled)
                foreach (var denoiser in Denoisers) denoiser.Reset();
            bool sameFilters = Settings.EqEnabled == previous.Settings.EqEnabled;
            for (int i = 0; i < Settings.Bands.Count; i++)
            {
                var a = Settings.Bands[i]; var b = previous.Settings.Bands[i];
                if (a.Enabled != b.Enabled || a.Type != b.Type || a.FrequencyHz != b.FrequencyHz || a.GainDb != b.GainDb || a.Q != b.Q) { sameFilters = false; break; }
            }
            if (sameFilters)
            {
                Filters = previous.Filters;
                // Preserve an unfinished transition if a gain-only update arrives mid-fade.
                PreviousFilters = previous.PreviousFilters; FilterFadeRemaining = previous.FilterFadeRemaining;
            }
            else { PreviousFilters = previous.Filters; FilterFadeRemaining = FilterFadeFrames; }
            Delay = previous.Delay;
            if (Settings.Creative.DelayEnabled && previous.Settings.Creative.DelayEnabled)
            {
                DelayPosition = previous.DelayPosition;
                DelayWritten = previous.DelayWritten;
            }
            Reverb = previous.Reverb;
            if (Settings.Creative.ReverbEnabled && previous.Settings.Creative.ReverbEnabled)
            {
                Array.Copy(previous.ReverbPosition, ReverbPosition, ReverbPosition.Length);
                ReverbWritten = previous.ReverbWritten;
            }
        }

        private static double Coefficient(double ms, int rate) => Math.Exp(-1 / (ms * 0.001 * rate));
    }
}
