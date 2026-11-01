using System.Buffers.Binary;
using System.Diagnostics;
using MicStudio.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicStudio.WindowsAudio;

/// <summary>
/// Explicitly started WASAPI shared capture. DSP runs once in the capture callback;
/// cable, monitor and file receive that same result. Input meters describe this endpoint's
/// shared stream, which may already include system effects, never a guaranteed raw stream.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _lifecycle = new();
    private readonly object _data = new();
    private readonly AudioDeviceService _devices;
    private readonly bool _ownsDevices;
    private WasapiCapture? _capture;
    private MMDevice? _inputDevice;
    private CaptureOptions? _options;
    private DspProcessor? _dsp;
    private FeedbackGuard? _feedbackGuard;
    private LoopbackEchoCancellation? _echoCancellation;
    private EchoCancellationState _echoStatus = new(false, false, false, 0, 0, "off", "回声消除未运行");
    private RenderTarget? _cable;
    private RenderTarget? _monitor;
    private WaveFormat? _format;
    private float[] _samples = [];
    private byte[] _floatBytes = [];
    private bool _running;
    private bool _disposed;
    private int _session;
    private long _clippedSamples;
    private long _lastMeterTicks;
    private double _meterInputPeakDb = -160, _meterOutputPeakDb = -160;
    private double _meterInputPower, _meterOutputPower;
    private long _meterSamples;
    private Stopwatch _elapsed = new();
    private WaveFileWriter? _writer;
    private string? _recordingPath;
    private long _recordedSamples;
    private long _recordingLimitSamples;
    private bool _recordSource;
    private WasapiOut? _playback;
    private WaveFileReader? _playbackReader;
    private MMDevice? _playbackDevice;

    public event EventHandler<AudioMeter>? MeterUpdated;
    public event EventHandler<AudioEngineStatus>? StatusChanged;
    public event EventHandler<RecordingResult>? RecordingStopped;
    public event EventHandler<FeedbackProtectionEvent>? FeedbackProtectionTriggered;
    public event EventHandler<EchoCancellationState>? EchoCancellationStatusChanged;
    public bool IsRunning { get { lock (_data) return _running; } }
    public int SessionId => Volatile.Read(ref _session);
    public bool IsRecording { get { lock (_data) return _writer != null; } }
    public bool IsPlaying { get { lock (_lifecycle) return _playback?.PlaybackState == PlaybackState.Playing; } }
    public string? LastRecordingPath { get; private set; }
    public bool LastRecordingCapturedBeforeLocalDsp { get; private set; }
    public int SampleRate { get { lock (_data) return _format?.SampleRate ?? 0; } }
    public int Channels { get { lock (_data) return _format?.Channels ?? 0; } }
    public long OutputBufferResets => Interlocked.Read(ref _outputBufferResets);
    public bool FeedbackProtectionMuted { get { lock (_data) return _feedbackGuard?.IsMuted == true; } }
    public EchoCancellationState EchoCancellationStatus { get { lock (_data) return _echoStatus; } }
    private long _outputBufferResets;

    public AudioEngine(AudioDeviceService? devices = null)
    {
        _devices = devices ?? new();
        _ownsDevices = devices == null;
        _devices.DevicesChanged += OnDevicesChanged;
    }

    public void Start(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.InputDeviceId)) throw new ArgumentException("请选择明确的麦克风，程序不会自动改用其他设备。");
        if (options.MonitorOutputDeviceId != null && !options.MonitorConfirmedHeadphones)
            throw new InvalidOperationException("监听前请选择物理耳机，并确认已经佩戴耳机。");
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();
            StopPlaybackCore();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _inputDevice = GetActiveDevice(enumerator, options.InputDeviceId, DataFlow.Capture);
                if (AudioDeviceService.IsCableOutput(_inputDevice.FriendlyName) && options.CableOutputDeviceId != null)
                    throw new InvalidOperationException("不能将 CABLE Output 再发送回 CABLE Input，这会形成反馈循环。请选择物理麦克风。");
                _capture = new WasapiCapture(_inputDevice, false, 20) { ShareMode = AudioClientShareMode.Shared };
                _format = _capture.WaveFormat;
                ValidateFormat(_format);
                _dsp = options.ProcessLocally ? new DspProcessor(_format.SampleRate, _format.Channels, options.Settings) : null;
                _feedbackGuard = options.FeedbackProtectionEnabled ? new FeedbackGuard(_format.SampleRate, _format.Channels) : null;
                _options = options;
                var session = ++_session;
                _echoStatus = new(options.EchoCancellationEnabled, false, false, 0, 0, "off", "回声消除未运行", SessionId: session);
                if (options.EchoCancellationEnabled)
                    _echoCancellation = LoopbackEchoCancellation.TryCreate(_format.SampleRate, _format.Channels, options.EchoReferenceOutputDeviceId, session, out _echoStatus);
                if (options.CableOutputDeviceId != null)
                {
                    var outputDevice = GetActiveDevice(enumerator, options.CableOutputDeviceId, DataFlow.Render);
                    if (!AudioDeviceService.IsCableInput(outputDevice.FriendlyName))
                    { outputDevice.Dispose(); throw new InvalidOperationException("线路发送必须选择 CABLE Input 播放端点。"); }
                    _cable = new RenderTarget(outputDevice, _format.SampleRate, _format.Channels, false,
                        ex => OnRenderError(ex, session), () => Interlocked.Increment(ref _outputBufferResets));
                }
                if (options.MonitorOutputDeviceId != null)
                {
                    var outputDevice = GetActiveDevice(enumerator, options.MonitorOutputDeviceId, DataFlow.Render);
                    if (AudioDeviceService.IsVirtualCableEndpoint(outputDevice.FriendlyName))
                    { outputDevice.Dispose(); throw new InvalidOperationException("耳机监听不能选择虚拟 CABLE Input。"); }
                    _monitor = new RenderTarget(outputDevice, _format.SampleRate, _format.Channels, true,
                        ex => OnRenderError(ex, session), () => Interlocked.Increment(ref _outputBufferResets));
                }
                _clippedSamples = 0;
                Interlocked.Exchange(ref _outputBufferResets, 0);
                _lastMeterTicks = 0;
                _meterInputPeakDb = _meterOutputPeakDb = -160;
                _meterInputPower = _meterOutputPower = 0;
                _meterSamples = 0;
                _elapsed = Stopwatch.StartNew();
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnCaptureStopped;
                lock (_data) _running = true;
                _capture.StartRecording();
                _cable?.Play();
                _monitor?.Play();
                EchoCancellationStatusChanged?.Invoke(this, _echoStatus);
                RaiseStatus(new(true, options.CableOutputDeviceId != null
                    ? "线路已启动 · 目标应用输入选择 CABLE Output · 仍需接收验证"
                    : "采集已启动 · 仅本地测试，未证明 APO 或其他应用已生效"));
            }
            catch
            {
                StopCore();
                throw;
            }
        }
    }

    private static MMDevice GetActiveDevice(MMDeviceEnumerator enumerator, string id, DataFlow flow)
    {
        var device = enumerator.GetDevice(id);
        if (device.State == DeviceState.Active && device.DataFlow == flow) return device;
        device.Dispose();
        throw new InvalidOperationException("所选设备已断开、停用或方向不匹配。保留原选择，请重新连接后再启动。");
    }

    private static void ValidateFormat(WaveFormat format)
    {
        if (format.SampleRate < 8000 || format.SampleRate > 384000 || format.Channels < 1 || format.Channels > 8)
            throw new NotSupportedException("此设备的采样率或声道数不受支持。");
        if (!((format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            || (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 8 or 16 or 24 or 32)))
            throw new NotSupportedException($"此设备的采样格式不受支持：{format.Encoding} / {format.BitsPerSample} bit。");
    }

    public void UpdateSettings(AudioSettings settings)
    {
        TryUpdateSettings(settings, SessionId);
    }

    /// <summary>
    /// Prepares a live update outside the capture lock and applies it only to the
    /// selected running DSP session. A stopped/replaced session is never restarted.
    /// The caller should serialize UI revisions and treat false as not applied.
    /// </summary>
    public bool TryUpdateSettings(AudioSettings settings, int expectedSessionId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DspProcessor processor;
        lock (_data)
        {
            if (!_running || _session != expectedSessionId || _dsp is null) return false;
            processor = _dsp;
        }
        // Validation/immutable state preparation can allocate. Do not make the
        // device callback wait for it while it is holding the shared data lock.
        processor.UpdateSettings(settings);
        lock (_data)
            return _running && _session == expectedSessionId && ReferenceEquals(_dsp, processor);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        AudioMeter? meter = null;
        RecordingResult? completed = null;
        FeedbackProtectionEvent? feedback = null;
        EchoCancellationState? echoChanged = null;
        var packetEndTimestamp = Stopwatch.GetTimestamp();
        int session;
        try
        {
            lock (_data)
            {
                if (!_running || sender != _capture || _format == null || e.BytesRecorded == 0) return;
                session = _session;
                var bytesPerSample = _format.BitsPerSample / 8;
                var count = (e.BytesRecorded / _format.BlockAlign) * _format.Channels;
                if (_samples.Length < count) _samples = new float[count];
                Decode(e.Buffer.AsSpan(0, count * bytesPerSample), _samples.AsSpan(0, count), _format);
                var input = Levels(_samples, count);
                if (_writer != null && _recordSource) completed = WriteRecordingLocked(count);
                if (_echoCancellation != null)
                {
                    _echoCancellation.Process(_samples, count, packetEndTimestamp);
                    if (_echoCancellation.HasStateChanged(_echoStatus))
                        echoChanged = _echoStatus = _echoCancellation.State(_session);
                }
                _dsp?.Process(_samples, 0, count);
                // Independent of effect bypass, before cable/monitor/wet-recording fanout.
                if (_feedbackGuard?.Process(_samples, count) == true)
                {
                    feedback = new(_session, _feedbackGuard.FrequencyHz, _feedbackGuard.TriggerPeakDb);
                    _cable?.ClearPending(); _monitor?.ClearPending();
                }
                var output = Levels(_samples, count);
                _meterInputPeakDb = Math.Max(_meterInputPeakDb, input.Peak);
                _meterOutputPeakDb = Math.Max(_meterOutputPeakDb, output.Peak);
                _meterInputPower += Math.Pow(10, input.Rms / 10) * count;
                _meterOutputPower += Math.Pow(10, output.Rms / 10) * count;
                _meterSamples += count;
                for (var i = 0; i < count; i++)
                {
                    if (!float.IsFinite(_samples[i])) _samples[i] = 0;
                    if (Math.Abs(_samples[i]) >= 1) _clippedSamples++;
                }
                if (_floatBytes.Length < count * 4) _floatBytes = new byte[count * 4];
                Buffer.BlockCopy(_samples, 0, _floatBytes, 0, count * 4);
                _cable?.Add(_floatBytes, count * 4);
                _monitor?.Add(_floatBytes, count * 4);
                if (_writer != null && !_recordSource) completed = WriteRecordingLocked(count);
                var now = Stopwatch.GetTimestamp();
                if (now - _lastMeterTicks >= Stopwatch.Frequency / 12)
                {
                    _lastMeterTicks = now;
                    meter = new(_meterInputPeakDb, Db(Math.Sqrt(_meterInputPower / Math.Max(1, _meterSamples))),
                        _meterOutputPeakDb, Db(Math.Sqrt(_meterOutputPower / Math.Max(1, _meterSamples))), _format.SampleRate,
                        _format.Channels, _clippedSamples, _elapsed.Elapsed, _session);
                    _meterInputPeakDb = _meterOutputPeakDb = -160;
                    _meterInputPower = _meterOutputPower = 0;
                    _meterSamples = 0;
                }
            }
            if (completed != null) RecordingStopped?.Invoke(this, completed);
            if (feedback != null) FeedbackProtectionTriggered?.Invoke(this, feedback);
            if (echoChanged != null) EchoCancellationStatusChanged?.Invoke(this, echoChanged);
            if (meter != null) MeterUpdated?.Invoke(this, meter);
        }
        catch (Exception ex)
        {
            session = _session;
            QueueFailure(ex, session, "音频处理已停止");
        }
    }

    internal static void Decode(ReadOnlySpan<byte> bytes, Span<float> output, WaveFormat format)
    {
        var width = format.BitsPerSample / 8;
        for (var i = 0; i < output.Length; i++)
        {
            var sample = bytes.Slice(i * width, width);
            float value;
            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
                value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample));
            else value = width switch
            {
                1 => (sample[0] - 128) / 128f,
                2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                3 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608f,
                4 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
                _ => 0
            };
            output[i] = float.IsFinite(value) ? value : 0;
        }
    }

    internal static (double Peak, double Rms) Levels(float[] buffer, int count)
    {
        double peak = 0, power = 0;
        for (var i = 0; i < count; i++)
        {
            var value = float.IsFinite(buffer[i]) ? buffer[i] : 0;
            peak = Math.Max(peak, Math.Abs(value));
            power += (double)value * value;
        }
        return (Db(peak), Db(Math.Sqrt(power / Math.Max(1, count))));
    }
    private static double Db(double value) => value <= 1e-8 ? -160 : 20 * Math.Log10(value);

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        int session;
        lock (_data) { if (!_running || sender != _capture) return; session = _session; }
        QueueFailure(e.Exception ?? new IOException("设备已停止采集。"), session, "采集已停止");
    }

    private void OnRenderError(Exception? exception, int session)
    {
        if (exception != null) QueueFailure(exception, session, "输出设备故障，线路已停止");
    }

    private void QueueFailure(Exception exception, int session, string message) => Task.Run(() =>
    {
        lock (_lifecycle)
        {
            if (_disposed || session != _session || !IsRunning) return;
            StopCore();
            RaiseStatus(new(false, $"{message}：{exception.Message}", $"0x{exception.HResult:X8}"));
        }
    });

    private void OnDevicesChanged(object? sender, EventArgs e) => Task.Run(() =>
    {
        CaptureOptions? options;
        LoopbackEchoCancellation? echoReference;
        int session;
        lock (_data) { if (!_running) return; options = _options; session = _session; echoReference = _echoCancellation; }
        if (options == null) return;
        try
        {
            var inputs = _devices.EnumerateInputs(false);
            var outputs = _devices.EnumerateOutputs(false);
            var echoReferenceChanged = echoReference != null && !echoReference.IsReferenceSelectionCurrent(outputs);
            EchoCancellationState? echoChanged = null;
            lock (_data)
                if (_running && _session == session && _echoCancellation != null && ReferenceEquals(_echoCancellation, echoReference) && echoReferenceChanged)
                {
                    _echoCancellation.MarkReferenceUnavailable("reference_changed");
                    echoChanged = _echoStatus = _echoCancellation.State(_session);
                }
            if (echoChanged != null) EchoCancellationStatusChanged?.Invoke(this, echoChanged);
            if (!inputs.Any(x => x.Id == options.InputDeviceId)
                || (options.CableOutputDeviceId != null && !outputs.Any(x => x.Id == options.CableOutputDeviceId))
                || (options.MonitorOutputDeviceId != null && !outputs.Any(x => x.Id == options.MonitorOutputDeviceId)))
                QueueFailure(new IOException("所选设备断开，已停止全部本地音频；保留选择，不自动切换设备。"), session, "设备断开");
        }
        catch (Exception ex) { QueueFailure(ex, session, "设备检测失败"); }
    });

    public void StartRecording(string path, int maxSeconds = 30, bool recordSource = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(maxSeconds), "短录音限制为 1 至 120 秒。");
        lock (_data)
        {
            if (!_running || _format == null) throw new InvalidOperationException("请先主动开始电平测试或线路处理，再开始录音。");
            if (_writer != null) throw new InvalidOperationException("录音正在进行。");
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            try { _writer = new WaveFileWriter(stream, WaveFormat.CreateIeeeFloatWaveFormat(_format.SampleRate, _format.Channels)); }
            catch { stream.Dispose(); throw; }
            _recordingPath = fullPath;
            _recordedSamples = 0;
            _recordSource = recordSource;
            _recordingLimitSamples = (long)_format.SampleRate * _format.Channels * maxSeconds;
        }
    }

    public void StopRecording()
    {
        RecordingResult? completed;
        lock (_data) completed = FinishRecordingLocked("用户停止录音");
        if (completed != null) RecordingStopped?.Invoke(this, completed);
    }

    private RecordingResult? WriteRecordingLocked(int count)
    {
        var writeCount = (int)Math.Min(count, _recordingLimitSamples - _recordedSamples);
        writeCount -= writeCount % _format!.Channels;
        if (writeCount > 0) { _writer!.WriteSamples(_samples, 0, writeCount); _recordedSamples += writeCount; }
        return _recordedSamples >= _recordingLimitSamples ? FinishRecordingLocked("已达到录音时长上限") : null;
    }

    private RecordingResult? FinishRecordingLocked(string reason)
    {
        if (_writer == null) return null;
        var writer = _writer;
        _writer = null;
        var path = _recordingPath!;
        _recordingPath = null;
        var duration = TimeSpan.FromSeconds((double)_recordedSamples / writer.WaveFormat.SampleRate / writer.WaveFormat.Channels);
        writer.Dispose();
        LastRecordingPath = path;
        LastRecordingCapturedBeforeLocalDsp = _recordSource || _dsp == null;
        return new(path, duration, reason, LastRecordingCapturedBeforeLocalDsp);
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            StopCore();
            RaiseStatus(new(false, "本地采集、录音、监听与线路已停止；系统 APO 配置需单独管理", SessionId: _session));
        }
    }

    private void StopCore()
    {
        WasapiCapture? capture;
        RenderTarget? cable, monitor;
        LoopbackEchoCancellation? echoCancellation;
        MMDevice? input;
        RecordingResult? completed;
        lock (_data)
        {
            _running = false;
            ++_session;
            capture = _capture; _capture = null;
            cable = _cable; _cable = null;
            monitor = _monitor; _monitor = null;
            echoCancellation = _echoCancellation; _echoCancellation = null;
            _echoStatus = new(false, false, false, 0, 0, "off", "回声消除未运行", SessionId: _session);
            input = _inputDevice; _inputDevice = null;
            try { completed = FinishRecordingLocked("音频会话停止"); }
            catch (IOException ex)
            {
                completed = null;
                var failedSession = _session;
                Task.Run(() => RaiseStatus(new(false, $"录音文件无法完整写入：{ex.Message}", $"0x{ex.HResult:X8}", failedSession)));
            }
            _dsp = null;
            _feedbackGuard = null;
            _options = null;
            _elapsed.Stop();
        }
        // Capture.Dispose joins the callback thread; never hold _data while joining it.
        if (capture != null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnCaptureStopped;
            try { capture.StopRecording(); } catch (Exception ex) { ReportCleanupError(ex); }
            try { capture.Dispose(); } catch (Exception ex) { ReportCleanupError(ex); }
        }
        try { cable?.Dispose(); } catch (Exception ex) { ReportCleanupError(ex); }
        try { monitor?.Dispose(); } catch (Exception ex) { ReportCleanupError(ex); }
        try { echoCancellation?.Dispose(); } catch (Exception ex) { ReportCleanupError(ex); }
        try { input?.Dispose(); } catch (Exception ex) { ReportCleanupError(ex); }
        if (completed != null) RecordingStopped?.Invoke(this, completed);
    }

    private void ReportCleanupError(Exception exception)
    {
        var failedSession = SessionId;
        Task.Run(() => RaiseStatus(new(false, $"音频资源释放时遇到错误：{exception.Message}", $"0x{exception.HResult:X8}", failedSession)));
    }

    public void PlayRecording(string path, string outputDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDeviceId);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) throw new InvalidOperationException("请先停止麦克风测试或线路，再回放录音，以免回放被重新采集。");
            StopPlaybackCore();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _playbackDevice = GetActiveDevice(enumerator, outputDeviceId, DataFlow.Render);
                if (AudioDeviceService.IsVirtualCableEndpoint(_playbackDevice.FriendlyName))
                    throw new InvalidOperationException("录音试听请选择物理耳机或扬声器，不发送到通话线路。");
                _playbackReader = new WaveFileReader(path);
                ISampleProvider source = _playbackReader.ToSampleProvider();
                var mix = _playbackDevice.AudioClient.MixFormat;
                if (source.WaveFormat.Channels != mix.Channels) source = new ChannelAdapter(source, mix.Channels);
                if (source.WaveFormat.SampleRate != mix.SampleRate) source = new WdlResamplingSampleProvider(source, mix.SampleRate);
                _playback = new WasapiOut(_playbackDevice, AudioClientShareMode.Shared, true, 80);
                _playback.PlaybackStopped += OnPlaybackStopped;
                _playback.Init(source.ToWaveProvider());
                _playback.Play();
            }
            catch { StopPlaybackCore(); throw; }
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => Task.Run(() =>
    {
        lock (_lifecycle)
        {
            if (sender != _playback) return;
            StopPlaybackCore();
            RaiseStatus(new(IsRunning, e.Exception == null ? "录音回放结束" : $"录音回放失败：{e.Exception.Message}",
                e.Exception == null ? null : $"0x{e.Exception.HResult:X8}"));
        }
    });
    public void StopPlayback() { lock (_lifecycle) StopPlaybackCore(); }
    private void StopPlaybackCore()
    {
        if (_playback != null)
        {
            _playback.PlaybackStopped -= OnPlaybackStopped;
            try { _playback.Stop(); } finally { _playback.Dispose(); _playback = null; }
        }
        _playbackReader?.Dispose(); _playbackReader = null;
        _playbackDevice?.Dispose(); _playbackDevice = null;
    }

    private void RaiseStatus(AudioEngineStatus status) => StatusChanged?.Invoke(this,
        status.SessionId < 0 ? status with { SessionId = SessionId } : status);
    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _devices.DevicesChanged -= OnDevicesChanged;
            StopCore();
            StopPlaybackCore();
            _disposed = true;
            if (_ownsDevices) _devices.Dispose();
        }
    }

    private sealed class RenderTarget : IDisposable
    {
        private readonly MMDevice _device;
        private readonly WasapiOut _player;
        private readonly BoundedRenderBuffer _buffer;
        private bool _disposed;
        public RenderTarget(MMDevice device, int sampleRate, int channels, bool monitor, Action<Exception?> stopped, Action bufferReset)
        {
            _device = device;
            _buffer = new BoundedRenderBuffer(sampleRate, channels, bufferReset);
            try
            {
                var mix = device.AudioClient.MixFormat;
                ISampleProvider source = _buffer.Provider.ToSampleProvider();
                if (channels != mix.Channels) source = new ChannelAdapter(source, mix.Channels);
                if (sampleRate != mix.SampleRate) source = new WdlResamplingSampleProvider(source, mix.SampleRate);
                if (monitor) source = new SafeMonitor(source);
                _player = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
                _player.PlaybackStopped += (_, e) => { if (!_disposed) stopped(e.Exception); };
                _player.Init(source.ToWaveProvider());
            }
            catch { _player?.Dispose(); device.Dispose(); throw; }
        }
        public void Play() => _player.Play();
        public void Add(byte[] bytes, int count) => _buffer.Add(bytes, count);
        public void ClearPending() => _buffer.Clear();
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _player.Stop(); } finally { _player.Dispose(); _device.Dispose(); }
        }
    }

    internal sealed class SafeMonitor(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            // Independent headphone monitor has a fixed -12 dB safety trim and sample clamp.
            for (var i = offset; i < offset + read; i++) buffer[i] = Math.Clamp(buffer[i] * 0.25118864f, -0.95f, 0.95f);
            return read;
        }
    }

    internal sealed class ChannelAdapter(ISampleProvider source, int channels) : ISampleProvider
    {
        private float[] _input = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / channels;
            var sourceChannels = source.WaveFormat.Channels;
            var wanted = frames * sourceChannels;
            if (_input.Length < wanted) _input = new float[wanted];
            var gotFrames = source.Read(_input, 0, wanted) / sourceChannels;
            for (var f = 0; f < gotFrames; f++)
            {
                if (channels == 1)
                {
                    var sum = 0f;
                    for (var c = 0; c < sourceChannels; c++) sum += _input[f * sourceChannels + c];
                    buffer[offset + f] = sum / sourceChannels;
                }
                else for (var c = 0; c < channels; c++)
                    buffer[offset + f * channels + c] = sourceChannels == 1 ? _input[f] : c < sourceChannels ? _input[f * sourceChannels + c] : 0;
            }
            return gotFrames * channels;
        }
    }
}
