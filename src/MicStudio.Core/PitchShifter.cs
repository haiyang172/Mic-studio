namespace MicStudio.Core;

/// <summary>
/// Small delay-line pitch shifter for the creative voice previews. Two read heads
/// sweep through a ring buffer at the requested ratio and crossfade with triangular
/// windows. It is a real pitch shift, not speed-up playback or ring modulation, but it
/// does not preserve formants; that limitation is stated in the UI and release notes.
/// </summary>
internal sealed class PitchShifter
{
    private const double GrainSeconds = 0.045;
    private readonly int grain, size;
    private readonly double[] buffer;
    private int write;
    private double delay1, delay2;
    private bool started;

    public PitchShifter(int sampleRate)
    {
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        grain = Math.Max(128, (int)(sampleRate * GrainSeconds));
        size = 1;
        while (size < grain * 4) size <<= 1;
        buffer = new double[size];
        Reset();
    }

    public int LatencySamples => grain / 2;

    public static int LatencyFor(int sampleRate) => Math.Max(128, (int)(sampleRate * GrainSeconds)) / 2;

    public void Reset()
    {
        Array.Clear(buffer);
        write = 0;
        delay1 = grain * 0.5;
        delay2 = (delay1 + grain * 0.5) % grain;
        if (delay2 < 1) delay2 = 1;
        started = false;
    }

    public double Process(double sample, double ratio)
    {
        if (!started) { started = true; delay1 = grain * 0.5; delay2 = grain - 1; }
        double s1 = Read(write, delay1);
        double s2 = Read(write, delay2);
        double position = (delay1 - 1) / Math.Max(1, grain - 1);
        double w1 = 1 - Math.Abs(2 * position - 1);
        double output = s1 * w1 + s2 * (1 - w1);
        buffer[write] = sample;
        write = (write + 1) % size;

        delay1 -= ratio - 1;
        if (delay1 < 1) delay1 += grain - 1;
        if (delay1 >= grain) delay1 -= grain - 1;
        delay2 = delay1 + grain * 0.5;
        if (delay2 >= grain) delay2 -= grain;
        if (delay2 < 1) delay2 = 1;
        return double.IsFinite(output) ? output : 0;
    }

    private double Read(int writeIndex, double delay)
    {
        double position = writeIndex - delay;
        while (position < 0) position += size;
        while (position >= size) position -= size;
        int first = (int)position;
        double fraction = position - first;
        int second = (first + 1) % size;
        return buffer[first] * (1 - fraction) + buffer[second] * fraction;
    }
}
