using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MicStudio.Setup;

public sealed class SetupWindow : Window
{
    private readonly bool uninstall;
    private readonly TextBlock status;
    private readonly ProgressBar progress;
    private readonly Button action, close, launch;
    private readonly CheckBox desktop;
    private bool busy;
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    public SetupWindow(bool uninstall)
    {
        this.uninstall = uninstall;
        Title = uninstall ? "Mic Studio · 卸载" : "Mic Studio · 安装";
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/MicStudio.Setup;component/Assets/mic-studio.ico"));
        Width = 680; Height = 610; MinWidth = 460; MinHeight = 420;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#101518"); Foreground = Brush("#E8F0EF"); FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
        var root = new Grid { Margin = new Thickness(30) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        var brandImage = new Image { Source = BitmapFrame.Create(new Uri("pack://application:,,,/MicStudio.Setup;component/Assets/mic-studio.png")), Width = 32, Height = 32, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(brandImage, BitmapScalingMode.HighQuality);
        brand.Children.Add(brandImage);
        brand.Children.Add(new TextBlock { Text = "Mic Studio", Foreground = Brush("#71D6BF"), FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(brand);
        content.Children.Add(new TextBlock { Text = uninstall ? "卸载 Mic Studio" : "让每一句话，更清楚。", FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 28, 0, 10), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = uninstall ? "移除清单内未改动的程序文件及本安装器创建的快捷方式；保留额外或已修改的文件。" : $"Windows 原生麦克风工作台 · {InstallerEngine.ProductVersion} 预览版\n增益、八段 EQ、人声效果与本地预设。", Foreground = Brush("#A4B4B5"), TextWrapping = TextWrapping.Wrap, LineHeight = 25 });
        var card = new Border { Background = Brush("#1B2327"), CornerRadius = new CornerRadius(10), Padding = new Thickness(18), Margin = new Thickness(0, 24, 0, 18) };
        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = uninstall ? "卸载位置" : "安装位置 · 仅当前用户", FontWeight = FontWeights.SemiBold });
        details.Children.Add(new TextBlock { Text = InstallerEngine.InstallDirectory, Foreground = Brush("#A4B4B5"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        details.Children.Add(new TextBlock { Text = uninstall ? "保留个人预设、设置、录音、旧版程序以及独立的 APO / VB-CABLE。目录中存在额外或改动的文件时会保留目录与标记。没有文件清单的旧版安装会停止自动清理。系统 APO 不会自动撤销，请先在主程序使用恢复入口。" : "无需管理员权限或另装 .NET。已捆绑 VB-CABLE 官方驱动原包；安装软件后可在设备页面主动打开驱动安装器并确认 UAC。首次启动默认关闭麦克风。", Foreground = Brush("#A4B4B5"), TextWrapping = TextWrapping.Wrap, LineHeight = 23 });
        if (!uninstall)
        {
            details.Children.Add(new TextBlock { Text = "VB-CABLE 由 VB-Audio 提供，是 Donationware；欢迎向原作者捐赠或购买许可。", Foreground = Brush("#A4B4B5"), TextWrapping = TextWrapping.Wrap, LineHeight = 23, Margin = new Thickness(0, 12, 0, 6) });
            var links = new TextBlock { TextWrapping = TextWrapping.Wrap };
            foreach (var item in new[] { (Label: "VB-CABLE 官方来源", Url: "https://vb-cable.com"), (Label: "支持作者 / 许可说明", Url: "https://vb-audio.com/Services/licensing.htm") })
            {
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(item.Label)) { NavigateUri = new Uri(item.Url), Foreground = Brush("#71D6BF") };
                link.RequestNavigate += (_, args) => { try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开官方页面"); } args.Handled = true; };
                links.Inlines.Add(link); links.Inlines.Add(new System.Windows.Documents.Run("     "));
            }
            details.Children.Add(links);
        }
        card.Child = details; content.Children.Add(card);
        desktop = new CheckBox { Content = "创建桌面快捷方式", IsChecked = true, Margin = new Thickness(0, 0, 0, 16), Foreground = Foreground, Visibility = uninstall ? Visibility.Collapsed : Visibility.Visible };
        content.Children.Add(desktop);
        progress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Value = 0, Foreground = Brush("#71D6BF"), Background = Brush("#273236"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 4, 0, 12) };
        content.Children.Add(progress);
        status = new TextBlock { Text = uninstall ? "关闭已安装的 Mic Studio 后即可卸载。个人数据会保留。" : "准备安装。使用软件前仍需实际麦克风与 Discord 验证。", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A4B4B5"), LineHeight = 23 };
        content.Children.Add(status);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        close = Button("关闭", () => Close(), false);
        launch = Button("打开 Mic Studio", OpenInstalled, false); launch.Visibility = Visibility.Collapsed;
        action = Button(uninstall ? "卸载" : "安装 Mic Studio", BeginOperation, true);
        buttons.Children.Add(close); buttons.Children.Add(launch); buttons.Children.Add(action);
        Grid.SetRow(buttons, 1); root.Children.Add(buttons); Content = root;
        Closing += (_, args) => { if (busy) args.Cancel = true; };
    }

    private Button Button(string text, Action handler, bool primary)
    {
        var button = new Button { Content = text, Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(8, 4, 0, 4), Background = Brush(primary ? "#71D6BF" : "#273236"), Foreground = Brush(primary ? "#0B1E18" : "#E8F0EF"), BorderThickness = new Thickness(0), MinWidth = 80, FontWeight = FontWeights.SemiBold };
        button.Click += (_, _) => handler(); return button;
    }

    private async void BeginOperation()
    {
        busy = true; action.IsEnabled = false; close.IsEnabled = false; desktop.IsEnabled = false;
        var feedback = new Progress<InstallProgress>(p => { progress.Value = p.Percent; status.Text = p.Message; });
        try
        {
            bool shortcut = desktop.IsChecked == true;
            var result = await Task.Run(() => uninstall ? InstallerEngine.Uninstall(feedback) : InstallerEngine.Install(shortcut, feedback));
            progress.Value = 100; status.Text = result;
            action.Visibility = Visibility.Collapsed;
            if (!uninstall) launch.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            status.Text = "操作未完成：" + ex.Message; progress.Value = 0; action.IsEnabled = true;
        }
        finally { busy = false; close.IsEnabled = true; desktop.IsEnabled = true; }
    }

    private void OpenInstalled()
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(InstallerEngine.InstallDirectory, "MicStudio.exe")) { UseShellExecute = true }); Close(); }
        catch (Exception ex) { status.Text = "无法打开：" + ex.Message; }
    }
}
