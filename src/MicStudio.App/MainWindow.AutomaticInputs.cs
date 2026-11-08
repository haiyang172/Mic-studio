using System.Windows.Automation;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private bool automaticInputsExpanded;

    private UIElement AutomaticInputControls()
    {
        var preferences = State.AutomaticInputs;
        var content = new StackPanel();
        var enabled = Toggle(Loc.T("原麦克风断开时使用备用", "Use a backup when the microphone disconnects"),
            preferences.UseBackups, value =>
            {
                preferences.UseBackups = value;
                AutomaticInputPreferencesChanged();
            }, State.ResumeSameDevice);
        AutomationProperties.SetAutomationId(enabled, "automatic-inputs-enabled");
        content.Children.Add(enabled);
        content.Children.Add(T(State.ResumeSameDevice
            ? Loc.T("按以下顺序选择备用麦克风，使用当前声音设置。", "Uses backups in the order below with the current sound settings.")
            : Loc.T("先开启断线恢复，再启用备用切换。", "Enable connection recovery before enabling backups."), 12, Muted));

        var backupCandidates = inputs.Where(IsBackupCandidate)
            .Where(d => d.Id != State.InputDeviceId && !preferences.IsExcluded(d.Id) && preferences.Backups.All(b => b.Id != d.Id))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var backupPicker = AutomaticInputPicker(backupCandidates, "automatic-backup-picker");
        var addBackup = B(Loc.T("加入备用", "Add backup"), () =>
        {
            if (backupPicker.SelectedItem is not AudioDeviceInfo selected) return;
            var current = inputs.FirstOrDefault(d => d.Id == selected.Id && IsBackupCandidate(d));
            if (current == null || current.Id == State.InputDeviceId || preferences.IsExcluded(current.Id))
            {
                Notice(Loc.T("请刷新设备并选择可用的备用麦克风。", "Refresh devices and choose an available backup microphone."));
                return;
            }
            if (preferences.Backups.Count >= AutomaticInputPreferences.MaximumBackups || preferences.Backups.Any(b => b.Id == current.Id)) return;
            var snapshot = new BackupMicrophone(current.Id, current.Name, current.SampleRate, current.Channels);
            try { AutomaticInputPreferences.ValidateBackup(snapshot); }
            catch (ArgumentException)
            {
                Notice(Loc.T("设备名称或格式无法保存，请刷新设备后重试。", "The device name or format cannot be saved. Refresh devices and retry."));
                return;
            }
            preferences.Backups.Add(snapshot);
            AutomaticInputPreferencesChanged();
        });
        addBackup.IsEnabled = false;
        AutomationProperties.SetAutomationId(addBackup, "automatic-backup-add");
        backupPicker.SelectionChanged += (_, _) => addBackup.IsEnabled = backupPicker.SelectedItem != null && preferences.Backups.Count < AutomaticInputPreferences.MaximumBackups;
        content.Children.Add(T(Loc.T("备用顺序", "Backup order"), 14, Ink, FontWeights.SemiBold, new(0, 12, 0, 8)));
        content.Children.Add(backupPicker);
        content.Children.Add(Buttons(addBackup));
        for (var index = 0; index < preferences.Backups.Count; index++)
            content.Children.Add(BackupMicrophoneRow(preferences.Backups[index], index));

        content.Children.Add(T(Loc.T("自动选择排除名单", "Excluded from automatic selection"), 14, Ink, FontWeights.SemiBold, new(0, 14, 0, 8)));
        content.Children.Add(T(Loc.T("对自动麦克风选择和恢复生效。", "Applies to automatic microphone selection and recovery."), 12, Muted));
        var exclusions = AutomaticExclusionCandidates().Where(d => !preferences.IsExcluded(d.Id)).ToList();
        var exclusionPicker = AutomaticInputPicker(exclusions, "automatic-exclusion-picker");
        var addExclusion = B(Loc.T("加入排除", "Exclude microphone"), () =>
        {
            if (exclusionPicker.SelectedItem is not AudioDeviceInfo selected || preferences.IsExcluded(selected.Id) ||
                preferences.ExcludedInputIds.Count >= AutomaticInputPreferences.MaximumExcludedInputs) return;
            try { AutomaticInputPreferences.ValidateEndpointId(selected.Id); }
            catch (ArgumentException)
            {
                Notice(Loc.T("设备标识无法保存，请刷新设备后重试。", "The device identity cannot be saved. Refresh devices and retry."));
                return;
            }
            preferences.ExcludedInputIds.Add(selected.Id);
            AutomaticInputPreferencesChanged();
        });
        addExclusion.IsEnabled = false;
        AutomationProperties.SetAutomationId(addExclusion, "automatic-exclusion-add");
        exclusionPicker.SelectionChanged += (_, _) => addExclusion.IsEnabled = exclusionPicker.SelectedItem != null && preferences.ExcludedInputIds.Count < AutomaticInputPreferences.MaximumExcludedInputs;
        content.Children.Add(exclusionPicker);
        content.Children.Add(Buttons(addExclusion));
        foreach (var id in preferences.ExcludedInputIds)
        {
            var device = AutomaticExclusionCandidates().FirstOrDefault(d => d.Id == id) ??
                new AudioDeviceInfo(id, Loc.T("已保存的麦克风", "Saved microphone"), "Disconnected", false, false, false, false, 0, 0);
            var row = new StackPanel { Margin = new(0, 4, 0, 6) };
            row.Children.Add(AutomaticDeviceName(device));
            var remove = B(Loc.T("移出排除", "Allow automatic selection"), () =>
            {
                if (preferences.ExcludedInputIds.Remove(id)) AutomaticInputPreferencesChanged();
            });
            AutomationProperties.SetAutomationId(remove, "automatic-exclusion-remove-" + preferences.ExcludedInputIds.IndexOf(id));
            row.Children.Add(Buttons(remove));
            content.Children.Add(row);
        }

        var details = new Expander
        {
            Header = Loc.T("备用与排除", "Backup and exclusions"),
            Content = content,
            Foreground = Ink,
            Margin = new(0, 10, 0, 8),
            IsExpanded = automaticInputsExpanded
        };
        AutomationProperties.SetAutomationId(details, "automatic-inputs");
        details.Expanded += (_, _) => automaticInputsExpanded = true;
        details.Collapsed += (_, _) => automaticInputsExpanded = false;
        return details;
    }

    private UIElement BackupMicrophoneRow(BackupMicrophone backup, int index)
    {
        var preferences = State.AutomaticInputs;
        var current = inputs.FirstOrDefault(d => d.Id == backup.Id);
        var device = current ?? new AudioDeviceInfo(backup.Id, backup.Name, "Disconnected", false, false, false, false, backup.SampleRate, backup.Channels);
        var row = new StackPanel { Margin = new(0, 6, 0, 8) };
        var name = AutomaticDeviceName(device);
        name.Text = (index + 1) + "  " + name.Text;
        AutomationProperties.SetAutomationId(name, "automatic-backup-name-" + index);
        row.Children.Add(name);
        var status = preferences.IsExcluded(backup.Id) ? Loc.T("已排除", "Excluded")
            : current is not { IsActive: true } ? Loc.T("已断开", "Disconnected")
            : current.SampleRate != backup.SampleRate || current.Channels != backup.Channels ? Loc.T("格式已变化", "Format changed")
            : Loc.T("已连接", "Connected");
        row.Children.Add(T(Loc.F("{0:0.#} kHz · {1} 声道 · {2}", "{0:0.#} kHz · {1} ch · {2}", backup.SampleRate / 1000.0, backup.Channels, status), 12, Muted));
        var up = B(Loc.T("上移", "Move up"), () => MoveBackupMicrophone(backup.Id, -1));
        var down = B(Loc.T("下移", "Move down"), () => MoveBackupMicrophone(backup.Id, 1));
        up.IsEnabled = index > 0;
        down.IsEnabled = index < preferences.Backups.Count - 1;
        var remove = B(Loc.T("移除", "Remove"), () =>
        {
            if (preferences.Backups.RemoveAll(b => b.Id == backup.Id) > 0) AutomaticInputPreferencesChanged();
        });
        AutomationProperties.SetAutomationId(up, "automatic-backup-up-" + index);
        AutomationProperties.SetAutomationId(down, "automatic-backup-down-" + index);
        AutomationProperties.SetAutomationId(remove, "automatic-backup-remove-" + index);
        var actions = Buttons(up, down, remove);
        if (current != null && IsBackupCandidate(current) && (current.SampleRate != backup.SampleRate || current.Channels != backup.Channels))
        {
            var update = B(Loc.T("更新格式", "Update format"), () =>
            {
                var available = inputs.FirstOrDefault(d => d.Id == backup.Id && IsBackupCandidate(d));
                var position = preferences.Backups.FindIndex(b => b.Id == backup.Id);
                if (available == null || position < 0) return;
                var snapshot = new BackupMicrophone(available.Id, available.Name, available.SampleRate, available.Channels);
                try { AutomaticInputPreferences.ValidateBackup(snapshot); }
                catch (ArgumentException)
                {
                    Notice(Loc.T("设备名称或格式无法保存，请刷新设备后重试。", "The device name or format cannot be saved. Refresh devices and retry."));
                    return;
                }
                preferences.Backups[position] = snapshot;
                AutomaticInputPreferencesChanged();
            });
            AutomationProperties.SetAutomationId(update, "automatic-backup-format-" + index);
            actions.Children.Add(update);
        }
        row.Children.Add(actions);
        return row;
    }

    private void MoveBackupMicrophone(string id, int offset)
    {
        var backups = State.AutomaticInputs.Backups;
        var index = backups.FindIndex(b => b.Id == id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= backups.Count) return;
        (backups[index], backups[target]) = (backups[target], backups[index]);
        AutomaticInputPreferencesChanged();
    }

    private static bool IsBackupCandidate(AudioDeviceInfo device) => device.IsActive && !device.IsCableReceive &&
        !AudioDeviceService.IsVirtualCableEndpoint(device.Name) && device.SampleRate is >= 8000 and <= 384000 && device.Channels is >= 1 and <= 8;

    private static ComboBox AutomaticInputPicker(IEnumerable<AudioDeviceInfo> devices, string automationId)
    {
        var picker = new ComboBox { ItemsSource = devices.ToList(), DisplayMemberPath = nameof(AudioDeviceInfo.DisplayName), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 4, 0, 8) };
        AutomationProperties.SetAutomationId(picker, automationId);
        AutomationProperties.SetName(picker, automationId == "automatic-backup-picker" ? Loc.T("选择备用麦克风", "Choose backup microphone") : Loc.T("选择排除麦克风", "Choose excluded microphone"));
        return picker;
    }

    private TextBlock AutomaticDeviceName(AudioDeviceInfo device) => new()
    {
        Text = device.Name + " · " + device.ShortId,
        DataContext = device,
        Foreground = Ink,
        TextWrapping = TextWrapping.Wrap,
        ToolTip = device.Name,
        Margin = new(0, 0, 0, 5)
    };

    private IEnumerable<AudioDeviceInfo> AutomaticExclusionCandidates()
    {
        var devices = inputs.Where(d => !d.IsCableReceive && !AudioDeviceService.IsVirtualCableEndpoint(d.Name)).ToList();
        foreach (var backup in State.AutomaticInputs.Backups)
            if (devices.All(d => d.Id != backup.Id)) devices.Add(new(backup.Id, backup.Name, "Disconnected", false, false, false, false, backup.SampleRate, backup.Channels));
        if (State.InputDeviceId is { } selectedId && devices.All(d => d.Id != selectedId))
            devices.Add(new(selectedId, Loc.T("已保存的麦克风", "Saved microphone"), "Disconnected", false, false, false, false, 0, 0));
        return devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);
    }
}
