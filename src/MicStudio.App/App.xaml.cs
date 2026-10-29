using System.Text.Json;
using MicStudio.Core;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--ui-smoke")
        {
            // Diagnostic control tests must never overwrite the user's real workspace.
            var isolated = Path.Combine(Path.GetFullPath(e.Args[1]), "test-state-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("MICSTUDIO_DATA_DIR", isolated);
        }
        DispatcherUnhandledException += (_, ev) => { MessageBox.Show(ev.Exception.Message, Loc.T("Mic Studio · 操作未完成", "Mic Studio · Action not completed"), MessageBoxButton.OK, MessageBoxImage.Warning); ev.Handled = true; };
        if (e.Args.Length == 2 && e.Args[0] == "--startup-smoke")
        {
            try
            {
                var applied = StartupRegistration.TryApply(true, out var applyError) && StartupRegistration.IsCurrentExecutableRegistered();
                var removed = StartupRegistration.TryApply(false, out var removeError) && !StartupRegistration.IsCurrentExecutableRegistered();
                File.WriteAllText(e.Args[1], JsonSerializer.Serialize(new { applied, removed, applyError, removeError, valueName = StartupCommand.ValueName, executable = Environment.ProcessPath }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(applied && removed ? 0 : 1);
            }
            catch (Exception ex) { File.WriteAllText(e.Args[1], ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--diagnostics")
        {
            try
            {
                using var devices = new AudioDeviceService();
                File.WriteAllText(e.Args[1], JsonSerializer.Serialize(new { os = Environment.OSVersion.ToString(), isAdministrator = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator), inputs = devices.EnumerateInputs().Select(d => new { d.Name, d.State, d.IsDefaultConsole, d.IsDefaultCommunications, d.SampleRate, d.Channels, d.IsCableReceive }), outputs = devices.EnumerateOutputs().Select(d => new { d.Name, d.State, d.SampleRate, d.Channels, d.IsCableSend }) }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(e.Args[1], ex.ToString()); Shutdown(1); }
            return;
        }
        var minimized = StartupCommand.IsMinimizedLaunch(e.Args);
        var window = new MainWindow(minimized);
        MainWindow = window;
        if (!minimized) window.Show();
        if (e.Args.Length == 2 && e.Args[0] == "--ui-smoke")
        {
            window.Loaded += async (_, _) => { await Task.Delay(200); try { await window.RunLayoutSmoke(e.Args[1]); Shutdown(0); } catch (Exception ex) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); Shutdown(1); } };
        }
    }
}
