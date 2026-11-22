using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;

namespace NetPulse.Core.Demo;

/// <summary>
/// 데모 경로·링크 관측 (M4 설계 10절). 모든 대상이 인터페이스 7(유선)을 쓰고, 시나리오의 links 구간에는
/// 그 어댑터가 내려가고 경로 조회도 실패한다. 실제 Windows를 조회하지 않는다.
/// 무선 시나리오면 신호도 낸다 (M12 설계 C 데모): 시나리오 signal 구간 값, 없으면 끊긴 동안 연결 안 됨·그 밖 품질 80%·−55 dBm.
/// </summary>
public sealed class DemoNetwork(Scenario scenario) : IRouteObserver, ILinkObserver, ISignalSource
{
    public const int InterfaceIndex = 7;
    public static readonly AdapterInfo Adapter = new("{DEMO-ADAPTER}", InterfaceIndex, "wired", false);
    /// <summary>시나리오의 연결 종류에 맞춘 어댑터 (M5: Wi‑Fi 데모).</summary>
    private readonly AdapterInfo adapter = scenario.Network?.Kind == "wireless" ? Adapter with { Kind = "wireless" } : Adapter;
    private string KindText => adapter.Kind;
    private Action<LinkSample>? sink;
    private long round;
    private bool watching;

    public IReadOnlyList<RouteSample> Observe(IReadOnlyList<ProbeTarget> targets, long round)
    {
        Volatile.Write(ref this.round, round);
        bool down = scenario.LinkDownAt(round);
        return targets.Select(t => down ? new RouteSample(t.Id, null, null, false, "demo-link-down")
            : new RouteSample(t.Id, InterfaceIndex, round % 30 == 0 ? KindText : null, true, null)).ToArray();
    }

    public (LinkCapability Capability, string? Code) Start(Action<LinkSample> sink)
    {
        this.sink = sink;
        return (LinkCapability.Supported, null);
    }

    public void Watch(IReadOnlyCollection<int> interfaceIndexes) => watching |= interfaceIndexes.Contains(InterfaceIndex);

    public void Poll()
    {
        // 링크가 내려간 동안은 경로가 없어 Watch가 안 불리므로, 한 번 감시를 시작하면 계속 읽는다
        if (!watching && scenario.LinkDownAt(Volatile.Read(ref round))) return;
        watching = true;
        sink?.Invoke(new LinkSample(adapter, scenario.LinkDownAt(Volatile.Read(ref round)) ? LinkState.Down : LinkState.Up, true));
    }

    public IReadOnlyList<AdapterInfo> Adapters => watching ? [adapter] : [];

    /// <summary>지금 회차(경로 관측이 적어 둔 것)의 시나리오 신호. 데모 어댑터가 아니거나 무선 시나리오가 아니면 무선 인터페이스가 아니다.</summary>
    public SignalReading Read(AdapterInfo adapter) =>
        adapter.Key == this.adapter.Key && scenario.SignalAt(Volatile.Read(ref round)) is { } reading
            ? reading
            : new SignalReading(SignalState.Unsupported, null, null, "not-wlan-interface", null);

    public void Dispose() { }
}
