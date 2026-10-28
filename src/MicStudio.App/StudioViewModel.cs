using System.ComponentModel;
using System.Text.Json;
using MicStudio.Core;

namespace MicStudio.App;

public sealed class StudioViewModel : INotifyPropertyChanged
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("MICSTUDIO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicStudio.Next");
    private readonly Stack<AudioSettings> undo = new(), redo = new();
    private DateTime lastHistory;
    public WorkspaceState State { get; private set; }
    public string? RecoveryWarning { get; }
    public bool IsFirstRun { get; } = !File.Exists(Path.Combine(DataDirectory, "workspace.json"));
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? DraftChanged;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
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
        if (!coalesce || DateTime.UtcNow - lastHistory > TimeSpan.FromMilliseconds(400))
        { undo.Push(State.Draft.Clone()); lastHistory = DateTime.UtcNow; }
        redo.Clear();
        action(State.Draft);
        State.DiscordVerifiedAt = null;
        Notify();
    }
    public void Replace(AudioSettings settings)
    {
        settings.Validate();
        undo.Push(State.Draft.Clone()); redo.Clear(); State.Draft = settings.Clone(); State.DiscordVerifiedAt = null; Notify();
    }
    public void Undo() { if (undo.TryPop(out var s)) { redo.Push(State.Draft.Clone()); State.Draft = s; State.DiscordVerifiedAt = null; Notify(); } }
    public void Redo() { if (redo.TryPop(out var s)) { undo.Push(State.Draft.Clone()); State.Draft = s; State.DiscordVerifiedAt = null; Notify(); } }
    public void Save() => SettingsStore.Save(Path.Combine(DataDirectory, "workspace.json"), State);
    public string DraftJson => JsonSerializer.Serialize(State.Draft, JsonDefaults.Options);
    private void Notify() { PropertyChanged?.Invoke(this, new(nameof(State))); DraftChanged?.Invoke(this, EventArgs.Empty); }
}
