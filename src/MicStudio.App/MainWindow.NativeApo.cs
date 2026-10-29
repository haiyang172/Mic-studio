using System.Security.Principal;
using System.Text.Json;
using MicStudio.Apo;
using MicStudio.NativeApo;

namespace MicStudio.App;

public partial class MainWindow
{
    private readonly NativeApoDeploymentService nativeDeployment = new();
    private readonly DispatcherTimer nativeLeaseTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private NativeApoParameterWriter? nativeWriter;
    private bool pendingNativeSettings;
    private bool NativeMode => State.Mode == 2;
    private bool ProcessingActive => engine.IsRunning || nativeWriter != null;
    private static string NativeApoDll => Path.Combine(AppContext.BaseDirectory, "components", "MicStudioAPO", "MicStudioApo.dll");

    private bool TryNativeEndpoint(out Guid endpoint)
    {
        try { endpoint = ApoConfigGenerator.ParseCaptureGuid(State.InputDeviceId ?? ""); return true; }
        catch (ArgumentException) { endpoint = Guid.Empty; return false; }
    }

    private void StartNativeProcessing()
    {
        if (SelectedInput is not { IsActive: true } || !TryNativeEndpoint(out var endpoint))
            throw new InvalidOperationException(Loc.T("请选择已连接的麦克风。", "Select a connected microphone."));
        var status = nativeDeployment.Inspect(NativeApoDll, endpoint);
        if (!status.Component.Trusted || !status.Registered || !status.Bound || !status.ParametersReady || !status.EndpointAvailable)
            throw new InvalidOperationException(Loc.T("自研 APO 尚未完成签名安装和设备绑定，请在设备页查看组件状态。", "The native APO needs signed installation and endpoint binding. Review its status on Devices."));
        if (apo.Found)
        {
            var legacy = Path.Combine(apo.ConfigDirectory, "config.txt");
            if (File.Exists(legacy) && File.ReadAllText(legacy).Contains(ApoConfigService.BeginMarker, StringComparison.Ordinal))
                throw new InvalidOperationException(Loc.T("请先移除 Mic Studio 旧系统 APO 挂接，避免两套增益和 EQ 重复处理。", "Remove the own legacy system APO hook first to avoid duplicate gain and EQ."));
        }
        if (engine.IsRunning) StopAudio();
        StopNativeProcessing();
        nativeWriter = NativeApoParameterWriter.OpenExisting(endpoint);
        try { WriteNativeSettings(); }
        catch { nativeWriter.Dispose(); nativeWriter = null; throw; }
        nativeLeaseTimer.Start();
        applyState = Loc.T("系统参数已发送 · 音频待验证", "System parameters sent · audio unverified");
        UpdateChrome();
        var excluded = NativeApoParameterCodec.GetUnsupportedEffects(Draft);
        Notice(Loc.T("自研 APO 预览仅处理增益、EQ 和限幅；系统实际声音仍需验证。外放回声消除和啸叫保护请使用虚拟输入路线。", "The native APO preview handles gain, EQ and limiting; actual system audio still needs verification. Use the virtual-input route for speaker echo cancellation and feedback protection.")
            + (excluded.Count > 0 ? Loc.T(" 当前本地效果未写入自研 APO。", " Current local effects were not sent to the native APO.") : ""));
    }

    private void WriteNativeSettings() => nativeWriter?.Write(Draft, SampleRate, Math.Clamp(SelectedInput?.Channels ?? 2, 1, 8));

    private void StopNativeProcessing()
    {
        nativeLeaseTimer.Stop(); pendingNativeSettings = false;
        var writer = nativeWriter; nativeWriter = null;
        if (writer == null) return;
        applyState = Loc.T("自研 APO 参数连接已停止", "Native APO parameter control stopped");
        try { writer.Disable(); }
        catch (Exception) { if (!closed) Notice(Loc.T("未能写入停用状态，当前控制器已停止心跳。请检查是否有其他控制器；无人续期的租约会在 3 秒内失效。", "Disable could not be written and this controller stopped its heartbeat. Check for another controller; an unrenewed lease expires within 3 seconds.")); }
        finally { writer.Dispose(); }
    }

    private void RefreshNativeLease()
    {
        if (nativeWriter == null) return;
        try
        {
            if (pendingNativeSettings) { WriteNativeSettings(); pendingNativeSettings = false; }
            if (!Draft.Bypass && !nativeWriter.RefreshLease()) throw new IOException(Loc.T("参数租约不再属于当前控制器。", "The parameter lease is no longer held by this controller."));
        }
        catch (Exception error)
        {
            StopNativeProcessing(); UpdateChrome();
            Notice(Loc.T("自研 APO 参数连接中断：", "Native APO parameter connection ended: ") + error.Message);
        }
    }

    private void DisableOwnNativeBeforeLocalStart()
    {
        StopNativeProcessing();
        if (!TryNativeEndpoint(out var endpoint)) return;
        if (!nativeDeployment.Inspect(NativeApoDll, endpoint).Bound) return;
        // The writer refuses a fresh lease held by another controller. This
        // prevents two app instances from silently stacking system/local gain.
        using var writer = NativeApoParameterWriter.OpenExisting(endpoint);
        writer.Disable();
    }

