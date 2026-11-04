using System.Buffers.Binary;

namespace MicStudio.WindowsAudio;

/// <summary>
/// Gates only the float CABLE send copy. Its caller serializes updates with the
/// capture callback and clears queued send packets immediately when closing it.
/// It never modifies the DSP samples used by meters, monitoring or recording.
/// </summary>
internal sealed class CableTransmitGate
{
    private readonly int _channels;
    private readonly int _fadeFrames;
    private readonly Action? _clearPending;
    private int _fadeFrame;
    private long _leaseExpiresAt;
    public bool Enabled { get; private set; }

    public CableTransmitGate(int sampleRate, int channels, bool enabled, Action? clearPending = null)
    {
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _fadeFrames = Math.Max(1, sampleRate / 200); // 5 ms, shared by every channel.
        _clearPending = clearPending;
        Enabled = enabled;
        _fadeFrame = enabled ? _fadeFrames : 0;
    }

    public static void ValidateLease(int leaseMilliseconds)
    {
        if (leaseMilliseconds != 0 && leaseMilliseconds is < 100 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(leaseMilliseconds), "A transmission lease must be zero or 100 to 1000 milliseconds.");
    }

    public bool SetEnabled(bool enabled, int leaseMilliseconds, long nowMilliseconds)
    {
        ValidateLease(leaseMilliseconds);
        if (nowMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
        ObserveEnabled(nowMilliseconds);
        if (!enabled) return Close();
        var changed = !Enabled;
        Enabled = true;
        if (changed) _fadeFrame = 0;
        _leaseExpiresAt = leaseMilliseconds == 0 ? 0 : checked(nowMilliseconds + leaseMilliseconds);
        return changed;
    }

    public bool ObserveEnabled(long nowMilliseconds)
    {
        if (Enabled && _leaseExpiresAt != 0 && nowMilliseconds >= _leaseExpiresAt) Close();
        return Enabled;
    }

    private bool Close()
    {
        _leaseExpiresAt = 0;
        if (!Enabled) return false;
        Enabled = false;
        _fadeFrame = 0;
        _clearPending?.Invoke();
        return true;
    }

    /// <returns>Bytes ready to enqueue; zero while muted, with the old send copy cleared.</returns>
    public int Encode(ReadOnlySpan<float> samples, Span<byte> destination, long nowMilliseconds)
    {
        if (samples.Length % _channels != 0) throw new ArgumentException("Transmission needs complete sample frames.", nameof(samples));
        var byteCount = checked(samples.Length * sizeof(float));
        if (destination.Length < byteCount) throw new ArgumentException("The send buffer is too small.", nameof(destination));
        if (!ObserveEnabled(nowMilliseconds)) { destination[..byteCount].Clear(); return 0; }
        for (var frame = 0; frame < samples.Length / _channels; frame++)
        {
            var gain = _fadeFrame < _fadeFrames ? ++_fadeFrame / (float)_fadeFrames : 1f;
            for (var channel = 0; channel < _channels; channel++)
            {
                var index = frame * _channels + channel;
                var sample = float.IsFinite(samples[index]) ? samples[index] : 0f;
                BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(index * sizeof(float), sizeof(float)), sample * gain);
            }
        }
        return byteCount;
    }
}
