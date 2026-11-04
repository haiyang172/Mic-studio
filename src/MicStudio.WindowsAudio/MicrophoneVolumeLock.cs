using NAudio.CoreAudioApi;

namespace MicStudio.WindowsAudio;

public sealed record MicrophoneVolumeLockState(bool Enabled, bool IsActive, bool Paused, string Code,
    double? Target, string? EndpointId, int RestoreAttempts, long Generation);

/// <summary>
/// Opt-in coordination for one explicitly selected capture endpoint. Construction performs no endpoint access.
/// The service never changes mute, Windows defaults, or microphone capture. It is not an exclusive Windows lock.
/// </summary>
public sealed class MicrophoneVolumeLock : IDisposable
{
    public const int PollIntervalMs = 400;
    private readonly object gate = new();
    private readonly Func<string, double> read;
    private readonly Action<string, double> write;
    private readonly Func<long> clock;
    private readonly Timer? timer;
    private readonly MicrophoneVolumeLockPolicy policy = new();
    private MicrophoneVolumeLockState state = new(false, false, false, "off", null, null, 0, 0);
    private bool disposed;
    private long generation;
    private int polling;
    public event EventHandler<MicrophoneVolumeLockState>? StatusChanged;
    public MicrophoneVolumeLockState State { get { lock (gate) return state; } }

    public MicrophoneVolumeLock() : this(ReadCaptureVolume, WriteCaptureVolume) { }

    /// <summary>Injectable endpoint access permits deterministic synthetic verification without opening Windows devices.</summary>
    public MicrophoneVolumeLock(Func<string, double> read, Action<string, double> write,
        Func<long>? clock = null, bool automaticPolling = true)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.write = write ?? throw new ArgumentNullException(nameof(write));
        this.clock = clock ?? (() => Environment.TickCount64);
        if (automaticPolling) timer = new(_ => PollNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Reads and adopts the endpoint's current, possibly quantized level. Enabling itself performs no write.</summary>
    public MicrophoneVolumeLockState Enable(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        if (endpointId.Length > 2048 || endpointId.Any(char.IsControl)) throw new ArgumentException("An explicit endpoint ID is required.", nameof(endpointId));
        long token;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token = ++generation; timer?.Change(Timeout.Infinite, Timeout.Infinite); policy.Disable();
            state = new(true, false, false, "starting", null, endpointId, 0, token);
        }
        double current = 0; Exception? failure = null;
        try { current = read(endpointId); }
        catch (Exception error) { failure = error; }
        MicrophoneVolumeLockState next;
        lock (gate)
        {
            if (disposed || generation != token) return state;
            if (failure == null)
            {
                try { policy.Enable(current, clock()); state = Snapshot(endpointId); timer?.Change(PollIntervalMs, PollIntervalMs); }
                catch (Exception) { state = new(true, false, true, "invalid_volume", null, endpointId, 0, token); }
            }
            else state = new(true, false, true, FailureCode(failure), null, endpointId, 0, token);
            next = state;
        }
        Notify(next); return next;
    }

    /// <summary>Updates the intended level before the host performs its own explicit slider write. This method does not write.</summary>
    public bool SetTarget(double scalar, string? expectedEndpointId = null)
    {
        if (!double.IsFinite(scalar) || scalar is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(scalar));
        MicrophoneVolumeLockState next;
        lock (gate)
        {
            if (disposed || !state.Enabled || (expectedEndpointId != null && state.EndpointId != expectedEndpointId)) return false;
            policy.SetTarget(scalar); state = state with { Target = scalar }; next = state;
        }
        Notify(next); return true;
    }

