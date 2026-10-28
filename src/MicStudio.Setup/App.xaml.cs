using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MicStudio.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] is "--self-test" or "--validate-payload")
        {
            try
            {
                object result;
                if (e.Args[0] == "--self-test") result = InstallerEngine.SelfTest();
                else
                {
                    var inspection = InstallerEngine.InspectEmbeddedPayload();
                    if (!inspection.PayloadPresent || !inspection.AppExecutableFound) throw new InvalidDataException("安装器缺少嵌入的 MicStudio.exe 程序文件。");
                    result = inspection;
                }
                File.WriteAllText(e.Args[1], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(e.Args[1], JsonSerializer.Serialize(new { error = ex.ToString() })); Shutdown(1); }
            return;
        }
        bool uninstall = e.Args.Contains("--uninstall") || e.Args.Contains("--uninstall-worker");
        if (uninstall && InstallerEngine.IsInsideInstall(Environment.ProcessPath ?? ""))
        {
            try
            {
                // Published setup is a self-contained single executable. Run a copy outside the owned tree before removal.
                var temporary = Path.Combine(Path.GetTempPath(), "MicStudio-Uninstall-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary);
                var helper = Path.Combine(temporary, "MicStudio.Setup.exe");
                File.Copy(Environment.ProcessPath!, helper, false);
                Process.Start(new ProcessStartInfo(helper, "--uninstall-worker") { UseShellExecute = true });
                Shutdown(0);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Mic Studio · 无法启动卸载", MessageBoxButton.OK, MessageBoxImage.Warning); Shutdown(1); }
            return;
        }
        MainWindow = new SetupWindow(uninstall);
        MainWindow.Show();
        if (e.Args.Length == 2 && e.Args[0] == "--ui-smoke")
        {
            var window = MainWindow;
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await Task.Delay(200);
                    Directory.CreateDirectory(e.Args[1]);
                    var reports = new List<object>();
                    foreach (var size in new[] { (Width: 680.0, Height: 610.0), (Width: 460.0, Height: 440.0) })
                    {
                        window.Width = size.Width; window.Height = size.Height; window.UpdateLayout();
                        await Task.Delay(60);
                        var scale = VisualTreeHelper.GetDpi(window);
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * scale.DpiScaleY), scale.PixelsPerInchX, scale.PixelsPerInchY, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var output = File.Create(Path.Combine(e.Args[1], "setup-" + (int)size.Width + ".png"))) encoder.Save(output);
                        reports.Add(new { width = window.ActualWidth, height = window.ActualHeight, dpiX = scale.PixelsPerInchX, installActionInvoked = false });
                    }
                    File.WriteAllText(Path.Combine(e.Args[1], "layout.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
                    Shutdown(0);
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); Shutdown(1); }
            };
        }
    }
}
