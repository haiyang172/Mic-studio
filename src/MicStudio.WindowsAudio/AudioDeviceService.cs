using System.Security.Cryptography;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MicStudio.WindowsAudio;

public sealed record AudioDeviceInfo(string Id, string Name, string State,
    bool IsDefaultConsole, bool IsDefaultCommunications, bool IsCableSend, bool IsCableReceive,
    int SampleRate, int Channels)
{
    public bool IsActive => State == DeviceState.Active.ToString();
    public string ShortId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Id)))[..6];
    public string DisplayName => $"{Name} · {ShortId}{(IsDefaultCommunications ? " · 默认通信" : IsDefaultConsole ? " · 默认" : "")}{(!IsActive ? " · " + State : "")}";
    public override string ToString() => DisplayName;
}

/// <summary>Endpoint enumeration never starts a capture stream or changes Windows defaults.</summary>
public sealed class AudioDeviceService : IDisposable, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _disposed;
    public event EventHandler? DevicesChanged;
    public AudioDeviceService() => _enumerator.RegisterEndpointNotificationCallback(this);
    public IReadOnlyList<AudioDeviceInfo> EnumerateInputs(bool includeInactive = true) => Enumerate(DataFlow.Capture, includeInactive);
    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputs(bool includeInactive = true) => Enumerate(DataFlow.Render, includeInactive);

    /// <summary>Reads the Windows endpoint level. This does not start microphone capture.</summary>
    public VolumeInfo GetInputVolume(string id)
    {
        using var device = GetActiveInput(id);
        var volume = device.AudioEndpointVolume;
        var range = volume.VolumeRange;
        return new(volume.MasterVolumeLevelScalar, volume.Mute, range.MinDecibels, range.MaxDecibels,
            range.IncrementDecibels, volume.MasterVolumeLevel);
    }

    /// <summary>Explicitly changes the selected system input level; affects other applications using that endpoint.</summary>
    public void SetInputVolume(string id, double scalar)
    {
        if (!double.IsFinite(scalar) || scalar is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(scalar), "Windows 输入音量必须在 0 到 1 之间。");
        using var device = GetActiveInput(id);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)scalar;
    }

    public void SetInputMute(string id, bool muted)
    {
        using var device = GetActiveInput(id);
        device.AudioEndpointVolume.Mute = muted;
    }

    private MMDevice GetActiveInput(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var device = _enumerator.GetDevice(id);
        if (device.DataFlow == DataFlow.Capture && device.State == DeviceState.Active) return device;
        device.Dispose();
        throw new InvalidOperationException("所选录音端点未连接或已停用；不会自动修改其他麦克风。");
    }

    private IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow, bool includeInactive)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var console = DefaultId(flow, Role.Console);
        var communications = DefaultId(flow, Role.Communications);
        var result = new List<AudioDeviceInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, includeInactive ? DeviceState.All : DeviceState.Active))
        {
            using (device)
            {
                var rate = 0;
                var channels = 0;
                if (device.State == DeviceState.Active)
                {
                    try { var format = device.AudioClient.MixFormat; rate = format.SampleRate; channels = format.Channels; }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
                }
                var name = device.FriendlyName;
                result.Add(new(device.ID, name, device.State.ToString(), device.ID == console, device.ID == communications,
                    flow == DataFlow.Render && IsCableInput(name), flow == DataFlow.Capture && IsCableOutput(name), rate, channels));
            }
        }
        return result.OrderByDescending(x => x.IsActive).ThenByDescending(x => x.IsDefaultCommunications).ThenBy(x => x.Name).ToArray();
    }

    private string? DefaultId(DataFlow flow, Role role)
    {
        try { using var device = _enumerator.GetDefaultAudioEndpoint(flow, role); return device.ID; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    public static bool IsCableInput(string name) => name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CABLE-A Input", StringComparison.OrdinalIgnoreCase) || name.Contains("CABLE-B Input", StringComparison.OrdinalIgnoreCase);
    public static bool IsCableOutput(string name) => name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CABLE-A Output", StringComparison.OrdinalIgnoreCase) || name.Contains("CABLE-B Output", StringComparison.OrdinalIgnoreCase);
    public static bool IsVirtualCableEndpoint(string name) => IsCableInput(name) || IsCableOutput(name)
        || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CABLE In", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CABLE Out", StringComparison.OrdinalIgnoreCase);

    private void Changed() { if (!_disposed) { try { DevicesChanged?.Invoke(this, EventArgs.Empty); } catch { /* Never throw through a COM notification. */ } } }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed();
    public void OnDeviceAdded(string deviceId) => Changed();
    public void OnDeviceRemoved(string deviceId) => Changed();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Changed();
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) => Changed();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } finally { _enumerator.Dispose(); }
    }
}
