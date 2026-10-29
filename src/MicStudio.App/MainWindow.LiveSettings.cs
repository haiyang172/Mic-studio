using System.Text.Json;
using System.Windows.Automation;
using MicStudio.Core;

namespace MicStudio.App;

public partial class MainWindow
{
    private int pendingSettingsSession = -1;

    private void OnSoundChanged()
    {
        if (NativeMode && nativeWriter != null)
        {
            pendingNativeSettings = true;
            if (!liveTimer.IsEnabled) liveTimer.Start();
            applyState = Loc.T("系统参数已发送 · 音频待验证", "System parameters sent · audio unverified");
            UpdateChrome(); curve?.InvalidateVisual(); SaveSoon(); return;
        }
        if (LocalMode && engine.IsRunning)
        {
            pendingSettingsSession = engine.SessionId;
            // Keep only the latest UI revision. The dispatcher serializes commits;
            // there are no worker tasks that can deliver an older slider value later.
            if (!liveTimer.IsEnabled) liveTimer.Start();
        }
        applyState = NativeMode ? Loc.T("自研 APO 参数尚未发送", "Native APO parameters have not been sent") : LocalMode ? engine.IsRunning ? "实时调音" : "设置已就绪" : "APO 需在设备页更新";
        UpdateChrome(); curve?.InvalidateVisual(); SaveSoon();
    }

    private void FlushLiveSettings()
    {
        liveTimer.Stop();
        if (pendingNativeSettings && NativeMode && nativeWriter != null)
        {
            pendingNativeSettings = false;
            try { WriteNativeSettings(); }
            catch (Exception error) { StopNativeProcessing(); Notice(error.Message); }
            UpdateChrome(); return;
        }
        var expectedSession = pendingSettingsSession;
        pendingSettingsSession = -1;
        if (closed || !LocalMode || !engine.IsRunning || expectedSession != engine.SessionId) return;
        try
        {
            var snapshot = Draft.CreateProcessingSnapshot(engine.SampleRate, out _);
            if (engine.TryUpdateSettings(snapshot, expectedSession))
            {
                applied = snapshot;
                applyState = "实时调音";
                UpdateChrome();
            }
        }
        catch (Exception ex)
        {
            applyState = "调音未更新";
            UpdateChrome(); Notice(Loc.T("当前声音保持上一次有效设置：", "Audio keeps the last valid settings: ") + ex.Message);
        }
    }

    private void RestoreDefaults()
    {
        vm.Replace(AudioSettings.CreateDefaultForSampleRate(SampleRate));
        State.SelectedPresetId = "";
        State.CreativeThemeId = "warm";
        State.CreativeEnabledMask = 1;
        SaveSoon(); ShowPage(currentPage);
    }

    private static string SettingsJson(AudioSettings settings) => JsonSerializer.Serialize(settings, JsonDefaults.Options);
    private string CurrentPresetName()
    {
        var preset = AllPresets().FirstOrDefault(p => p.Id == State.SelectedPresetId);
        if (preset != null && SettingsJson(preset.Settings) == vm.DraftJson) return Loc.PresetName(preset);
        if (string.IsNullOrEmpty(State.SelectedPresetId) && (SettingsJson(AudioSettings.CreateDefaultForSampleRate(SampleRate)) == vm.DraftJson || SettingsJson(new AudioSettings()) == vm.DraftJson)) return Loc.T("无", "None");
        return Loc.T("自定义", "Custom");
    }

    private static bool SameEq(AudioSettings a, AudioSettings b) => a.EqEnabled == b.EqEnabled &&
        JsonSerializer.Serialize(a.Bands, JsonDefaults.Options) == JsonSerializer.Serialize(b.Bands, JsonDefaults.Options);

    private void LoadEqPreset(SoundPreset? preset)
    {
        var settings = preset?.Settings ?? AudioSettings.CreateDefaultForSampleRate(SampleRate);
        settings.Validate();
        // EQ themes only change EQ, preserving calibrated gain, effects and device routing.
        var copy = settings.Clone();
        vm.Change(s => { s.Bands = copy.Bands; s.EqEnabled = copy.EqEnabled; });
        State.SelectedPresetId = preset?.Id ?? "";
        SaveSoon(); ShowPage(currentPage);
    }

    private UIElement BuildPresetSelector(bool eqOnly = true)
    {
        var panel = new StackPanel { Margin = new(0, 0, 0, 10) };
        panel.Children.Add(T(eqOnly ? "EQ 主题" : "声音主题", 12, Muted));
        var picker = new ComboBox { MinWidth = 150, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(picker, Loc.T(eqOnly ? "EQ 主题" : "声音主题", eqOnly ? "EQ theme" : "Sound theme"));
        var defaults = AudioSettings.CreateDefaultForSampleRate(SampleRate);
        var defaultItem = new ComboBoxItem { Content = Loc.T("默认", "Default") }; picker.Items.Add(defaultItem);
        ComboBoxItem? selected = (eqOnly ? SameEq(Draft, defaults) : SettingsJson(Draft) == SettingsJson(defaults)) ? defaultItem : null;
        foreach (var preset in AllPresets())
        {
            var item = new ComboBoxItem { Content = Loc.PresetName(preset), Tag = preset };
            picker.Items.Add(item);
            if (preset.Id == State.SelectedPresetId && (eqOnly ? SameEq(Draft, preset.Settings) : SettingsJson(Draft) == SettingsJson(preset.Settings))) selected = item;
        }
        var customItem = new ComboBoxItem { Content = Loc.T("自定义", "Custom") }; picker.Items.Add(customItem);
        picker.SelectedItem = selected ?? customItem;
        picker.SelectionChanged += (_, _) =>
        {
            if (building || picker.SelectedItem is not ComboBoxItem item || item == customItem) return;
            if (eqOnly) LoadEqPreset(item.Tag as SoundPreset);
            else if (item.Tag is SoundPreset preset) LoadPreset(preset);
            else RestoreDefaults();
        };
        RegisterSoundControl(picker, () =>
        {
            ComboBoxItem? match = (eqOnly ? SameEq(Draft, defaults) : SettingsJson(Draft) == SettingsJson(defaults)) ? defaultItem : null;
            foreach (ComboBoxItem item in picker.Items)
                if (item.Tag is SoundPreset preset && preset.Id == State.SelectedPresetId && (eqOnly ? SameEq(Draft, preset.Settings) : SettingsJson(Draft) == SettingsJson(preset.Settings))) match = item;
            picker.SelectedItem = match ?? customItem;
        });
        panel.Children.Add(picker); return panel;
    }
}
