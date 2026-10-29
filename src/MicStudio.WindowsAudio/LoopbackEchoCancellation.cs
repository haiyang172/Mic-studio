using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicStudio.WindowsAudio;

public sealed record EchoCancellationState(bool Enabled, bool IsActive, bool ReferenceAvailable,
    int LatencySamples, int ReferenceResets, string Code, string Message, string? ReferenceName = null, int SessionId = 0);

/// <summary>Local playback reference capture plus native SpeexDSP. Does not record or upload the reference.</summary>
internal sealed class LoopbackEchoCancellation : IDisposable
{
    private readonly MMDevice device;
    private readonly WasapiLoopbackCapture capture;
    private readonly EchoReferenceBuffer reference;
    private readonly EchoFrameProcessor processor;
    private readonly int rate, channels, sourceRate, sourceChannels;
    private readonly string referenceName, referenceId;
    private readonly bool automaticReference;
    private readonly WaveFormat decodeFormat;
    private readonly long origin = Stopwatch.GetTimestamp();
    private MonoReferenceResampler resampler;
    private float[] decoded = [], mono = [], converted = [];
    private long expectedReferenceFrame = long.MinValue, expectedMicrophoneFrame = long.MinValue;
    private volatile bool referenceStopped;
    private string failureCode = "reference_unavailable";
    private volatile bool disposed;
    public string ReferenceDeviceId => referenceId;

    private LoopbackEchoCancellation(MMDevice device, int sampleRate, int channels, bool automaticReference)
    {
        this.device = device; rate = sampleRate; this.channels = channels;
        referenceName = device.FriendlyName; referenceId = device.ID;
        this.automaticReference = automaticReference;
        // One reference frame of tolerance absorbs callback ordering jitter. This
        // offsets reference history, not additional output buffering.
        reference = new(rate); processor = new(rate, channels, reference, referenceDelayFrames: rate / 100);
        try
        {
            capture = new WasapiLoopbackCapture(device);
            var format = capture.WaveFormat;
            decodeFormat = format is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : format;
            if (!((decodeFormat.Encoding == WaveFormatEncoding.IeeeFloat && decodeFormat.BitsPerSample == 32)
                || (decodeFormat.Encoding == WaveFormatEncoding.Pcm && decodeFormat.BitsPerSample is 8 or 16 or 24 or 32)))
                throw new NotSupportedException("播放参考格式不受支持。");
            sourceRate = format.SampleRate; sourceChannels = format.Channels;
            resampler = new(sourceRate, rate);
            capture.DataAvailable += OnReferenceData;
            capture.RecordingStopped += OnReferenceStopped;
            capture.StartRecording();
        }
        catch
        {
            try { capture?.Dispose(); } finally { processor.Dispose(); }
            throw;
        }
    }

    public static LoopbackEchoCancellation? TryCreate(int rate, int channels, string? selectedId, int sessionId, out EchoCancellationState state)
    {
        if (rate is < 8000 or > 96000 || rate % 100 != 0 || channels is < 1 or > 8)
        {
            state = new(true, false, false, 0, 0, "format_unsupported", "当前采样格式不支持回声消除，当前音频保持原样。", SessionId: sessionId); return null;
        }
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (selectedId == null)
            {
                var discord = PlaybackRoutingInspector.ActiveDiscordPhysicalOutputIds(enumerator, out var inspected);
                if (!inspected)
                {
                    state = new(true, false, false, 0, 0, "routing_unavailable", "无法确认播放参考，请在设备页明确选择外放设备。", SessionId: sessionId); return null;
                }
                if (discord.Count > 1)
                {
                    state = new(true, false, false, 0, 0, "multiple_references", "Discord 正在使用多个输出，请明确选择播放参考。", SessionId: sessionId); return null;
                }
                device = discord.Count == 1 ? enumerator.GetDevice(discord[0]) : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            else device = enumerator.GetDevice(selectedId);
            if (device.State != DeviceState.Active || device.DataFlow != DataFlow.Render || AudioDeviceService.IsVirtualCableEndpoint(device.FriendlyName))
            {
                state = new(true, false, false, 0, 0, "reference_is_virtual", "播放参考必须是已连接的物理扬声器或耳机，不能选择虚拟线路。", SessionId: sessionId);
                device.Dispose(); return null;
            }
            var controller = new LoopbackEchoCancellation(device, rate, channels, selectedId == null); device = null;
            state = controller.State(sessionId); return controller;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            device?.Dispose(); state = new(true, false, false, 0, 0, "library_missing", "回声消除组件不可用，当前音频保持原样；啸叫保护仍独立工作。", SessionId: sessionId); return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException or IOException)
        {
            device?.Dispose(); state = new(true, false, false, 0, 0, "reference_unavailable", "无法启动外放播放参考，当前音频保持原样。", SessionId: sessionId); return null;
        }
    }

