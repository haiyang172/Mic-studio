namespace MicStudio.WindowsAudio;

/// <summary>A timestamp-indexed half-second history. Unwritten/expired reference is silence, never stale playback.</summary>
internal sealed class EchoReferenceBuffer
{
    private readonly object sync = new();
    private readonly float[] samples;
    private readonly long[] positions;
    private int generation;
    private bool disabled;
    public int Generation => Volatile.Read(ref generation);

    public EchoReferenceBuffer(int sampleRate)
    {
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        samples = new float[sampleRate / 2]; positions = new long[samples.Length]; Array.Fill(positions, long.MinValue);
    }

    public void Push(long firstFrame, ReadOnlySpan<float> input, bool discontinuity = false)
    {
        lock (sync)
        {
            if (disabled) return;
            if (discontinuity) { Array.Fill(positions, long.MinValue); Interlocked.Increment(ref generation); }
            var first = Math.Max(0, input.Length - samples.Length);
            for (var i = first; i < input.Length; i++)
            {
                var frame = firstFrame + i; if (frame < 0) continue;
                var slot = (int)(frame % samples.Length);
                samples[slot] = float.IsFinite(input[i]) ? Math.Clamp(input[i], -1, 1) : 0;
                positions[slot] = frame;
            }
        }
    }

    public bool Read(long firstFrame, short[] destination)
    {
        var valid = 0;
        lock (sync)
        {
            if (disabled) { Array.Clear(destination); return false; }
            for (var i = 0; i < destination.Length; i++)
            {
                var frame = firstFrame + i;
                var slot = frame < 0 ? -1 : (int)(frame % samples.Length);
                if (slot >= 0 && positions[slot] == frame) { destination[i] = ToPcm(samples[slot]); valid++; }
                else destination[i] = 0;
            }
        }
        return valid >= destination.Length * .9;
    }

    public void Disable()
    {
        lock (sync)
        {
            if (disabled) return;
            disabled = true; Array.Fill(positions, long.MinValue); Interlocked.Increment(ref generation);
        }
    }

    internal static short ToPcm(float value) => (short)Math.Round(Math.Clamp(float.IsFinite(value) ? value : 0, -1, 1) * 32767);
}

/// <summary>Linear streaming conversion; fractional phase and previous sample survive callback boundaries.</summary>
internal sealed class MonoReferenceResampler
{
    private readonly double step;
    private long inputPosition = -1;
    private double nextOutputPosition;
    private float previous;
    public MonoReferenceResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        step = inputRate / (double)outputRate;
    }
    public int Convert(ReadOnlySpan<float> input, Span<float> output)
    {
        var written = 0;
        foreach (var current in input)
        {
            inputPosition++;
            if (inputPosition == 0) previous = current;
            while (nextOutputPosition <= inputPosition)
            {
                if (written >= output.Length) throw new ArgumentException("Reference resampling destination is too small.");
                var fraction = inputPosition == 0 ? 0 : nextOutputPosition - (inputPosition - 1);
                output[written++] = previous + (current - previous) * (float)Math.Clamp(fraction, 0, 1);
                nextOutputPosition += step;
            }
            previous = current;
        }
        return written;
    }
}
