using MicStudio.Core;

namespace MicStudio.WindowsAudio;

public sealed class CaptureOptions
{
    public required string InputDeviceId { get; init; }
    public bool ProcessLocally { get; init; }
    public AudioSettings Settings { get; init; } = new();
    public string? CableOutputDeviceId { get; init; }
    public string? MonitorOutputDeviceId { get; init; }
    public bool MonitorConfirmedHeadphones { get; init; }
}

public sealed record AudioMeter(double InputPeakDb, double InputRmsDb, double OutputPeakDb, double OutputRmsDb,
    int SampleRate, int Channels, long ClippedSamples, TimeSpan Elapsed, int SessionId = 0);
public sealed record AudioEngineStatus(bool IsRunning, string Message, string? ErrorCode = null, int SessionId = -1);
public sealed record RecordingResult(string Path, TimeSpan Duration, string Reason, bool CapturedBeforeLocalDsp = false);
public sealed record VolumeInfo(double Scalar, bool Muted, float MinDb, float MaxDb, float StepDb, double LevelDb);
