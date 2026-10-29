using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private string? activeCableSendId;
    private string? cableConnectionWarning;
    private CableRouteSelection SelectCableRoute()
    {
        var route = CableRouteResolver.Resolve(inputs, outputs, State.CableOutputId);
        if (route.IsReady && cableConnectionWarning != null)
        {
            if (noticeRawText == cableConnectionWarning) NoticeBar.Visibility = Visibility.Collapsed;
            cableConnectionWarning = null;
        }
        if (route.IsReady && State.CableOutputId != route.Send!.Id)
        {
            if (engine.IsRunning && activeCableSendId != null && activeCableSendId != route.Send.Id)
            {
                StopAudio();
                Notice("虚拟线路已重新匹配。点击开始处理继续。");
            }
            State.CableOutputId = route.Send.Id;
            State.DiscordVerifiedAt = null;
            SaveSoon();
        }
        // Keep the saved ID while a route is absent. When endpoints reappear,
        // another enumeration repairs the route without changing the microphone.
        return route;
    }

    /// <summary>Stable route states are localized here; resolver messages remain data for diagnostics.</summary>
    private static string RouteMessage(CableRouteSelection route) => route.Status switch
    {
        CableRouteStatus.NotDetected => Loc.T("未检测到 VB-CABLE。请在设备页安装驱动，安装完成后会自动识别。", "VB-CABLE was not detected. Install the driver on Devices; it will be recognized automatically."),
        CableRouteStatus.SendUnavailable => Loc.T("VB-CABLE 发送端未连接或已停用。恢复后会自动匹配。", "The VB-CABLE send endpoint is disconnected or disabled. It will match automatically when it returns."),
        CableRouteStatus.ReceiveUnavailable => Loc.T("VB-CABLE 接收端未连接或已停用。恢复后会自动匹配。", "The VB-CABLE receive endpoint is disconnected or disabled. It will match automatically when it returns."),
        CableRouteStatus.AmbiguousEndpoints => Loc.T("检测到多组同名 VB-CABLE，无法确认配对。请在设备页检查重复驱动。", "Multiple VB-CABLE endpoints with the same name were found, so pairing is ambiguous. Check for duplicate drivers on Devices."),
        CableRouteStatus.UnmatchedEndpoints => Loc.T("未找到同一条 VB-CABLE 的收发端。请检查驱动状态，恢复后会自动匹配。", "No matching send/receive endpoints from the same VB-CABLE were found. Check the driver; pairing is automatic when it returns."),
        _ => Loc.T("虚拟线路已自动匹配。", "Virtual cable matched automatically.")
    };
}
