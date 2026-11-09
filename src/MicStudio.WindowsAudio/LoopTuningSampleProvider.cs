using MicStudio.Core;
using NAudio.Wave;

namespace MicStudio.WindowsAudio;

/// <summary>One audition stream owns the clip and DSP. Only the render thread reads samples.</summary>
internal sealed class LoopTuningSampleProvider : ISampleProvider
{
    private const int MaximumSeconds = 30;
    private readonly float[] _clip;
    private readonly float[] _reference;
    private readonly float[] _processed;
    private readonly DspProcessor _dsp;
    private readonly int _channels, _clipFrames, _blockFrames, _edgeFadeFrames;
    private readonly double _mixStep, _powerDecay, _gainStep;
    private long _nextFrame;
    private int _offset;
    private int _useProcessed = 1;
    private bool _matchLoudness;
    private double _mix = 1, _referencePower, _processedPower;
    private double _referenceGain = 1, _processedGain = 1;

    public WaveFormat WaveFormat { get; }

    internal static LoopTuningSampleProvider Load(string path, AudioSettings settings, bool matchLoudness,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;
        if (format.SampleRate is < 8000 or > 384000 || format.Channels is < 1 or > 8 ||
            format.BlockAlign < 1 || reader.Length == 0 || reader.Length % format.BlockAlign != 0 ||
            reader.Length / format.BlockAlign > (long)format.SampleRate * MaximumSeconds)
            throw new InvalidDataException(UiText.T("循环调音需要最长 30 秒、格式完整的 WAV 录音。",
                "Loop tuning requires a complete WAV recording of up to 30 seconds."));
        var frames = reader.Length / format.BlockAlign;

        // Decode off the UI and render threads. At typical 48 kHz stereo this uses up to 12 MB.
        var samples = new float[checked((int)(frames * format.Channels))];
        var source = reader.ToSampleProvider();
        for (var offset = 0; offset < samples.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(samples.Length - offset, 2048 * format.Channels);
            var read = source.Read(samples, offset, count);
            if (read <= 0 || read % format.Channels != 0)
                throw new InvalidDataException(UiText.T("录音包含不完整的采样帧。", "The recording has incomplete sample frames."));
            offset += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(samples, format.SampleRate, format.Channels, settings, matchLoudness);
    }

    internal LoopTuningSampleProvider(float[] clip, int sampleRate, int channels, AudioSettings settings,
        bool matchLoudness)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (sampleRate is < 8000 or > 384000 || channels is < 1 or > 8 ||
            clip.Length == 0 || clip.Length % channels != 0 || clip.Length / channels > sampleRate * MaximumSeconds)
            throw new ArgumentException("Invalid loop sample format.", nameof(clip));
        _clip = clip;
        _channels = channels;
        _clipFrames = clip.Length / channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _blockFrames = sampleRate / 100;
        _edgeFadeFrames = Math.Min(sampleRate / 200, _clipFrames / 4);
        _reference = new float[_blockFrames * channels];
        _processed = new float[_reference.Length];
        _offset = _processed.Length;
        _dsp = new(sampleRate, channels, settings.CreateProcessingSnapshot(sampleRate, out _));
        _matchLoudness = matchLoudness;
        _mixStep = 100.0 / sampleRate;
        _powerDecay = Math.Exp(-_blockFrames / (0.5 * sampleRate));
        _gainStep = 1 - Math.Exp(-1 / (0.02 * sampleRate));
    }

    // Settings snapshots are prepared on the caller thread and adopted at a DSP block boundary.
    internal void UpdateSettings(AudioSettings settings) =>
        _dsp.UpdateSettings(settings.CreateProcessingSnapshot(WaveFormat.SampleRate, out _));

    internal void SetProcessed(bool enabled) => Volatile.Write(ref _useProcessed, enabled ? 1 : 0);

    internal void SetLoudnessMatching(bool match) => Volatile.Write(ref _matchLoudness, match);

    public int Read(float[] buffer, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > buffer.Length - count || count % _channels != 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        for (var written = 0; written < count;)
        {
            if (_offset == _processed.Length) FillBlock();
            var copy = Math.Min(count - written, _processed.Length - _offset);
            Array.Copy(_processed, _offset, buffer, offset + written, copy);
            written += copy;
            _offset += copy;
        }
        return count;
    }

    private void FillBlock()
    {
        ReadClip(_processed, _nextFrame);
        _dsp.Process(_processed, 0, _processed.Length);
        // Both sides represent the same source position after the processor's latency.
        ReadClip(_reference, _nextFrame - _dsp.AlgorithmLatencySamples);
        var (referencePeak, referencePower) = Measure(_reference);
        var (processedPeak, processedPower) = Measure(_processed);
        _referencePower = _nextFrame == 0 ? referencePower : SmoothPower(_referencePower, referencePower);
        _processedPower = _nextFrame == 0 ? processedPower : SmoothPower(_processedPower, processedPower);
        var (referenceTarget, processedTarget, _) = PlaybackComparisonLevels.CalculateGains(
            Math.Sqrt(_referencePower), referencePeak, Math.Sqrt(_processedPower), processedPeak,
            Volatile.Read(ref _matchLoudness));
        if (_nextFrame == 0)
        {
            _referenceGain = referenceTarget;
            _processedGain = processedTarget;
        }

        var referencePeakGain = PlaybackComparisonLevels.PeakGain(referencePeak);
        var processedPeakGain = PlaybackComparisonLevels.PeakGain(processedPeak);
        var targetMix = Volatile.Read(ref _useProcessed);
        for (var index = 0; index < _processed.Length;)
        {
            _mix += Math.Clamp(targetMix - _mix, -_mixStep, _mixStep);
            _referenceGain += (referenceTarget - _referenceGain) * _gainStep;
            _processedGain += (processedTarget - _processedGain) * _gainStep;
            var a = Math.Min(referencePeakGain, _referenceGain) * (1 - _mix);
            var b = Math.Min(processedPeakGain, _processedGain) * _mix;
            for (var channel = 0; channel < _channels; channel++, index++)
                _processed[index] = (float)(_reference[index] * a + _processed[index] * b);
        }
        _nextFrame += _blockFrames;
        _offset = 0;
    }

    private double SmoothPower(double previous, double current) =>
        _powerDecay * previous + (1 - _powerDecay) * current;

    private void ReadClip(float[] block, long startFrame)
    {
        var sourceFrame = (int)(Math.Max(0, startFrame) % _clipFrames);
        for (var i = 0; i < block.Length; i += _channels, startFrame++)
        {
            if (startFrame < 0)
            {
                Array.Clear(block, i, _channels);
                continue;
            }
            var edgeDistance = Math.Min(sourceFrame, _clipFrames - 1 - sourceFrame);
            var gain = edgeDistance < _edgeFadeFrames ? (float)edgeDistance / _edgeFadeFrames : 1;
            for (var channel = 0; channel < _channels; channel++)
            {
                var sample = _clip[sourceFrame * _channels + channel];
                block[i + channel] = float.IsFinite(sample) ? sample * gain : 0;
            }
            if (++sourceFrame == _clipFrames) sourceFrame = 0;
        }
    }

    private static (double Peak, double Power) Measure(float[] block)
    {
        double peak = 0, power = 0;
        for (var i = 0; i < block.Length; i++)
        {
            var sample = float.IsFinite(block[i]) ? (double)block[i] : 0;
            block[i] = (float)sample;
            peak = Math.Max(peak, Math.Abs(sample));
            power += sample * sample;
        }
        return (peak, power / block.Length);
    }
}
