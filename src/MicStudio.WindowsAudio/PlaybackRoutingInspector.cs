using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MicStudio.WindowsAudio;

public sealed record PlaybackRoutingReport(bool DefaultPlaybackIsVirtual, bool DefaultCommunicationsIsVirtual,
    int DiscordVirtualSessions, int DiscordPhysicalSessions, bool InspectionComplete,
    int DiscordPhysicalCaptureSessions = 0, int DiscordCableCaptureSessions = 0, bool CaptureInspectionComplete = false,
    IReadOnlyList<string>? DiscordVirtualOutputFamilies = null)
{
    public bool HasDigitalLoopRisk => DefaultPlaybackIsVirtual || DefaultCommunicationsIsVirtual || DiscordVirtualSessions > 0;
}

/// <summary>Read-only endpoint/session inspection. Never captures, plays, changes defaults or edits Discord.</summary>
public static class PlaybackRoutingInspector
{
    internal static IReadOnlyList<string> ActiveDiscordPhysicalOutputIds(MMDeviceEnumerator enumerator, out bool complete)
    {
        var found = new List<string>(); complete = true;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                if (AudioDeviceService.IsVirtualCableEndpoint(device.FriendlyName)) continue;
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var index = 0; index < sessions.Count; index++)
                    {
                        using var session = sessions[index];
                        if (session.State != AudioSessionState.AudioSessionStateActive || session.GetProcessID == 0) continue;
                        try
                        {
                            using var process = Process.GetProcessById((int)session.GetProcessID);
                            if (!process.ProcessName.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)) continue;
                            found.Add(device.ID); break;
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { complete = false; }
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { complete = false; }
            }
        }
        return found;
    }

    public static PlaybackRoutingReport Inspect()
    {
        using var enumerator = new MMDeviceEnumerator();
        var complete = true;
        bool DefaultVirtual(Role role)
        {
            try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role); return AudioDeviceService.IsVirtualCableEndpoint(device.FriendlyName); }
            catch (System.Runtime.InteropServices.COMException) { complete = false; return false; }
        }
        var playback = DefaultVirtual(Role.Multimedia) || DefaultVirtual(Role.Console);
        var communications = DefaultVirtual(Role.Communications);
        var virtualSessions = 0; var physicalSessions = 0;
        var virtualFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var index = 0; index < sessions.Count; index++)
                    {
                        using var session = sessions[index];
                        if (session.State != AudioSessionState.AudioSessionStateActive || session.GetProcessID == 0) continue;
                        try
                        {
                            using var process = Process.GetProcessById((int)session.GetProcessID);
                            if (!process.ProcessName.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)) continue;
                            if (AudioDeviceService.IsVirtualCableEndpoint(device.FriendlyName))
                            {
                                virtualSessions++;
                                var name = device.FriendlyName;
                                virtualFamilies.Add(AudioDeviceService.IsCableInput(name) ? name.Split('(')[0].Trim() : "Other virtual playback");
                            }
                            else physicalSessions++;
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { complete = false; }
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { complete = false; }
            }
        }
        var captureComplete = true; var physicalCapture = 0; var cableCapture = 0;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var index = 0; index < sessions.Count; index++)
                    {
                        using var session = sessions[index];
                        if (session.State != AudioSessionState.AudioSessionStateActive || session.GetProcessID == 0) continue;
                        try
                        {
                            using var process = Process.GetProcessById((int)session.GetProcessID);
                            if (!process.ProcessName.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)) continue;
                            if (AudioDeviceService.IsCableOutput(device.FriendlyName)) cableCapture++; else physicalCapture++;
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { captureComplete = false; }
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { captureComplete = false; }
            }
        }
        return new(playback, communications, virtualSessions, physicalSessions, complete, physicalCapture, cableCapture, captureComplete, virtualFamilies.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
