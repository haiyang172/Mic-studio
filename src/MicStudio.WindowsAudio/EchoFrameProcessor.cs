using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MicStudio.WindowsAudio;

internal interface IEchoFrameAdapter : IDisposable
{
    bool Process(short[] microphone, short[] reference, short[] output);
    void Reset();
}

internal sealed class SpeexEchoAdapter : SafeHandleZeroOrMinusOneIsInvalid, IEchoFrameAdapter
{
    public SpeexEchoAdapter(int sampleRate, int frameSamples) : base(true)
    {
        SetHandle(Native.MS_EchoCreate(sampleRate, frameSamples, 200));
        if (IsInvalid) throw new NotSupportedException("The native echo canceller does not support this format.");
        // Resolve the interop stub and initialize native work buffers before any capture callback.
        try
        {
            var silence = new short[frameSamples];
            if (Native.MS_EchoProcess(this, silence, silence, new short[frameSamples]) != 0) throw new NotSupportedException("Native echo initialization failed.");
            Native.MS_EchoReset(this);
        }
        catch { Dispose(); throw; }
    }
    public bool Process(short[] microphone, short[] reference, short[] output) => Native.MS_EchoProcess(this, microphone, reference, output) == 0;
    public void Reset() => Native.MS_EchoReset(this);
    protected override bool ReleaseHandle() { Native.MS_EchoDestroy(handle); return true; }

    private static class Native
    {
        [DllImport("MicStudioEcho.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
        internal static extern IntPtr MS_EchoCreate(int sampleRate, int frameSamples, int tailMs);
        [DllImport("MicStudioEcho.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
        internal static extern int MS_EchoProcess(SpeexEchoAdapter handle, [In] short[] microphone, [In] short[] reference, [Out] short[] output);
        [DllImport("MicStudioEcho.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
        internal static extern void MS_EchoReset(SpeexEchoAdapter handle);
        [DllImport("MicStudioEcho.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
        internal static extern void MS_EchoDestroy(IntPtr handle);
    }
}

/// <summary>Adapts arbitrary interleaved packets to native 10 ms mono frames, one native filter per microphone channel.</summary>
internal sealed class EchoFrameProcessor : IDisposable
{
    private readonly int channels, frameSize, referenceDelayFrames;
    private readonly IEchoFrameAdapter[] adapters;
    private readonly short[][] microphone, processed;
    private readonly float[][] microphoneFloat, output;
    private readonly short[] reference;
    private readonly EchoReferenceBuffer history;
    private int position, previousGeneration;
    private long nextFrame, frameStart;
    private bool clockInitialized;
    public int LatencySamples => frameSize;
    public bool ReferenceAvailable { get; private set; }
    public bool Failed { get; private set; }

    public EchoFrameProcessor(int sampleRate, int channels, EchoReferenceBuffer history,
        Func<int, IEchoFrameAdapter>? factory = null, int referenceDelayFrames = 0)
    {
        if (sampleRate is < 8000 or > 384000 || channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.channels = channels; this.history = history; frameSize = sampleRate / 100;
        if (referenceDelayFrames < 0 || referenceDelayFrames > sampleRate / 10) throw new ArgumentOutOfRangeException(nameof(referenceDelayFrames));
        this.referenceDelayFrames = referenceDelayFrames;
        adapters = new IEchoFrameAdapter[channels];
        try { for (var c = 0; c < channels; c++) adapters[c] = factory?.Invoke(c) ?? new SpeexEchoAdapter(sampleRate, frameSize); }
        catch { foreach (var adapter in adapters) adapter?.Dispose(); throw; }
        microphone = Enumerable.Range(0, channels).Select(_ => new short[frameSize]).ToArray();
        processed = Enumerable.Range(0, channels).Select(_ => new short[frameSize]).ToArray();
        microphoneFloat = Enumerable.Range(0, channels).Select(_ => new float[frameSize]).ToArray();
        output = Enumerable.Range(0, channels).Select(_ => new float[frameSize]).ToArray();
        reference = new short[frameSize];
    }

    public void Process(float[] samples, int count, long packetFirstFrame)
    {
        if (count < 0 || count > samples.Length || count % channels != 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (!clockInitialized) { nextFrame = frameStart = packetFirstFrame; clockInitialized = true; }
        // A large capture discontinuity cannot use an old playback reference or old frame output.
        if (Math.Abs(packetFirstFrame - nextFrame) > frameSize * 5)
        {
            position = 0; nextFrame = frameStart = packetFirstFrame;
            foreach (var buffer in output) Array.Clear(buffer);
            foreach (var adapter in adapters) adapter.Reset();
        }
        for (var frame = 0; frame < count; frame += channels)
        {
            if (position == 0) frameStart = nextFrame;
            for (var c = 0; c < channels; c++)
            {
                var value = float.IsFinite(samples[frame + c]) ? samples[frame + c] : 0;
                microphoneFloat[c][position] = value;
                microphone[c][position] = EchoReferenceBuffer.ToPcm(value);
                samples[frame + c] = output[c][position];
            }
            nextFrame++; position++;
            if (position != frameSize) continue;
            ReferenceAvailable = history.Read(frameStart - referenceDelayFrames, reference);
            if (previousGeneration != history.Generation)
            {
                previousGeneration = history.Generation;
                foreach (var adapter in adapters) adapter.Reset();
            }
            for (var c = 0; c < channels; c++)
            {
                // Advance native filter history through reference gaps, but return
                // exact delayed input while the corresponding reference is absent.
                if (!Failed && !adapters[c].Process(microphone[c], reference, processed[c])) Failed = true;
            }
            // A failure on any channel falls back coherently on every channel.
            for (var c = 0; c < channels; c++)
            {
                for (var i = 0; i < frameSize; i++)
                    output[c][i] = Failed || !ReferenceAvailable ? microphoneFloat[c][i] : processed[c][i] / 32768f;
            }
            position = 0;
        }
    }

    public void Dispose() { foreach (var adapter in adapters) adapter.Dispose(); }
}
