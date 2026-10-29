using MicStudio.Core;

namespace MicStudio.App;

public sealed class WorkspaceState
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public AudioSettings Draft { get; set; } = new();
    public string? InputDeviceId { get; set; }
    public string? CableOutputId { get; set; }
    public string? PlaybackOutputId { get; set; }
    public bool EchoCancellationEnabled { get; set; } = true;
    public string? EchoReferenceOutputDeviceId { get; set; }
    public Guid? NativeApoInstallReportId { get; set; }
    public int Mode { get; set; }
    public string SelectedPresetId { get; set; } = "";
    public string CreativeThemeId { get; set; } = "warm";
    public int CreativeEnabledMask { get; set; } = 1;
    public List<SoundPreset> Presets { get; set; } = new();
    public DateTimeOffset? DiscordVerifiedAt { get; set; }
    public string Theme { get; set; } = "dark";
    // P2: closing the window hides it in the tray and keeps processing by default.
    public bool MinimizeToTray { get; set; } = true;
    public bool LaunchAtLogin { get; set; }
    public bool AutoStartProcessing { get; set; }
    public string Language { get; set; } = UiText.Chinese;

    /// <summary>
    /// Migrates the P1 workspace format and normalizes preferences that came from
    /// hand-edited or older files. Unknown future schema versions are rejected.
    /// </summary>
    public void Normalize()
    {
        if (SchemaVersion == 1)
        {
            // P1 had no background default. The plan changes the default to the tray;
            // old workspaces had no reliable marker for an explicit opt-out.
            SchemaVersion = CurrentSchemaVersion;
            MinimizeToTray = true;
        }
        if (SchemaVersion != CurrentSchemaVersion) throw new ArgumentException(UiText.T("工作区版本不受支持；请保留设置文件并检查版本。", "This workspace version is not supported; keep the settings file and check the version."));
        if (Mode is < 0 or > 2) throw new ArgumentException(UiText.T("工作区处理方式无效。", "The workspace processing mode is invalid."));
        if (Presets is null || Presets.Count > 200) throw new ArgumentException(UiText.T("工作区预设数量无效。", "The workspace preset count is invalid."));
        if (Language is not (UiText.Chinese or UiText.English)) Language = UiText.Chinese;
        if (Theme is not ("dark" or "light")) Theme = "dark";
    }
}