    public EchoCancellationState State(int sessionId)
    {
        if (referenceStopped) return new(true, false, false, processor.LatencySamples, reference.Generation, failureCode,
            "播放参考已断开或改变，回声消除暂停；请重新选择设备并开始。", referenceName, sessionId);
        if (processor.Failed) return new(true, false, false, processor.LatencySamples, reference.Generation, "processing_failed",
            "回声处理组件返回错误，当前使用原音。", referenceName, sessionId);
        return new(true, true, processor.ReferenceAvailable, processor.LatencySamples, reference.Generation,
            processor.ReferenceAvailable ? "active" : "waiting_reference",
            processor.ReferenceAvailable ? "外放回声消除运行中；实际效果需试听确认。" : "已启用回声消除，等待播放参考。", referenceName, sessionId);
    }

    public bool HasStateChanged(EchoCancellationState previous)
    {
        var code = referenceStopped ? failureCode : processor.Failed ? "processing_failed" : processor.ReferenceAvailable ? "active" : "waiting_reference";
        return previous.Code != code || previous.ReferenceResets != reference.Generation;
    }

    public bool IsReferenceSelectionCurrent(IReadOnlyList<AudioDeviceInfo> outputs)
    {
        if (!outputs.Any(d => d.Id == referenceId && d.IsActive && !AudioDeviceService.IsVirtualCableEndpoint(d.Name))) return false;
        if (!automaticReference) return true;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var discord = PlaybackRoutingInspector.ActiveDiscordPhysicalOutputIds(enumerator, out var inspected);
            if (!inspected || discord.Count > 1) return false;
            if (discord.Count == 1) return discord[0] == referenceId;
            using var defaultOutput = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return AutomaticSelectionMatches(referenceId, defaultOutput.ID, discord, inspected);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { return false; }
    }

    internal static bool AutomaticSelectionMatches(string currentId, string? defaultId, IReadOnlyList<string> discordIds, bool inspected) =>
        inspected && discordIds.Count <= 1 && currentId == (discordIds.Count == 1 ? discordIds[0] : defaultId);

    public void Process(float[] samples, int count, long packetEndTimestamp)
    {
        var frames = count / channels;
        var observedFirst = (long)Math.Round((packetEndTimestamp - origin) * rate / (double)Stopwatch.Frequency) - frames;
        if (expectedMicrophoneFrame == long.MinValue || Math.Abs(observedFirst - expectedMicrophoneFrame) > rate / 20)
            expectedMicrophoneFrame = observedFirst;
        processor.Process(samples, count, expectedMicrophoneFrame);
        expectedMicrophoneFrame += frames;
    }

    private void OnReferenceData(object? sender, WaveInEventArgs e)
    {
        var end = Stopwatch.GetTimestamp();
        if (disposed || referenceStopped || e.BytesRecorded == 0) return;
        try
        {
            var count = e.BytesRecorded / decodeFormat.BlockAlign * sourceChannels;
            if (decoded.Length < count) decoded = new float[count];
            AudioEngine.Decode(e.Buffer.AsSpan(0, count * (decodeFormat.BitsPerSample / 8)), decoded.AsSpan(0, count), decodeFormat);
            var frames = count / sourceChannels;
            if (mono.Length < frames) mono = new float[frames];
            for (var f = 0; f < frames; f++)
            {
                double sum = 0; for (var c = 0; c < sourceChannels; c++) sum += decoded[f * sourceChannels + c];
                mono[f] = (float)(sum / sourceChannels);
            }
            var observedFirst = (long)Math.Round((end - origin) * rate / (double)Stopwatch.Frequency - frames * rate / (double)sourceRate);
            var discontinuity = expectedReferenceFrame != long.MinValue && Math.Abs(observedFirst - expectedReferenceFrame) > rate / 20;
            if (expectedReferenceFrame == long.MinValue || discontinuity) expectedReferenceFrame = observedFirst;
            if (discontinuity) resampler = new(sourceRate, rate);
            var needed = (int)Math.Ceiling(frames * rate / (double)sourceRate) + 4;
            if (converted.Length < needed) converted = new float[needed];
            var got = resampler.Convert(mono.AsSpan(0, frames), converted);
            reference.Push(expectedReferenceFrame, converted.AsSpan(0, got), discontinuity);
            expectedReferenceFrame += got;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            MarkReferenceUnavailable("reference_unavailable");
        }
    }

    private void OnReferenceStopped(object? sender, StoppedEventArgs e) { if (!disposed) MarkReferenceUnavailable("reference_unavailable"); }
    public void MarkReferenceUnavailable(string code)
    {
        if (referenceStopped) return;
        failureCode = code; referenceStopped = true; reference.Disable();
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        capture.DataAvailable -= OnReferenceData; capture.RecordingStopped -= OnReferenceStopped;
        try { capture.StopRecording(); }
        finally
        {
            try { capture.Dispose(); }
            finally { try { processor.Dispose(); } finally { device.Dispose(); } }
        }
    }
}
