using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetPulse.Core.Detection;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;

namespace NetPulse.Platform;

/// <summary>
/// 인터페이스·경로 조회 (M3 설계 4절, 6.4). 경로표 조회일 뿐 패킷을 보내지 않는다.
/// IPv4만 다루므로 GetBestRoute2 대신 GetBestRoute·GetBestInterface를 쓴다 (같은 경로표, 같은 선택. DECISIONS 기록).
/// </summary>
public static class NetworkInfo
{
    /// <summary>탐지 입력을 만든다.</summary>
    public static NetworkSnapshot Snapshot(IPAddress routeProbe)
    {
        var interfaces = Interfaces();
        var (route, error) = BestRoute(routeProbe);
        return new NetworkSnapshot(interfaces, route, error);
    }

    /// <summary>작동 중인 IPv4 인터페이스 (루프백 제외). 이름·주소는 담지 않는다.</summary>
    public static IReadOnlyList<InterfaceInfo> Interfaces()
    {
        var list = new List<InterfaceInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            int index;
            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
                // IPv4가 없는 연결(IPv6만)도 "작동 중인 연결"로 센다: 그래야 NoNetwork가 아니라 NoIpv4Route가 된다
                index = Ipv4Index(props) ?? Ipv6Index(props) ?? -1;
            }
            catch (NetworkInformationException) { continue; }
            if (index < 0) continue;
            bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            list.Add(new InterfaceInfo(index, KindOf(nic), VirtualOf(nic), gateway));
        }
        return list;
    }

    private static int? Ipv4Index(IPInterfaceProperties props)
    {
        try { return props.GetIPv4Properties()?.Index; }
        catch (NetworkInformationException) { return null; }
    }

    private static int? Ipv6Index(IPInterfaceProperties props)
    {
        try { return props.GetIPv6Properties()?.Index; }
        catch (NetworkInformationException) { return null; }
    }

    public static InterfaceKind KindOf(NetworkInterface nic) => nic.NetworkInterfaceType switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Ethernet3Megabit => InterfaceKind.Wired,
        NetworkInterfaceType.Wireless80211 => InterfaceKind.Wireless,
        NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => InterfaceKind.Tunnel,
        _ => InterfaceKind.Other,
    };

    private static readonly string[] VirtualHints = ["virtual", "hyper-v", "vpn", "tap-", "wireguard", "vmware", "virtualbox", "loopback", "tailscale", "zerotier", "wintun"];

    /// <summary>가상·터널 여부. 이름 단서가 있으면 Yes, 무선·유선이면서 단서가 없으면 No, 그 밖에는 Unknown.</summary>
    public static Tristate VirtualOf(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return Tristate.Yes;
        var text = (nic.Description + " " + nic.Name).ToLowerInvariant();
        if (VirtualHints.Any(text.Contains)) return Tristate.Yes;
        return KindOf(nic) is InterfaceKind.Wired or InterfaceKind.Wireless ? Tristate.No : Tristate.Unknown;
    }

    /// <summary>주소로 가는 가장 좋은 IPv4 경로. 경로가 없으면 (null, null), 조회 실패면 (null, 코드).</summary>
    public static (BestRoute? Route, string? Error) BestRoute(IPAddress destination)
    {
        uint result = GetBestRoute(ToDword(destination), 0, out var row);
        if (result == ErrorNotFound || result == ErrorNetworkUnreachable) return (null, null);
        if (result != 0) return (null, "GetBestRoute_" + result);
        var nextHop = row.dwForwardNextHop == 0 ? null : new IPAddress(row.dwForwardNextHop);
        return (new BestRoute((int)row.dwForwardIfIndex, nextHop), null);
    }

    /// <summary>주소로 갈 때 Windows가 고르는 인터페이스 번호. 실패하면 오류 코드.</summary>
    public static (int? Index, string? Error) BestInterface(IPAddress destination)
    {
        uint result = GetBestInterface(ToDword(destination), out uint index);
        return result == 0 ? ((int)index, null) : (null, "GetBestInterface_" + result);
    }

    /// <summary>인터페이스 번호 → 유형 문자열 (기록용). 찾지 못하면 null.</summary>
    public static string? KindText(int index)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.GetIPProperties().GetIPv4Properties()?.Index != index) continue;
            }
            catch (NetworkInformationException) { continue; }
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return "loopback";
            return KindOf(nic) switch
            {
                InterfaceKind.Wired => "wired", InterfaceKind.Wireless => "wireless", InterfaceKind.Tunnel => "tunnel", _ => "other",
            };
        }
        return null;
    }

    private static uint ToDword(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("IPv4만 지원합니다.", nameof(address));
        return BitConverter.ToUInt32(address.GetAddressBytes(), 0);   // 네트워크 바이트 순서 그대로
    }

    private const uint ErrorNotFound = 1168, ErrorNetworkUnreachable = 1231;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow
    {
        public uint dwForwardDest, dwForwardMask, dwForwardPolicy, dwForwardNextHop, dwForwardIfIndex, dwForwardType, dwForwardProto,
            dwForwardAge, dwForwardNextHopAS, dwForwardMetric1, dwForwardMetric2, dwForwardMetric3, dwForwardMetric4, dwForwardMetric5;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestRoute(uint dwDestAddr, uint dwSourceAddr, out MibIpForwardRow pBestRoute);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);
}

/// <summary>
/// 대상별 Windows 경로 (M3 설계 6.4): 인터페이스 번호는 매 회차, 유형은 30회차마다 다시 조회한다.
/// 번호가 바뀐 회차에는 유형도 바로 조회한다 (앞 인터페이스의 유형을 새 번호에 붙이지 않는다).
/// 실패는 Ok=false와 코드로 돌려준다 (정상으로 꾸미지 않는다).
/// </summary>
public sealed class RouteObserver : IRouteObserver
{
    public const int KindEvery = 30;

    private readonly Dictionary<string, int> lastIndex = [];

    public IReadOnlyList<RouteSample> Observe(IReadOnlyList<ProbeTarget> targets, long round)
    {
        bool periodic = round % KindEvery == 0;
        var samples = new RouteSample[targets.Count];
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            try
            {
                var (index, error) = NetworkInfo.BestInterface(t.Address);
                if (index is int idx)
                {
                    bool changed = !lastIndex.TryGetValue(t.Id, out var before) || before != idx;
                    lastIndex[t.Id] = idx;
                    samples[i] = new RouteSample(t.Id, idx, periodic || changed ? NetworkInfo.KindText(idx) : null, true, null);
                }
                else
                {
                    lastIndex.Remove(t.Id);
                    samples[i] = new RouteSample(t.Id, null, null, false, error);
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or ArgumentException)
            {
                lastIndex.Remove(t.Id);
                samples[i] = new RouteSample(t.Id, null, null, false, ex.GetType().Name);
            }
        }
        return samples;
    }
}
