using MicStudio.Apo;
using MicStudio.Core;

namespace MicStudio.App;

public partial class MainWindow
{
    private string DiagnosticSummary()
    {
        var permission = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "管理员" : "普通权限";
        return $"Mic Studio 0.1 · 本地诊断摘要\n时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}\n系统：{Environment.OSVersion.VersionString}\n权限：{permission}\n\n" +
            $"工作路线：{(LocalMode ? "本地 DSP" : "APO 直接输入")}\n已连接录音设备：{inputs.Count(d => d.IsActive)}\n所选设备：{(SelectedInput?.IsActive == true ? "已连接" : "未连接")}\n采样率：{SampleRate} Hz\n" +
            $"APO：{(apo.Found ? "组件已检测 " + apo.Version : "未检测到")}\nAPO 音频生效：待验证\nCABLE 发送端：{outputs.Count(d => d.IsActive && d.IsCableSend)}\nCABLE 接收端：{inputs.Count(d => d.IsActive && d.IsCableReceive)}\n" +
            $"本地采集：{(engine.IsRunning ? "运行中" : "关闭")}\n录音：{(engine.IsRecording ? "进行中" : "关闭")}\n渲染缓冲重置计数：{engine.OutputBufferResets}\n" +
            $"当前测量：{(lastMeter == null ? "—" : "输入峰值 " + lastMeter.InputPeakDb.ToString("0.0") + " dBFS / 本地输出 " + lastMeter.OutputPeakDb.ToString("0.0") + " dBFS")}\n" +
            $"算法缓冲：{(LocalMode && applied.Denoise.Enabled ? 512000.0 / SampleRate : 0):0.00} ms（仅降噪算法，不是端到端延迟）\n" +
            "接收软件音频：待实际确认\n\n" +
            "已脱敏：用户名、设备名称与唯一标识、路径与录音。\n此摘要不构成音质或接收软件验收。";
    }
    private void BuildDiagnostics()
    {
        Header("KNOW YOUR SIGNAL / 06", "状态真实，问题才有线索。", "配置保存成功、组件检测成功与声音实际生效分别记录。导出前可以预览脱敏内容。", false);
        Add(Card("当前会话", "不会把队列长度或算法帧长标成实测端到端延迟。", T(DiagnosticSummary(), 12, Muted), Buttons(B("刷新摘要", () => { RefreshDevices(); apo = ApoInstallation.Detect(); ShowPage(5); }, true), B("预览并导出诊断", () =>
        {
            var summary = DiagnosticSummary();
            ShowTextDialog("脱敏诊断预览", summary, "选择位置保存", () =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "MicStudio-diagnostics.txt", Filter = "诊断摘要|*.txt" };
                if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, summary); Notice("脱敏诊断已保存本机，未发送到任何服务。"); }
            });
        }))));
        Add(Card("麦克风无法使用", "先定位错误，不把所有问题归因于管理员权限。", T("1  点击刷新，确认所选 USB 麦克风仍已连接。\n2  在 Windows 麦克风访问设置中检查桌面应用访问权限。\n3  关闭独占使用麦克风的软件，再主动测试。\n4  在设置页请求管理员启动，对比普通权限与管理员设备列表和错误码。", 12, Muted), Buttons(B("Windows 声音设置", () => Open("ms-settings:sound")), B("麦克风访问设置", () => Open("ms-settings:privacy-microphone")))));
        Add(Card("其他软件没有声音", null, T("虚拟输入：确认正在处理，接收软件的输入选择 CABLE Output。\n系统 APO：接收软件仍选原麦克风，并确认其经过系统效果。", 13, Muted), B("设备", () => ShowPage(4))));
        Add(Card("恢复与冲突", "只处理 Mic Studio 自有挂接，保留外部新增配置。", T("APO 预览后若配置被其他软件修改，应用会停止并提示重新预览。Peace 或无法可靠解释的复杂配置需通过官方编辑器处理。原始设置损坏时保留恢复副本，不会静默覆盖。", 12, Muted), B("检查自有 APO 挂接", () => ShowPage(4))));
    }
    private void BuildSettings()
    {
        Header("MAKE IT YOURS / 07", "工作台，也按你的习惯。", "参数本地保存，启动不自动开启麦克风、录音、监听或虚拟线路。", false);
        Add(Card("外观", "石墨色与浅色主题；界面随窗口宽度折叠导航。", Buttons(B("石墨色", () => { State.Theme = "dark"; ApplyTheme(); SaveSoon(); ShowPage(6); }), B("浅色", () => { State.Theme = "light"; ApplyTheme(); SaveSoon(); ShowPage(6); })), T("界面语言：简体中文。英文界面尚未提供。", 11, Muted)));
        Add(Card("关闭行为", "默认关闭窗口即停止本地音频。APO 系统配置可能继续生效。", Toggle("关闭到托盘，保持当前本地音频运行", State.MinimizeToTray, value => { State.MinimizeToTray = value; if (value) EnsureTray(); SaveSoon(); }), T("启用后，托盘菜单可随时停止音频或退出。程序重新启动仍不会自动采集。", 11, Muted), B("立即停止本地音频", StopAudio)));
        Add(Card("管理员操作", "日常使用普通权限。只有安装/绑定组件或写受保护 APO 文件才需要 UAC。", B("请求管理员启动", RestartElevated), T("重新启动会停止采集并保留草稿。Windows UAC 由你确认；取消后继续使用当前窗口。不会放宽 Program Files 权限。", 11, Muted)));
        Add(Card("本地数据", "预设与草稿不上传；私人录音由用户自行选择保存位置。", Buttons(B("打开个人数据目录", () => { Directory.CreateDirectory(StudioViewModel.DataDirectory); Open(StudioViewModel.DataDirectory); }), B("另存当前声音", SavePreset)), T("快捷键：Ctrl+Z 撤销、Ctrl+Y 重做、Ctrl+S 另存预设。编辑数值后按 Enter 或移开焦点提交。", 11, Muted)));
        Add(Card("组件", "NAudio 2.3.0 · .NET 10 · Windows WASAPI", Buttons(B("驱动管理", () => ShowPage(4)), B("Equalizer APO", () => Open("https://sourceforge.net/p/equalizerapo/wiki/Documentation/")))));
    }
}
