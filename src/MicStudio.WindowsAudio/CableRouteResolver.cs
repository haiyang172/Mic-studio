using System.Text.RegularExpressions;

namespace MicStudio.WindowsAudio;

public enum CableRouteStatus
{
    Ready,
    NotDetected,
    SendUnavailable,
    ReceiveUnavailable,
    UnmatchedEndpoints,
    AmbiguousEndpoints
}

public sealed record CableRouteSelection(CableRouteStatus Status, AudioDeviceInfo? Send,
    AudioDeviceInfo? Receive, string Message)
{
    public bool IsReady => Status == CableRouteStatus.Ready && Send is not null && Receive is not null;
}

/// <summary>
/// Selects a matching virtual route from an enumeration snapshot. This helper never
/// activates an endpoint, starts audio, changes defaults or installs a driver.
/// </summary>
public static partial class CableRouteResolver
{
    // Match the endpoint label, not arbitrary occurrences of Input/Output in the
    // driver description. Keep the 16-channel label distinct from the stereo pair.
    [GeneratedRegex(@"^\s*(?<family>CABLE(?:-[ABCD])?)\s+(?<direction>Input|Output|In\s+16ch|Out\s+16ch)(?![\p{L}\p{N}_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndpointLabel();

    public static bool IsSendName(string? name) => Identity(name, true) is not null;
    public static bool IsReceiveName(string? name) => Identity(name, false) is not null;

    public static CableRouteSelection Resolve(IReadOnlyList<AudioDeviceInfo> inputs,
        IReadOnlyList<AudioDeviceInfo> outputs, string? preferredSendId = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        var sends = outputs.Where(d => d.IsCableSend).Select(d => (Device: d, Identity: Identity(d.Name, true)))
            .Where(d => d.Identity is not null).ToArray();
        var receives = inputs.Where(d => d.IsCableReceive).Select(d => (Device: d, Identity: Identity(d.Name, false)))
            .Where(d => d.Identity is not null).ToArray();
        if (sends.Length == 0 && receives.Length == 0)
            return Failure(CableRouteStatus.NotDetected, "未检测到 VB-CABLE。请在设备页安装驱动，安装完成后会自动识别。");
        var activeSends = sends.Where(d => d.Device.IsActive).ToArray();
        var activeReceives = receives.Where(d => d.Device.IsActive).ToArray();
        if (activeSends.Length == 0)
            return Failure(CableRouteStatus.SendUnavailable, "VB-CABLE 发送端未连接或已停用。恢复后会自动匹配。");
        if (activeReceives.Length == 0)
            return Failure(CableRouteStatus.ReceiveUnavailable, "VB-CABLE 接收端未连接或已停用。恢复后会自动匹配。");

        // A valid saved send choice takes priority, but its receive endpoint must
        // belong to the same family and be unambiguous.
        var preferred = activeSends.FirstOrDefault(d => d.Device.Id == preferredSendId);
        if (preferred.Device is not null)
        {
            var matches = activeReceives.Where(d => d.Identity!.Key == preferred.Identity!.Key).ToArray();
            if (matches.Length == 1) return Ready(preferred.Device, matches[0].Device);
        }

        foreach (var group in activeSends.GroupBy(d => d.Identity!.Key)
                     .OrderBy(g => g.First().Identity!.Rank).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var send = group.ToArray();
            var receive = activeReceives.Where(d => d.Identity!.Key == group.Key).ToArray();
            if (send.Length == 1 && receive.Length == 1) return Ready(send[0].Device, receive[0].Device);
        }
        var overlaps = activeSends.Any(s => activeReceives.Any(r => r.Identity!.Key == s.Identity!.Key));
        return overlaps
            ? Failure(CableRouteStatus.AmbiguousEndpoints, "检测到多组同名 VB-CABLE，无法确认配对。请在设备页检查重复驱动。")
            : Failure(CableRouteStatus.UnmatchedEndpoints, "未找到同一条 VB-CABLE 的收发端。请检查驱动状态，恢复后会自动匹配。");
    }

    private static CableRouteSelection Ready(AudioDeviceInfo send, AudioDeviceInfo receive) =>
        new(CableRouteStatus.Ready, send, receive, "虚拟线路已自动匹配。");

    private static CableRouteSelection Failure(CableRouteStatus status, string message) => new(status, null, null, message);

    private static RouteIdentity? Identity(string? name, bool send)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var match = EndpointLabel().Match(name);
        if (!match.Success) return null;
        var direction = match.Groups["direction"].Value;
        var isSend = direction.StartsWith("In", StringComparison.OrdinalIgnoreCase);
        if (isSend != send) return null;
        var family = match.Groups["family"].Value.ToUpperInvariant();
        var multichannel = direction.EndsWith("16ch", StringComparison.OrdinalIgnoreCase);
        var familyRank = family.Length == 5 ? 0 : family[^1] - 'A' + 1;
        return new(family + (multichannel ? ":16" : ":2"), familyRank * 2 + (multichannel ? 1 : 0));
    }

    private sealed record RouteIdentity(string Key, int Rank);
}
