using Microsoft.Win32;

namespace MicStudio.App;

public partial class MainWindow
{
    private void InitializeTrayBranding() => SystemEvents.UserPreferenceChanged += OnTrayPreferencesChanged;

    private void DisposeTrayBranding() => SystemEvents.UserPreferenceChanged -= OnTrayPreferencesChanged;

    private void OnTrayPreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try { Dispatcher.BeginInvoke(() =>
        {
            if (closed || tray == null) return;
            var replacement = LoadTrayIcon();
            try { tray.Icon = replacement; }
            catch { replacement.Dispose(); throw; }
            var previous = trayIcon;
            trayIcon = replacement;
            previous?.Dispose();
        }); }
        catch (InvalidOperationException) when (closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var lightBackground = false;
        if (SystemParameters.HighContrast)
        {
            var background = SystemColors.ControlColor;
            lightBackground = .2126 * background.R + .7152 * background.G + .0722 * background.B > 127;
        }
        else
        {
            try
            {
                lightBackground = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value == 1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A restricted or absent theme setting uses the light glyph for a dark taskbar.
            }
        }
        var name = lightBackground ? "mic-studio-tray-light.ico" : "mic-studio-tray-dark.ico";
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/MicStudio;component/Assets/" + name))
            ?? throw new InvalidOperationException(Loc.T("缺少 Mic Studio 托盘图标资源。", "The Mic Studio tray icon resource is missing."));
        var size = System.Windows.Forms.SystemInformation.SmallIconSize;
        using var stream = resource.Stream;
        using var source = new System.Drawing.Icon(stream, Math.Clamp(size.Width, 16, 64), Math.Clamp(size.Height, 16, 64));
        return (System.Drawing.Icon)source.Clone();
    }
}
