using System.Net;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Detection;

public enum InterfaceKind { Wired, Wireless, Tunnel, Other }

public enum Tristate { Yes, No, Unknown }

/// <summary>작동 중인 인터페이스 하나 (주소·이름은 담지 않는다).</summary>
public sealed record InterfaceInfo(int Index, InterfaceKind Kind, Tristate Virtual, bool HasIpv4Gateway);

/// <summary>Windows 경로표가 고른 가장 좋은 IPv4 경로. 다음 홉이 없으면(같은 링크) NextHop은 null.</summary>
public sealed record BestRoute(int InterfaceIndex, IPAddress? NextHop);

/// <summary>탐지 입력: 인터페이스 목록과 외부 주소로 가는 가장 좋은 경로 (M3 설계 4절).</summary>
public sealed record NetworkSnapshot(IReadOnlyList<InterfaceInfo> Interfaces, BestRoute? Route, string? RouteError);

public enum DetectionStatus { Ok, NoGateway, NoIpv4Route, NoNetwork, Unknown }

/// <summary>탐지 결과와 제안 프로필. 안내 문구는 화면(M5 첫 실행)이 보여 준다.</summary>
public sealed record DetectionResult(
    DetectionStatus Status,
    Profile? Proposal,
    string? Notice,
    InterfaceKind? Kind,
    Tristate Virtual,
    int OtherGateways);

/// <summary>
/// 자동 탐지 제안 (M3 설계 4절, 순수 로직). 실제 경로 선택은 Windows 경로표의 결과를 그대로 따른다 (앱이 다시 고르지 않음).
/// </summary>
public static class DetectionPlanner
{
    /// <summary>제안할 외부 대상: 서로 다른 사업자의 공개 DNS 둘. 사용자가 바꿀 수 있다.</summary>
    public static IReadOnlyList<Target> DefaultExternals { get; } =
    [
        new("cloudflare", "Cloudflare DNS", IPAddress.Parse("1.1.1.1"), TargetRole.External, "Cloudflare", ProbeMethod.Icmp),
        new("google", "Google DNS", IPAddress.Parse("8.8.8.8"), TargetRole.External, "Google", ProbeMethod.Icmp),
    ];

    /// <summary>경로 질의에 쓰는 주소 (첫 외부 대상).</summary>
    public static IPAddress RouteProbeAddress => DefaultExternals[0].Address;

    public static DetectionResult Plan(NetworkSnapshot net)
    {
        var up = net.Interfaces;
        if (up.Count == 0)
            return new(DetectionStatus.NoNetwork, null, "작동 중인 네트워크 연결이 없습니다.", null, Tristate.Unknown, 0);

        if (net.Route is null)
        {
            if (net.RouteError is not null)
                return new(DetectionStatus.Unknown, Proposal(null, ExpectedConnection.Any), "경로를 조회하지 못했습니다: " + net.RouteError, null, Tristate.Unknown, CountGateways(up, null));
            return new(DetectionStatus.NoIpv4Route, Proposal(null, ExpectedConnection.Any),
                "IPv4 인터넷 경로가 없습니다. IPv6만 쓰는 망이거나 연결되지 않은 상태일 수 있습니다.", null, Tristate.Unknown, CountGateways(up, null));
        }

        var nic = up.FirstOrDefault(i => i.Index == net.Route.InterfaceIndex);
        var kind = nic?.Kind ?? InterfaceKind.Other;
        var virt = nic?.Virtual ?? Tristate.Unknown;
        var expected = kind switch { InterfaceKind.Wired => ExpectedConnection.Wired, InterfaceKind.Wireless => ExpectedConnection.Wireless, _ => ExpectedConnection.Any };
        int others = CountGateways(up, net.Route.InterfaceIndex);
        var hop = net.Route.NextHop;
        if (hop is null || hop.Equals(IPAddress.Any))
            return new(DetectionStatus.NoGateway, Proposal(null, expected), "게이트웨이를 찾지 못했습니다. VPN이나 터널을 쓰는 중일 수 있습니다.", kind, virt, others);

        string? notice = virt == Tristate.Yes ? "기본 경로가 가상 어댑터 또는 터널(VPN일 수 있음)을 지납니다. 첫 측정 전에 확인하세요." : null;
        if (others > 0) notice = (notice is null ? "" : notice + " ") + $"다른 연결에도 게이트웨이가 {others}개 있습니다.";
        return new(DetectionStatus.Ok, Proposal(hop, expected), notice, kind, virt, others);
    }

    private static int CountGateways(IReadOnlyList<InterfaceInfo> up, int? chosen) =>
        up.Count(i => i.HasIpv4Gateway && i.Index != chosen);

    private static Profile Proposal(IPAddress? gateway, ExpectedConnection expected)
    {
        var targets = new List<Target>();
        if (gateway is not null) targets.Add(new("router", "공유기", gateway, TargetRole.Local, null, ProbeMethod.Icmp));
        targets.AddRange(DefaultExternals);
        return new Profile("default", "기본", targets, Profile.DefaultTimeout, Profile.DefaultDuration, expected);
    }
}
