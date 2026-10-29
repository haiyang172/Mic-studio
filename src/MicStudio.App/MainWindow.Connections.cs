using System.Threading;
using MicStudio.Apo;
using MicStudio.Components;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private readonly DriverManager driverManager = new();
    private readonly CancellationTokenSource lifetime = new();
    private DriverUpdateInfo? driverUpdate;
    private TextBlock? driverStatusText;

    private void BuildConnections()
    {
        Header("", "设备", "", false);
        Add(Buttons(B("刷新", () => { RefreshDevices(); apo = ApoInstallation.Detect(); ShowPage(4); }, true), B("Windows 声音设置", () => Open("ms-settings:sound"))));
        var send = outputs.Where(d => d.IsActive && d.IsCableSend).ToList();
        var receive = inputs.Where(d => d.IsActive && d.IsCableReceive).ToList();
        var status = driverManager.Detect(send.Count > 0, receive.Count > 0);
        var package = driverManager.GetLocalPackage();
        var driverDescription = Loc.English
            ? (status.InputEndpointPresent && status.OutputEndpointPresent
                ? "CABLE Input and Output endpoints were found; the actual route still needs a test. Package numbers are not compared with the installed driver version."
                : "VB-CABLE endpoints are incomplete; a restart or refresh may be needed after installation. Package numbers are not compared with the installed driver version.")
            : status.Message;
        driverStatusText = T(driverUpdate is null ? "" : Loc.F("官网安装包：Pack {0}", "Official package: Pack {0}", driverUpdate.AvailablePackNumber), 13, Accent);
        Add(Card("虚拟输入驱动", driverDescription,
            T(Loc.F("VB-CABLE：{0}   {1}", "VB-CABLE: {0}   {1}", status.ServicePresent ? Loc.T("已安装", "Installed") : Loc.T("未检测到安装", "Not installed"), status.DriverVersion), 14),
            T(Loc.F("发送端：{0}    接收端：{1}", "Send: {0}    Receive: {1}", send.Count > 0 ? Loc.T("正常", "Ready") : Loc.T("未检测到", "Not found"), receive.Count > 0 ? Loc.T("正常", "Ready") : Loc.T("未检测到", "Not found")), 13, send.Count > 0 && receive.Count > 0 ? Accent : Amber),
            T(package is null ? Loc.T("本地安装包：未找到", "Local package: not found") : Loc.F("本地安装包：Pack {0}", "Local package: Pack {0}", package.PackNumber), 13, Muted),
            driverStatusText,
            Buttons(AsyncB("检查更新", async () =>
            {
                driverUpdate = await driverManager.CheckForUpdatesAsync(lifetime.Token);
                if (closed) return;
                driverStatusText.Text = Loc.F("官网：Pack {0} · {1}（已安装版本需另行核对）", "Official: Pack {0} · {1} (installed version needs separate verification)", driverUpdate.AvailablePackNumber, driverUpdate.PackageUpdateAvailable ? Loc.T("可下载新包", "New package available") : Loc.T("本地包编号一致", "Local package number matches"));
            }), AsyncB("重新下载", async () =>
            {
                var downloaded = await driverManager.DownloadLatestAsync(true, lifetime.Token);
                if (closed) return;
                driverStatusText.Text = Loc.F("已下载 Pack {0}，待安装。", "Downloaded Pack {0}; ready to install.", downloaded.PackNumber);
            }), AsyncB("安装 / 更新 / 重装", async () =>
            {
                StopAudio();
                var local = driverManager.GetLocalPackage();
                if (local is null || driverUpdate is { PackageUpdateAvailable: true }) local = await driverManager.DownloadLatestAsync(false, lifetime.Token);
                if (closed) return;
                var installer = await Task.Run(() => driverManager.PrepareInstaller(local), lifetime.Token);
                if (closed) return;
                if (!installer.SignatureValid) { Notice(installer.Message); return; }
                var result = driverManager.LaunchInstaller(local);
                Notice(result.Message);
                driverStatusText.Text = result.Success ? Loc.T("官方安装器已打开；安装完成后刷新，必要时重启。", "The official installer is open. Refresh after it finishes; restart Windows if needed.") : result.Message;
            })),
            Buttons(B("VB-CABLE · VB-Audio · Donationware", () => Open(DriverManager.AttributionUrl)), B("支持驱动作者", () => Open(DriverManager.DonationUrl)))));
        var route = SelectCableRoute();
        var routeControls = new StackPanel();
        routeControls.Children.Add(T(route.IsReady ? Loc.T("虚拟线路：自动连接就绪", "Virtual cable: automatically connected") : RouteMessage(route), 14, route.IsReady ? Accent : Amber));
        if (route.IsReady) routeControls.Children.Add(T(Loc.T("其他软件的输入选：", "In the other app, select input: ") + route.Receive!.Name, 14, Accent));
        var completeSends = send.Where(d => CableRouteResolver.Resolve(inputs, outputs, d.Id).Send?.Id == d.Id).ToList();
        if (completeSends.Count > 1)
            routeControls.Children.Add(new Expander { Header = Loc.T("更换虚拟线路", "Change virtual cable"), Foreground = Ink, Content = DevicePicker(completeSends, State.CableOutputId, id => { if (engine.IsRunning) StopAudio(); State.CableOutputId = id; InvalidateDiscord(); SaveSoon(); ShowPage(4); }), Margin = new(0, 0, 0, 12) });
        routeControls.Children.Add(Buttons(B("开始处理并发送", () => { if (!LocalMode) { ModePicker.SelectedIndex = 1; } StartAudio(true); }, true), B("停止", StopAudio), B("复制输入名称", () => { if (route.Receive != null) Clipboard.SetText(route.Receive.Name); else Notice("虚拟线路尚未就绪。"); })));
        Add(Card("用作其他软件的输入", "自动选择匹配的 CABLE 收发端；不会修改其他软件或 Windows 默认输入。", routeControls));
        var physical = outputs.Where(d => d.IsActive && !AudioDeviceService.IsVirtualCableEndpoint(d.Name)).ToList();
        Add(Card("耳机监听", "仅选择并佩戴物理耳机后开启，监听额外衰减 12 dB；更改监听设置会停止当前音频。", DevicePicker(physical, State.PlaybackOutputId, id => { State.PlaybackOutputId = id; monitorId = id; if (engine.IsRunning) StopAudio(); SaveSoon(); }),
            Toggle("已佩戴耳机", headphones, v => { headphones = v; if (!v) { monitor = false; if (engine.IsRunning) StopAudio(); } }),
            Toggle("开启监听（下次启动生效）", monitor, v =>
            {
                if (v && (!headphones || State.PlaybackOutputId is null)) { Notice("先选择耳机并确认佩戴。"); ShowPage(4); return; }
                monitor = v; monitorId = State.PlaybackOutputId; if (engine.IsRunning) StopAudio();
            })));
        var advanced = new StackPanel();
        advanced.Children.Add(T(apo.Found ? Loc.F("Equalizer APO {0}", "Equalizer APO {0}", apo.Version) : Loc.T("Equalizer APO 未检测到", "Equalizer APO not found"), 14, apo.Found ? Accent : Amber));
        advanced.Children.Add(Buttons(B("获取 APO", () => Open("https://sourceforge.net/projects/equalizerapo/")), B("设备绑定", () => { if (apo.ConfiguratorPath is null) { Notice("未检测到 APO 设备配置工具。"); return; } Open(apo.ConfiguratorPath); }), B("官方编辑器", () => { if (apo.EditorPath is null) { Notice("未检测到 APO 编辑器。"); return; } Open(apo.EditorPath); }), B("预览 / 应用 EQ", () => { if (LocalMode) { Notice("先在顶部选择系统输入 · APO。"); return; } ApplyDraft(); }), B("移除自有挂接", () => { StopAudio(); var result = new ApoConfigService(apo).RemoveOwnedConfiguration(); Notice(result.Message); InvalidateDiscord(); })));
        advanced.Children.Add(T("APO 路线只应用增益与 EQ；需要绑定到目标录音端点。关闭软件后系统效果仍可能存在。", 12, Muted));
        Add(new Expander { Header = Loc.T("系统 APO（高级）", "System APO (advanced)"), Foreground = Ink, Content = advanced, Margin = new(0, 8, 0, 12) });
    }

    private System.Windows.Controls.Button AsyncB(string label, Func<Task> action)
    {
        var button = B(label, () => { });
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { if (!closed) Notice("操作已取消。"); }
            catch (Exception ex) { if (!closed) Notice(Loc.T("操作未完成：", "Action did not complete: ") + ex.Message); }
            finally { if (!closed) button.IsEnabled = true; }
        };
        return button;
    }
    private ComboBox DevicePicker(IEnumerable<AudioDeviceInfo> candidates, string? selectedId, Action<string?> change)
    {
        var items = candidates.ToList();
        var combo = new ComboBox { ItemsSource = items, DisplayMemberPath = "DisplayName", Margin = new(0, 4, 0, 14), HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 720 };
        combo.SelectedItem = items.FirstOrDefault(d => d.Id == selectedId);
        combo.SelectionChanged += (_, _) => { if (!building && combo.SelectedItem is AudioDeviceInfo d) change(d.Id); }; return combo;
    }
}