    /// <summary>After the host's own slider write, adopts the actual rounded device value; it never resumes a paused conflict.</summary>
    public bool AdoptCurrentTarget(string? expectedEndpointId = null)
    {
        string endpoint; long token;
        lock (gate)
        {
            if (disposed || !state.Enabled || state.EndpointId == null || (expectedEndpointId != null && state.EndpointId != expectedEndpointId)) return false;
            endpoint = state.EndpointId; token = generation;
        }
        double current;
        try { current = read(endpoint); }
        catch (Exception error) { PauseIfCurrent(token, error); return false; }
        MicrophoneVolumeLockState next;
        lock (gate)
        {
            if (disposed || generation != token) return false;
            if (!double.IsFinite(current) || current is < 0 or > 1) { PauseLocked("invalid_volume"); next = state; }
            else { policy.SetTarget(current); state = state with { Target = current }; next = state; }
        }
        Notify(next); return !next.Paused;
    }

    /// <summary>One bounded policy observation; disabled/stale/disposed instances cannot write. Timer callbacks never throw.</summary>
    public void PollNow()
    {
        if (Interlocked.Exchange(ref polling, 1) != 0) return;
        try
        {
            string endpoint; long token;
            lock (gate)
            {
                if (disposed || !state.IsActive || state.EndpointId == null) return;
                endpoint = state.EndpointId; token = generation;
            }
            double current;
            try { current = read(endpoint); }
            catch (Exception error) { PauseIfCurrent(token, error); return; }
            MicrophoneVolumeLockState? changed = null;
            lock (gate)
            {
                if (disposed || generation != token || !state.IsActive) return;
                try
                {
                    var decision = policy.Observe(current, clock());
                    if (decision == VolumeLockDecision.Restore) write(endpoint, policy.Target!.Value);
                    if (disposed || generation != token || !state.IsActive) return;
                    if (decision == VolumeLockDecision.Pause) timer?.Change(Timeout.Infinite, Timeout.Infinite);
                    var next = Snapshot(endpoint);
                    if (next != state) { state = next; changed = next; }
                }
                catch (Exception error) { PauseLocked(FailureCode(error)); changed = state; }
            }
            if (changed != null) Notify(changed);
        }
        finally { Volatile.Write(ref polling, 0); }
    }

    public void Disable()
    {
        MicrophoneVolumeLockState next;
        lock (gate)
        {
            if (disposed) return;
            ++generation; timer?.Change(Timeout.Infinite, Timeout.Infinite); policy.Disable();
            next = state = new(false, false, false, "off", null, null, 0, generation);
        }
        Notify(next);
    }

    private MicrophoneVolumeLockState Snapshot(string endpoint) => new(true, policy.Enabled && !policy.Paused,
        policy.Paused, policy.Code, policy.Target, endpoint, policy.RestoreAttempts, generation);
    private void PauseLocked(string code)
    {
        policy.Pause(code); timer?.Change(Timeout.Infinite, Timeout.Infinite);
        state = state with { IsActive = false, Paused = true, Code = code, RestoreAttempts = policy.RestoreAttempts };
    }
    private void PauseIfCurrent(long token, Exception error)
    {
        MicrophoneVolumeLockState next;
        lock (gate) { if (disposed || generation != token) return; PauseLocked(FailureCode(error)); next = state; }
        Notify(next);
    }
    private static string FailureCode(Exception error) => error is System.Runtime.InteropServices.COMException or InvalidOperationException
        ? "endpoint_unavailable" : "io_error";
    private void Notify(MicrophoneVolumeLockState next)
    {
        try { StatusChanged?.Invoke(this, next); } catch { /* Never throw through a timer or endpoint-control callback. */ }
    }
    private static double ReadCaptureVolume(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(id);
        RequireActiveCapture(device);
        return device.AudioEndpointVolume.MasterVolumeLevelScalar;
    }
    private static void WriteCaptureVolume(string id, double scalar)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(id);
        RequireActiveCapture(device);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)scalar;
    }
    private static void RequireActiveCapture(MMDevice device)
    {
        if (device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active)
            throw new InvalidOperationException("The selected capture endpoint is unavailable.");
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; ++generation; policy.Disable(); timer?.Dispose();
            state = new(false, false, false, "off", null, null, 0, generation);
        }
    }
}
