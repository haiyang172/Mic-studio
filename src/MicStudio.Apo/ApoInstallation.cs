using System.Diagnostics;
using Microsoft.Win32;

namespace MicStudio.Apo;

public sealed record ApoInstallation(bool Found, string InstallDirectory, string ConfigDirectory,
    string Version, string? ConfiguratorPath, string? EditorPath, string Message)
{
    public static ApoInstallation Detect()
    {
        var candidates = new List<(string Install, string? Config)>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
                var config = key?.GetValue("ConfigPath") as string;
                var install = key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(install)) candidates.Add((install, config));
                if (!string.IsNullOrWhiteSpace(config))
                    candidates.Add((Directory.GetParent(config.TrimEnd('\\', '/'))?.FullName ?? "", config));
                using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EqualizerAPO");
                var location = uninstall?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(location)) candidates.Add((location, config));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (!string.IsNullOrEmpty(root))
                foreach (var name in new[] { "EqualizerAPO", "Equalizer APO" })
                    candidates.Add((Path.Combine(root, name), null));

        foreach (var candidate in candidates)
        {
            try
            {
                var install = Path.GetFullPath(candidate.Install);
                var configurator = Path.Combine(install, "Configurator.exe");
                var editor = Path.Combine(install, "Editor.exe");
                if (!File.Exists(configurator) && !File.Exists(editor)) continue;
                var config = Path.GetFullPath(candidate.Config ?? Path.Combine(install, "config"));
                var version = FileVersionInfo.GetVersionInfo(File.Exists(configurator) ? configurator : editor).FileVersion ?? "未知版本";
                return new(true, install, config, version, File.Exists(configurator) ? configurator : null,
                    File.Exists(editor) ? editor : null, "已检测到组件；麦克风绑定及音频生效仍待验证。");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return new(false, "", "", "—", null, null, "未检测到 Equalizer APO。可先编辑草稿，或使用原生处理线路。");
    }

    /// <summary>Finds a registered endpoint effect that explicitly names Equalizer APO. This is evidence of registration only.</summary>
    public static string GetCaptureBindingEvidence(string captureEndpointId)
    {
        var guid = ApoConfigGenerator.ParseCaptureGuid(captureEndpointId);
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var endpoint = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\" + guid.ToString("B"));
            if (endpoint is null) return "注册表中未找到该录音端点；不能据此确认 APO 绑定。";
            using var effects = endpoint.OpenSubKey("FxProperties");
            if (effects is null) return "未检测到端点效果注册；请在 APO Configurator 核对。";
            foreach (var valueName in effects.GetValueNames())
            {
                if (effects.GetValue(valueName) is not string value || !Guid.TryParse(value, out var clsid)) continue;
                using var com = machine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + clsid.ToString("B") + @"\InprocServer32");
                if ((com?.GetValue("") as string)?.Contains("EqualizerAPO", StringComparison.OrdinalIgnoreCase) == true)
                    return "已发现 Equalizer APO 端点效果注册；当前采集路径及 Discord 仍待验证。";
            }
            return "存在端点效果注册，但未确认 Equalizer APO；请用 Configurator 核对。";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { return "无法读取端点效果注册；" + ex.GetType().Name + "。音频生效状态未知。"; }
    }
}
