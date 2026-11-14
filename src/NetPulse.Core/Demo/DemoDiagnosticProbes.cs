using System.Net;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;

namespace NetPulse.Core.Demo;

/// <summary>
/// 데모 진단 검사기 (M6 설계 3.2). 실제 검사기 자리에 들어간다 (기획서 4.3: 가장 바깥에서만 바꿔 끼운다).
/// 답은 호출된 그 순간의 시간축 회차와 시나리오로 정한다. 시나리오의 <c>diagnosis</c> 덮어쓰기가 있으면 그 값이 이긴다.
/// <c>timeout</c>은 결과를 꾸미지 않고 취소될 때까지 기다려 조율자의 실제 제한시간 경로를 탄다.
/// </summary>
public sealed class DemoDiagnosticProbes : IDiagnosticProbes
{
    /// <summary>빠른 진단 슬롯 도메인 (데모 설정, 문서용).</summary>
    public static readonly IReadOnlyList<string> SlotDomains = ["a.example", "b.example"];
    public static readonly Uri PortalUri = new("http://portal.example/connecttest.txt");

    private readonly DemoWorld world;
    private readonly ScenarioDiagnosis? d;
    private readonly ScenarioTarget[] externals;
    private readonly ScenarioTarget? local;

    public DemoDiagnosticProbes(DemoWorld world)
    {
        this.world = world;
        d = world.Scenario.Diagnosis;
        externals = world.Scenario.Targets.Where(t => t.Role == "external").Take(2).ToArray();
        local = world.Scenario.Targets.FirstOrDefault(t => t.Role == "local");
    }

    /// <summary>데모 진단 설정 (M6 설계 3.2): 외부 TCP = 시나리오 외부 대상 앞의 둘, 도메인 = 슬롯 도메인, 로그인 주소 = 문서용.</summary>
    public static DiagnosisSettings Settings(Scenario scenario) => new()
    {
        TcpTargets = scenario.Targets.Where(t => t.Role == "external").Take(2).Select(t => IPAddress.Parse(t.Address)).ToArray(),
        Domains = SlotDomains,
        PortalUri = PortalUri,
    };

    private long Round => (long)world.TimeBase.Scaled.TotalSeconds;

    private async Task Delay(string key, CancellationToken ct)
    {
        if (d?.DelayMs?.GetValueOrDefault(key) is int ms && ms > 0) await Task.Delay(ms, ct).ConfigureAwait(false);
    }

    /// <summary>덮어쓴 결과 값 → 대상 결과. timeout은 취소될 때까지 기다린다.</summary>
    private static async Task<ProbeOutcomeResult> Forced(string value, CancellationToken ct)
    {
        switch (value)
        {
            case "ok": return new(TargetOutcome.Ok);
            case "fail": return new(TargetOutcome.Fail, "demo-fail");
            case "error": return new(TargetOutcome.Error, "demo-error");
            default:
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return new(TargetOutcome.Timeout, "timeout");
        }
    }

    /// <summary>그 대상의 회차 결과 → 대상 결과. error·정의되지 않은 회차는 오류로 둔다 (성공·무응답으로 줄이지 않음).</summary>
    private ProbeOutcomeResult FromTimeline(ScenarioTarget target)
    {
        var r = world.Resolve(target.Id, Round);
        return r.Outcome switch
        {
            ProbeOutcome.Success => new(TargetOutcome.Ok),
            ProbeOutcome.Lost => new(TargetOutcome.Fail, r.Status),
            _ => new(TargetOutcome.Error, r.Status),
        };
    }

    private bool ExternalsAllLost => externals.Length > 0 && externals.All(t => world.Resolve(t.Id, Round).Outcome == ProbeOutcome.Lost);

    public async Task<AdapterFacts> AdapterAsync(CancellationToken ct)
    {
        await Delay("adapter", ct).ConfigureAwait(false);
        IPAddress? gateway = local is null ? null : IPAddress.Parse(local.Address);
        if (d?.Adapter is { } a)
            return new AdapterFacts(a.Route, a.Up ? 1 : 0, a.Up, a.Ipv4, a.Apipa, a.Gateway ? gateway : null, a.Dns);
        bool up = !world.Scenario.LinkDownAt(Round) && world.Scenario.Network?.Kind != "none";
        return new AdapterFacts(true, up ? 1 : 0, up, up, false, gateway, true);
    }

