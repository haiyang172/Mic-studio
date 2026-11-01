using NAudio.Wave;

namespace MicStudio.WindowsAudio;

/// <summary>One capture producer, one playback consumer; capacity is always bounded.</summary>
internal sealed class BoundedRenderBuffer
{
    private readonly Action _onReset;
    public BufferedWaveProvider Provider { get; }
    public BoundedRenderBuffer(int sampleRate, int channels, Action onReset)
    {
        _onReset = onReset;
        Provider = new(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels))
        { BufferDuration = TimeSpan.FromMilliseconds(300), ReadFully = true, DiscardOnBufferOverflow = false };
    }
    public void Add(byte[] bytes, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (count < 0 || count > bytes.Length || count % Provider.WaveFormat.BlockAlign != 0)
            throw new ArgumentException("线路音频包必须为完整采样帧。", nameof(count));
        if (count > Provider.BufferLength) throw new IOException("设备音频包超过线路缓冲上限。");
        if (Provider.BufferedBytes + count > Provider.BufferLength)
        {
            // This is a discontinuity, exposed as a reset count, not measured latency.
            Provider.ClearBuffer();
            _onReset();
        }
        Provider.AddSamples(bytes, 0, count);
    }
    public void Clear()
    {
        if (Provider.BufferedBytes == 0) return;
        Provider.ClearBuffer(); _onReset();
    }
}