    private UIElement NativeApoControls()
    {
        var panel = new StackPanel();
        var endpoint = TryNativeEndpoint(out var selected) ? selected : Guid.Empty;
        var status = nativeDeployment.Inspect(NativeApoDll, endpoint);
        panel.Children.Add(T(!status.Component.Present
            ? Loc.T("原生组件：未找到", "Native component: not found")
            : status.Component.Trusted ? Loc.T("原生组件：签名与身份检查通过", "Native component: signature and identity checks passed")
            : Loc.T("原生组件：开发构建，系统部署未就绪", "Native component: development build; system deployment is not ready"), 13, status.Component.Trusted ? Accent : Amber));
        panel.Children.Add(T(Loc.F("注册：{0} · 设备绑定：{1}", "Registered: {0} · Endpoint bound: {1}",
            status.Registered ? Loc.T("是", "yes") : Loc.T("否", "no"), status.Bound ? Loc.T("是", "yes") : Loc.T("否", "no")), 12, Muted));
        panel.Children.Add(T(Loc.T("Windows 实际加载与音频响应：尚待验证。当前安装包不会自动注册开发组件。", "Windows loading and audio response: unverified. This package never registers a development component automatically."), 12, Muted));
        panel.Children.Add(Buttons(B(Loc.T("查看安装计划", "Preview installation plan"), PreviewNativeInstallation),
            B(Loc.T("还原安装记录", "Restore from operation report"), PreviewNativeRestore)));
        return panel;
    }

    private void PreviewNativeInstallation()
    {
        if (!TryNativeEndpoint(out var endpoint)) { Notice(Loc.T("请先选择麦克风。", "Select a microphone first.")); return; }
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var plan = nativeDeployment.PrepareInstallation(NativeApoDll, endpoint, sid);
        ShowTextDialog(Loc.T("自研 APO 安装预览", "Native APO installation preview"),
            Loc.T("仅安装到当前选择的录音端点；不重启音频服务、不修改系统默认设备。\n\n", "Targets the selected capture endpoint only; no audio-service restart or system-default change.\n\n") + plan.Message + "\n\n" + plan.PreviewText,
            plan.CanApply ? Loc.T("安装并绑定（管理员）", "Install and bind (administrator)") : null,
            () => { StopAudio(); _ = RunNativeHelperAsync(["--apply", plan.SourceDll, endpoint.ToString("D"), sid, plan.Fingerprint]); });
    }

    private void PreviewNativeRestore()
    {
        var request = State.NativeApoInstallReportId;
        if (request == null)
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Mic Studio operation report|*.json", Title = Loc.T("选择自研 APO 操作记录", "Select a native APO operation report"), InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MicStudio", "APO", "Reports") };
            if (picker.ShowDialog(this) != true) return;
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(picker.FileName), out var parsed) || !Path.GetFullPath(picker.FileName).Equals(NativeApoPaths.OperationReport(parsed), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Select a protected Mic Studio operation report.");
            request = parsed;
        }
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var report = nativeDeployment.ReadOperationReport(request.Value, sid);
        var backup = report.Result.BackupFile;
        var hash = report.BackupSha256;
        if (string.IsNullOrWhiteSpace(backup) || string.IsNullOrWhiteSpace(hash)) throw new InvalidDataException(Loc.T("该记录没有可还原的绑定备份。", "This report has no restorable binding backup."));
        ShowTextDialog(Loc.T("自研 APO 还原预览", "Native APO restore preview"),
            Loc.T("将核对受保护备份与当前注册值，只还原未被外部改动的自有绑定；保留组件文件和个人设置。\n\n", "Checks the protected backup and current registry values, restoring unchanged own bindings only. Component files and personal settings are retained.\n\n") + backup,
            Loc.T("还原绑定（管理员）", "Restore binding (administrator)"),
            () => { StopAudio(); _ = RunNativeHelperAsync(["--restore", backup, hash, sid]); });
    }

    private async Task RunNativeHelperAsync(string[] operation)
    {
        try
        {
            var helperPath = Path.Combine(AppContext.BaseDirectory, "MicStudio.ApoControl.exe");
            if (!File.Exists(helperPath)) throw new FileNotFoundException(Loc.T("缺少原生组件管理程序。", "The native component administration helper is missing."));
            var request = Guid.NewGuid();
            var start = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var argument in operation) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(request.ToString("D"));
            using var process = Process.Start(start) ?? throw new IOException("The administration helper did not start.");
            await process.WaitForExitAsync();
            if (closed) return;
            var reportPath = NativeApoPaths.OperationReport(request);
            Notice(process.ExitCode == 0 ? Loc.T("自研组件操作完成；实际音频仍需验证。", "Native component operation completed; actual audio still needs verification.") : Loc.T("自研组件操作未完成，系统处理未被标记为生效。", "Native component operation did not complete; system audio is not marked verified."));
            if (File.Exists(reportPath))
            {
                var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
                var report = nativeDeployment.ReadOperationReport(request, sid);
                if (report.Result.Success && process.ExitCode == 0 && operation[0] == "--apply" && !string.IsNullOrEmpty(report.BackupSha256)) { State.NativeApoInstallReportId = request; SaveSoon(); }
                ShowTextDialog(Loc.T("自研组件操作记录", "Native component operation report"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            ShowPage(currentPage);
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        { if (!closed) Notice(Loc.T("管理员操作已取消。", "Administrator operation cancelled.")); }
        catch (Exception error) { if (!closed) Notice(error.Message); }
    }
}
