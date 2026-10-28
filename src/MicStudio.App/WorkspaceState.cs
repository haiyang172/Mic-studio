using MicStudio.Core;

namespace MicStudio.App;

public sealed class WorkspaceState
{
    public int SchemaVersion { get; set; } = 1;
    public AudioSettings Draft { get; set; } = new();
    public string? InputDeviceId { get; set; }
    public string? CableOutputId { get; set; }
    public string? PlaybackOutputId { get; set; }
    public int Mode { get; set; }
    public string SelectedPresetId { get; set; } = "";
    public string CreativeThemeId { get; set; } = "warm";
    public int CreativeEnabledMask { get; set; } = 1;
    public List<SoundPreset> Presets { get; set; } = new();
    public DateTimeOffset? DiscordVerifiedAt { get; set; }
    public string Theme { get; set; } = "dark";
    public bool MinimizeToTray { get; set; }
    public string Language { get; set; } = "zh-CN";
}
