namespace MicStudio.WindowsAudio;

public enum VolumeLockDecision { None, Restore, Pause }

/// <summary>Pure, monotonic-time policy. The host serializes access and performs the requested endpoint write.</summary>
public sealed class MicrophoneVolumeLockPolicy
{
    public const double Tolerance = .006;
    public const long MinimumRestoreIntervalMs = 750;
    public const long ConflictWindowMs = 10000;
    public const int MaximumRestoresPerWindow = 4;
    private long lastObservedMs, windowStartMs, lastRestoreMs;
    private int restoresInWindow;
    public bool Enabled { get; private set; }
    public bool Paused { get; private set; }
    public double? Target { get; private set; }
    public int RestoreAttempts { get; private set; }
    public string Code { get; private set; } = "off";

    public void Enable(double actual, long nowMs)
    {
        Validate(actual); ValidateTime(nowMs);
        Enabled = true; Paused = false; Target = actual; RestoreAttempts = 0;
        windowStartMs = lastObservedMs = nowMs; lastRestoreMs = long.MinValue; restoresInWindow = 0;
        Code = "active";
    }

    public void SetTarget(double scalar)
    {
        Validate(scalar); Target = scalar;
        // A paused conflict is not silently resumed by moving the slider.
    }

    public VolumeLockDecision Observe(double actual, long nowMs)
    {
        if (!Enabled || Paused || Target == null) return VolumeLockDecision.None;
        if (!double.IsFinite(actual) || actual is < 0 or > 1) { Pause("invalid_volume"); return VolumeLockDecision.Pause; }
        if (nowMs < lastObservedMs || nowMs < 0) { Pause("clock_changed"); return VolumeLockDecision.Pause; }
        lastObservedMs = nowMs;
        if (nowMs - windowStartMs >= ConflictWindowMs) { windowStartMs = nowMs; restoresInWindow = 0; }
        if (Math.Abs(actual - Target.Value) <= Tolerance) return VolumeLockDecision.None;
        if (lastRestoreMs != long.MinValue && nowMs - lastRestoreMs < MinimumRestoreIntervalMs) return VolumeLockDecision.None;
        if (restoresInWindow >= MaximumRestoresPerWindow) { Pause("conflict"); return VolumeLockDecision.Pause; }
        lastRestoreMs = nowMs; restoresInWindow++; RestoreAttempts++;
        return VolumeLockDecision.Restore;
    }

    public void Pause(string code) { if (Enabled) { Paused = true; Code = code; } }
    public void Disable()
    {
        Enabled = false; Paused = false; Target = null; Code = "off";
        RestoreAttempts = restoresInWindow = 0; lastRestoreMs = long.MinValue;
    }
    private static void Validate(double scalar) { if (!double.IsFinite(scalar) || scalar is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(scalar)); }
    private static void ValidateTime(long nowMs) { if (nowMs < 0) throw new ArgumentOutOfRangeException(nameof(nowMs)); }
}
