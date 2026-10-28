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
            if (NoticeText.Text == cableConnectionWarning) NoticeBar.Visibility = Visibility.Collapsed;
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
}