    public async Task<ProbeOutcomeResult> PingAsync(IPAddress address, CancellationToken ct)
    {
        await Delay("gateway", ct).ConfigureAwait(false);
        if (d?.Gateway is { } g) return await Forced(g, ct).ConfigureAwait(false);
        var target = world.Scenario.Targets.FirstOrDefault(t => IPAddress.Parse(t.Address).Equals(address));
        return target is null ? new(TargetOutcome.Fail, "TimedOut") : FromTimeline(target);
    }

    public async Task<ProbeOutcomeResult> TcpAsync(IPAddress address, int port, CancellationToken ct)
    {
        await Delay("tcp", ct).ConfigureAwait(false);
        int slot = Array.FindIndex(externals, t => IPAddress.Parse(t.Address).Equals(address));
        if (slot >= 0 && d?.Tcp is { } tcp && slot < tcp.Count) return await Forced(tcp[slot], ct).ConfigureAwait(false);
        return slot >= 0 ? FromTimeline(externals[slot]) : new(TargetOutcome.Fail, "TimedOut");
    }

    public Task<ProbeOutcomeResult> DnsAsync(string host, CancellationToken ct) => Host(host, isDns: true, ct);

    public Task<ProbeOutcomeResult> HttpsAsync(string host, CancellationToken ct) => Host(host, isDns: false, ct);

    /// <summary>
    /// 이름 검사 (빠른 진단과 사이트 점검이 같은 규칙, 같은 호스트는 같은 답): ① site[호스트] ② 슬롯 도메인이면 슬롯 규칙
    /// (슬롯 덮어쓰기 → 대응 외부 대상의 회차) ③ 문서용 도메인 성공 ④ 실패.
    /// </summary>
    private async Task<ProbeOutcomeResult> Host(string host, bool isDns, CancellationToken ct)
    {
        string h = host.Trim().TrimEnd('.').ToLowerInvariant();
        bool siteCheck = !SlotDomains.Contains(h);
        await Delay(siteCheck ? "site" : isDns ? "dns" : "https", ct).ConfigureAwait(false);
        if (d?.Site?.GetValueOrDefault(h) is { } site && (isDns ? site.Dns : site.Https) is { } v) return await Forced(v, ct).ConfigureAwait(false);
        int slot = SlotDomains.ToList().IndexOf(h);
        if (slot >= 0)
        {
            if ((isDns ? d?.Dns : d?.Https) is { } forced && slot < forced.Count) return await Forced(forced[slot], ct).ConfigureAwait(false);
            if (slot < externals.Length) return FromTimeline(externals[slot]);
        }
        if (Scenario.IsDocumentationDomain(h)) return new(TargetOutcome.Ok);
        return isDns ? new(TargetOutcome.Fail, "dns-HostNotFound") : new(TargetOutcome.Fail, "no-address");
    }

    public async Task<PortalResult> PortalAsync(Uri uri, string expected, CancellationToken ct)
    {
        await Delay("portal", ct).ConfigureAwait(false);
        if (d?.Portal is { } p)
        {
            var o = await Forced(p.Outcome ?? "ok", ct).ConfigureAwait(false);
            return o.Outcome == TargetOutcome.Ok ? new PortalResult(TargetOutcome.Ok, p.Status, p.Body) : new PortalResult(o.Outcome, null, false, o.Code);
        }
        if (ExternalsAllLost)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);   // 인터넷 쪽 무응답: 제한시간 경로
        }
        return new PortalResult(TargetOutcome.Ok, 200, true);
    }

    public async Task<LinkFacts> LinkAsync(CancellationToken ct)
    {
        await Delay("link", ct).ConfigureAwait(false);
        if (d?.Link is { } l)
            return new LinkFacts(l.Now switch { "down" => LinkState.Down, "unknown" => LinkState.Unknown, _ => LinkState.Up },
                l.Log switch { "unsupported" => LinkCapability.Unsupported, "denied" => LinkCapability.Denied, _ => LinkCapability.Supported }, l.RecentDown);
        long r = Round;
        bool recent = false;
        for (long i = Math.Max(0, r - 1800); i < r && !recent; i++) recent = world.Scenario.LinkDownAt(i);
        return new LinkFacts(world.Scenario.LinkDownAt(r) ? LinkState.Down : LinkState.Up, LinkCapability.Supported, recent);
    }
}
