using System.ComponentModel;
using System.Text.Json;
using MicStudio.Core;

namespace MicStudio.App;

public sealed class StudioViewModel : INotifyPropertyChanged
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("MICSTUDIO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicStudio.Next");
    public WorkspaceState State { get; private set; }
    public string? RecoveryWarning { get; }
    public bool IsFirstRun { get; } = !File.Exists(Path.Combine(DataDirectory, "workspace.json"));
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? DraftChanged;
    public StudioViewModel()
    {
        var loaded = SettingsStore.Load<WorkspaceState>(Path.Combine(DataDirectory, "workspace.json"));
        State = loaded.Value;
        RecoveryWarning = loaded.Warning;
        try
        {
            if (State.SchemaVersion != 1 || State.Mode is < 0 or > 1 || State.Presets is null || State.Presets.Count > 200) throw new ArgumentException("工作区版本或数量无效。");
            State.Draft.Validate();
            foreach (var p in State.Presets) p.Validate();
            var creative = State.Draft.Creative;
            var activeMask = (creative.SaturationEnabled ? 1 : 0) | (creative.RobotEnabled ? 2 : 0) | (creative.DelayEnabled ? 4 : 0) | (creative.ReverbEnabled ? 8 : 0);
            var themeMask = State.CreativeThemeId switch { "warm" or "crunch" => 1, "robot" => 2, "echo" => 4, "space" => 8, "custom" => activeMask, _ => -1 };
            // Old workspaces had the effect flags but no named creative theme.
            if (themeMask == -1 || (activeMask != 0 && activeMask != themeMask)) State.CreativeThemeId = "custom";
            if (State.CreativeEnabledMask is < 1 or > 15) State.CreativeEnabledMask = activeMask == 0 ? 1 : activeMask;
        }
        catch (Exception ex)
        {
            var path = Path.Combine(DataDirectory, "workspace.json");
            if (File.Exists(path)) File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
            State = new(); RecoveryWarning = "设置未通过校验，原文件已保留。" + ex.Message;
        }
    }
    public void Change(Action<AudioSettings> action, bool coalesce = false)
    {
        // Slider revisions are coalesced at the live audio dispatcher; there is no
        // hidden undo history retaining settings throughout a background session.
        action(State.Draft);
        State.DiscordVerifiedAt = null;
        Notify();
    }
    public void Replace(AudioSettings settings)
    {
        settings.Validate();
        State.Draft = settings.Clone(); State.DiscordVerifiedAt = null; Notify();
    }
    public void Save() => SettingsStore.Save(Path.Combine(DataDirectory, "workspace.json"), State);
    public string DraftJson => JsonSerializer.Serialize(State.Draft, JsonDefaults.Options);
    private void Notify() { PropertyChanged?.Invoke(this, new(nameof(State))); DraftChanged?.Invoke(this, EventArgs.Empty); }
}
