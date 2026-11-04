using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private MicrophoneVolumeLock? volumeLock;
    private TextBlock? volumeLockStatusText;
    private System.Windows.Controls.Button? volumeLockRetry;
    private double smokeVolume = .5;
    private int smokeVolumeWrites;

    private void InitializeVolumeLock()
    {
        volumeLock = UiSmoke ? new MicrophoneVolumeLock(_ => smokeVolume, (_, value) => { smokeVolume = value; smokeVolumeWrites++; }, automaticPolling: false) : new();
        volumeLock.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() =>
        {
            if (closed || volumeLock == null || status.Generation != volumeLock.State.Generation) return;
            UpdateVolumeLockStatus();
        });
    }
    private void RestoreVolumeLockPreference()
    {
        if (State.VolumeLockEnabled && State.VolumeLockEndpointId == State.InputDeviceId && SelectedInput is { IsActive: true })
            volumeLock?.Enable(State.VolumeLockEndpointId!);
    }
    private void DisableVolumeLockForDeviceChange()
    {
        volumeLock?.Disable(); State.VolumeLockEnabled = false; State.VolumeLockEndpointId = null;
    }
    private void CheckVolumeLockDevice()
    {
        if (volumeLock?.State.IsActive == true && (SelectedInput is not { IsActive: true } || volumeLock.State.EndpointId != State.InputDeviceId))
            volumeLock.Disable();
        UpdateVolumeLockStatus();
    }
    private void SetSelectedInputVolume(string id, double scalar)
    {
        var previous = volumeLock?.State;
        var coordinated = previous is { IsActive: true } && previous.EndpointId == id;
        if (coordinated) volumeLock!.SetTarget(scalar, id);
        try
        {
            if (UiSmoke) smokeVolume = scalar; else devices.SetInputVolume(id, scalar);
            if (coordinated) volumeLock!.AdoptCurrentTarget(id);
        }
        catch
        {
            if (coordinated && previous?.Target is { } target) volumeLock!.SetTarget(target, id);
            throw;
        }
    }
    private void SetVolumeLockPreference(bool enabled)
    {
        if (!enabled) { State.VolumeLockEnabled = false; volumeLock?.Disable(); SaveSoon(); UpdateVolumeLockStatus(); return; }
        if (SelectedInput is not { IsActive: true } selected)
        { Notice(Loc.T("请先选择已连接的麦克风。", "Select a connected microphone first.")); return; }
        State.VolumeLockEnabled = true; State.VolumeLockEndpointId = selected.Id;
        volumeLock?.Enable(selected.Id); SaveSoon(); UpdateVolumeLockStatus();
    }
    private void UpdateVolumeLockStatus()
    {
        if (volumeLockStatusText == null) return;
        var state = volumeLock?.State;
        volumeLockStatusText.Text = !State.VolumeLockEnabled ? Loc.T("音量锁定已关闭", "Microphone volume lock is off")
            : state is { IsActive: true } ? Loc.F("目标系统音量：{0:0}%", "Target system volume: {0:0}%", state.Target * 100)
            : state?.Code == "conflict" ? Loc.T("已暂停：其他软件反复修改音量。调整冲突设置后重新锁定。", "Paused: another app keeps changing the volume. Resolve the conflict, then lock again.")
            : Loc.T("音量锁定已暂停，请确认所选麦克风后重新锁定。", "Volume lock is paused. Confirm the selected microphone, then lock again.");
        volumeLockStatusText.Foreground = State.VolumeLockEnabled && state is not { IsActive: true } ? Amber : Muted;
        if (volumeLockRetry != null) volumeLockRetry.IsEnabled = State.VolumeLockEnabled && state is not { IsActive: true } && SelectedInput is { IsActive: true };
    }
    private Border VolumeLockSettingsCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Toggle(Loc.T("锁定所选麦克风音量", "Lock selected microphone volume"), State.VolumeLockEnabled, value =>
        { SetVolumeLockPreference(value); ShowPage(6); }));
        panel.Children.Add(T(Loc.T("记录当前系统音量并协调外部改动。自己的音量滑条仍可调；切换麦克风后需重新开启。", "Keeps the current system input level against outside changes. Your own volume slider still works; enable again after changing microphones."), 12, Muted));
        var text = T("", 12, Muted); volumeLockStatusText = text;
        text.Loaded += (_, _) => { volumeLockStatusText = text; UpdateVolumeLockStatus(); };
        text.Unloaded += (_, _) => { if (ReferenceEquals(volumeLockStatusText, text)) volumeLockStatusText = null; };
        panel.Children.Add(text);
        volumeLockRetry = B(Loc.T("重新锁定", "Lock again"), () => SetVolumeLockPreference(true));
        panel.Children.Add(volumeLockRetry); UpdateVolumeLockStatus();
        return Card(Loc.T("麦克风音量锁定", "Microphone volume lock"), null, panel);
    }
}
