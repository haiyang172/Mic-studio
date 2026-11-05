namespace MicStudio.WindowsAudio;

/// <summary>The exact endpoints and capture format of one explicitly started local session.</summary>
public sealed record RecoveryRoute(string InputId, string? CableSendId, string? CableReceiveId,
    string? MonitorId, int SampleRate, int Channels)
{
    // Host-supplied rates/channels/formats for send, receive and monitor endpoints.
    // Equality includes this value, so their format changes cannot reuse old consent.
    public string? EndpointFormats { get; init; }
}

/// <summary>A single-use reservation. Hosts must recheck readiness before opening any devices.</summary>
public sealed record RecoveryAttempt(long Generation, RecoveryRoute Route);

public sealed record DeviceRecoveryState(bool Enabled, bool HasRunningIntent, bool Pending,
    bool AttemptInProgress, bool Paused, string Code, RecoveryRoute? Route, long Generation)
{
    public int AutomaticAttemptsInWindow { get; init; }
}

/// <summary>
/// Pure recovery authorization policy; never enumerates, opens or substitutes devices.
/// RememberRunning is called only after a successful explicit start, or CompleteAttempt
/// after the reserved recovery succeeds. Persisting Enabled alone cannot authorize capture.
/// </summary>
public sealed class DeviceRecoveryPolicy
{
    public const long StableReadyMilliseconds = 1000;
    public const long BurstWindowMilliseconds = 60000;
    public const int MaximumAutomaticAttemptsPerWindow = 3;
    private readonly object gate = new();
    private readonly Func<long> clock;
    private bool enabled, runningIntent, pending, paused;
    private RecoveryRoute? route;
    private RecoveryAttempt? activeAttempt;
    private long generation, lastObserved;
    private long? readySince;
    private readonly Queue<long> automaticAttempts = new(MaximumAutomaticAttemptsPerWindow);
    private string code = "off";

    public DeviceRecoveryPolicy(Func<long>? clock = null) => this.clock = clock ?? (() => Environment.TickCount64);

    public DeviceRecoveryState State
    {
        get { lock (gate) return new(enabled, runningIntent, pending, activeAttempt != null, paused, code, route, generation) { AutomaticAttemptsInWindow = automaticAttempts.Count }; }
    }

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (enabled == value) return;
            enabled = value;
            Clear(value ? "idle" : "off");
        }
    }

    /// <summary>Records a successful start, not a request to start. An explicit new start clears a failed recovery.</summary>
    public bool RememberRunning(RecoveryRoute runningRoute)
    {
        ValidateRoute(runningRoute);
        lock (gate)
        {
            if (!enabled) return false;
            Clear("armed");
            if (!ObserveClock(out _)) return false;
            route = runningRoute; runningIntent = true;
            return true;
        }
    }

    /// <summary>
    /// A host-confirmed absence on the remembered route is required. Old callbacks for
    /// another route cannot arm or cancel a newer session. Duplicate losses share one outage.
    /// </summary>
    public bool ObserveUnavailable(RecoveryRoute unavailableRoute)
    {
        ArgumentNullException.ThrowIfNull(unavailableRoute);
        lock (gate)
        {
            if (!enabled || !runningIntent || paused || activeAttempt != null || route != unavailableRoute) return false;
            if (!ObserveClock(out _)) return false;
            pending = true; readySince = null; code = "waiting_device";
            return true;
        }
    }

    /// <summary>
    /// Call periodically with exact endpoint readiness. While absent, pass the remembered
    /// route and false; once present, pass its actual current format. Any identity/format
    /// mismatch cancels authorization. Readiness must remain true for one full second.
    /// </summary>
    public bool TryTakeRecovery(RecoveryRoute currentRoute, bool exactEndpointsReady, out RecoveryAttempt? attempt)
    {
        ArgumentNullException.ThrowIfNull(currentRoute);
        lock (gate)
        {
            attempt = null;
            if (!enabled || !runningIntent || !pending || paused || activeAttempt != null || route == null) return false;
            if (!ObserveClock(out var now)) return false;
            if (route != currentRoute) { Clear("route_changed"); return false; }
            if (!exactEndpointsReady) { readySince = null; code = "waiting_device"; return false; }
            if (readySince == null) { readySince = now; code = "stabilizing"; return false; }
            if (now - readySince.Value < StableReadyMilliseconds) return false;
            while (automaticAttempts.TryPeek(out var previous) && now - previous >= BurstWindowMilliseconds)
                automaticAttempts.Dequeue();
            if (automaticAttempts.Count >= MaximumAutomaticAttemptsPerWindow)
            {
                ++generation; runningIntent = pending = false; paused = true; readySince = null; code = "flapping";
                return false;
            }
            automaticAttempts.Enqueue(now);
            activeAttempt = attempt = new(++generation, route);
            readySince = null; pending = false; code = "recovering";
            return true;
        }
    }

    /// <summary>Recheck after queued work and immediately before opening devices; cancellation revokes the reservation.</summary>
    public bool IsAttemptCurrent(RecoveryAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (gate)
            return enabled && activeAttempt != null && activeAttempt == attempt && generation == attempt.Generation && ObserveClock(out _);
    }

    /// <summary>A failed attempt pauses until a new successful explicit start; there is no retry loop.</summary>
    public bool CompleteAttempt(RecoveryAttempt attempt, bool success)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (gate)
        {
            if (!enabled || activeAttempt == null || activeAttempt != attempt || generation != attempt.Generation) return false;
            if (!ObserveClock(out _)) return false;
            activeAttempt = null; readySince = null; pending = false; ++generation;
            runningIntent = success; paused = !success;
            code = success ? "recovered" : "recovery_failed";
            return true;
        }
    }

    /// <summary>Explicit stop, route changes, lock/suspend and shutdown revoke every outstanding reservation.</summary>
    public void Cancel(string reason = "user_stopped")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate) Clear(enabled ? reason : "off");
    }

    public void Disarm(string reason = "stopped") => Cancel(reason);

    private void Clear(string nextCode)
    {
        ++generation; runningIntent = pending = paused = false;
        route = null; activeAttempt = null; readySince = null; code = nextCode;
        automaticAttempts.Clear();
    }

    private bool ObserveClock(out long now)
    {
        now = clock();
        if (now < 0 || now < lastObserved) { Clear("clock_changed"); lastObserved = Math.Max(0, now); return false; }
        lastObserved = now;
        return true;
    }

    private static void ValidateRoute(RecoveryRoute value)
    {
        ArgumentNullException.ThrowIfNull(value);
        static void Endpoint(string? id, bool required)
        {
            if (id == null && !required) return;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 2048 || id.Any(char.IsControl))
                throw new ArgumentException("Recovery requires explicit endpoint IDs.");
        }
        Endpoint(value.InputId, true); Endpoint(value.CableSendId, false);
        Endpoint(value.CableReceiveId, false); Endpoint(value.MonitorId, false);
        if ((value.CableSendId == null) != (value.CableReceiveId == null))
            throw new ArgumentException("Recovery requires both cable endpoints or neither.");
        if (value.SampleRate is < 8000 or > 384000 || value.Channels is < 1 or > 8)
            throw new ArgumentException("Recovery requires the successful capture format.");
        if (value.EndpointFormats is { } formats && (formats.Length > 1024 || formats.Any(char.IsControl)))
            throw new ArgumentException("The endpoint format fingerprint is invalid.");
    }
}
