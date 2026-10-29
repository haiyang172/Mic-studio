using MicStudio.Core;
using Microsoft.Win32;

namespace MicStudio.App;

/// <summary>
/// Per-user start-at-sign-in entry. The workspace stores the user's intent; the
/// registry stores the actual command. Applying the intent again repairs a stale
/// path after an update or moving a portable folder.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool TryGetRegistered(out string? command, out string? error)
    {
        command = null; error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            command = key?.GetValue(StartupCommand.ValueName) as string;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Reads whether this executable is currently registered for tray startup.</summary>
    public static bool IsCurrentExecutableRegistered()
    {
        var executable = Environment.ProcessPath;
        if (executable is null || !string.Equals(Path.GetFileName(executable), "MicStudio.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryGetRegistered(out var command, out _)) return false;
        return StartupCommand.Matches(command, executable);
    }

    /// <summary>Creates or removes the Run value for this executable.</summary>
    public static bool TryApply(bool enabled, out string? error)
    {
        error = null;
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException(UiText.T("无法取得程序路径。", "The program path is unavailable."));
            if (!string.Equals(Path.GetFileName(executable), "MicStudio.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(UiText.T("只有 MicStudio.exe 可以注册登录启动。", "Only MicStudio.exe can register for sign-in startup."));
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true) ?? throw new InvalidOperationException(UiText.T("无法打开当前用户的启动项注册表。", "The per-user startup registry key could not be opened."));
            if (enabled) key.SetValue(StartupCommand.ValueName, StartupCommand.Build(executable), RegistryValueKind.String);
            else key.DeleteValue(StartupCommand.ValueName, false);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Removes only a Run value that points at this installed executable.</summary>
    public static void RemoveIfOwned(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key?.GetValue(StartupCommand.ValueName) is string command && StartupCommand.Matches(command, executablePath))
                key.DeleteValue(StartupCommand.ValueName, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            // Uninstall reports its main cleanup result; a missing Run entry must not
            // block removal of the program directory.
        }
    }
}
