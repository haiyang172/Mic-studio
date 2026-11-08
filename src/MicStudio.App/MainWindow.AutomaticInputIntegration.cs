using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private string? smokeRecoveryInputId;
    private Action? smokeRecoveryDuringCommit;

    private AudioDeviceInfo? InputForAudioStart(RecoveryAttempt? attempt)
    {
        var requestedId = attempt?.Route.InputId ?? State.InputDeviceId;
        return inputs.FirstOrDefault(input => input.Id == requestedId && input.IsActive);
    }

    private AudioDeviceInfo? ChooseInitialInput()
    {
        var candidates = inputs.Where(input => input.IsActive && !input.IsCableReceive &&
            !State.AutomaticInputs.IsExcluded(input.Id)).ToList();
        return candidates.FirstOrDefault(input => input.IsDefaultCommunications) ?? candidates.FirstOrDefault();
    }

    private void AutomaticInputPreferencesChanged()
    {
        State.AutomaticInputs.Normalize();
        CancelDeviceRecovery("automatic_inputs_changed");
        if (engine.IsRunning && activeCableSendId != null) RememberDeviceRecovery(true);
        SaveSoon();
        ShowPage(currentPage);
    }

    private bool CompletedRecoveryStillCurrent(RecoveryAttempt attempt, long generation, int openedSession, bool committed)
    {
        var state = deviceRecovery.State;
        if (state.Generation != generation || !state.HasRunningIntent || state.Route != attempt.Route) return false;
        if (attempt.Route.InputId != (attempt.SourceRoute ?? attempt.Route).InputId && !State.AutomaticInputs.UseBackups) return false;
        if (!UiSmoke && (!engine.IsRunning || engine.SessionId != openedSession)) return false;
        var expectedRoute = committed ? attempt.Route : attempt.SourceRoute ?? attempt.Route;
        return !State.AutomaticInputs.IsExcluded(attempt.Route.InputId) &&
            RecoverySafetyAllows(expectedRoute) && TryReadRecoveryRoute(out var current, attempt.Route.InputId) && current == attempt.Route;
    }

    private bool CommitAutomaticInput(RecoveryAttempt attempt, long generation, int openedSession)
    {
        if (!CompletedRecoveryStillCurrent(attempt, generation, openedSession, false)) return false;
        volumeLock?.Disable();
        if (UiSmoke) smokeRecoveryDuringCommit?.Invoke();
        if (!CompletedRecoveryStillCurrent(attempt, generation, openedSession, false)) return false;
        var inputId = attempt.Route.InputId;
        State.VolumeLockEnabled = false;
        State.VolumeLockEndpointId = null;
        suggestedGain = null;
        State.InputDeviceId = inputId;
        var wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            InputPicker.SelectedItem = InputPicker.Items.OfType<AudioDeviceInfo>().FirstOrDefault(input => input.Id == inputId);
        }
        finally { refreshing = wasRefreshing; }
        InvalidateDiscord();
        SaveSoon();
        Dispatcher.BeginInvoke(() => RebuildAutomaticInputView(inputId, generation, openedSession));
        return true;
    }

    private void RebuildAutomaticInputView(string inputId, long generation, int openedSession)
    {
        if (closed || State.InputDeviceId != inputId || deviceRecovery.State.Generation != generation || engine.SessionId != openedSession) return;
        ShowPage(currentPage);
    }

    private void ShowAutomaticInputNotice(string inputId)
    {
        var name = inputs.FirstOrDefault(input => input.Id == inputId)?.Name ?? Loc.T("已配置的麦克风", "the configured microphone");
        var message = Loc.F("已自动切换到 {0}，使用当前声音设置。", "Switched automatically to {0} using the current sound settings.", name);
        Notice(message);
        if (UiSmoke) return;
        try { tray?.ShowBalloonTip(5000, "Mic Studio", message, System.Windows.Forms.ToolTipIcon.Info); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private string AutomaticInputDiagnosticDetails() => Loc.F(
        "\n\n自动输入配置\n备用切换：{0}\n备用数量：{1}\n排除数量：{2}",
        "\n\nAutomatic input configuration\nBackups enabled: {0}\nBackup count: {1}\nExcluded count: {2}",
        State.AutomaticInputs.UseBackups, State.AutomaticInputs.Backups.Count, State.AutomaticInputs.ExcludedInputIds.Count);
}
