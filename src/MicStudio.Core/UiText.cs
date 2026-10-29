using System.Globalization;

namespace MicStudio.Core;

/// <summary>
/// Process-wide interface language. Data files keep stable identifiers; this only
/// changes how text is presented. Callers that build WPF elements rebuild them after
/// <see cref="Set"/> so no control keeps a stale translation.
/// </summary>
public static class UiText
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    public static string Current { get; private set; } = Chinese;
    public static bool IsEnglish => Current == English;
    public static event Action? Changed;

    public static bool IsSupported(string? language) => language is Chinese or English;

    public static void Set(string? language)
    {
        var normalized = language == English ? English : Chinese;
        if (normalized == Current) return;
        Current = normalized;
        Changed?.Invoke();
    }

    public static string T(string chinese, string english) => IsEnglish ? english : chinese;

    public static string F(string chinese, string english, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, IsEnglish ? english : chinese, arguments);
}
