namespace MicStudio.Core;

/// <summary>
/// The per-user Run entry starts the same executable in background-tray mode.
/// Keeping the command construction pure makes quoting and argument handling testable
/// without reading or writing the real registry.
/// </summary>
public static class StartupCommand
{
    public const string MinimizedArgument = "--minimized";
    public const string ValueName = "MicStudio.Next";

    public static string Build(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Any(char.IsControl) || executablePath.Contains('"'))
            throw new ArgumentException(UiText.T("启动命令的可执行文件路径无效。", "The startup command's executable path is invalid."), nameof(executablePath));
        if (!Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException(UiText.T("启动命令需要完整的可执行文件路径。", "The startup command needs a fully qualified executable path."), nameof(executablePath));
        if (!string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(UiText.T("启动命令只接受 .exe 文件。", "The startup command only accepts an .exe file."), nameof(executablePath));
        // A per-user Run value may launch the executable directly. The path is always
        // quoted so directories containing spaces stay one token.
        return "\"" + executablePath + "\" " + MinimizedArgument;
    }

    public static bool IsMinimizedLaunch(IReadOnlyList<string>? arguments) =>
        arguments is { Count: 1 } && string.Equals(arguments[0], MinimizedArgument, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when an existing Run value points at this executable in tray mode.</summary>
    public static bool Matches(string? registeredCommand, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(registeredCommand)) return false;
        try { return string.Equals(registeredCommand.Trim(), Build(executablePath), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
}
