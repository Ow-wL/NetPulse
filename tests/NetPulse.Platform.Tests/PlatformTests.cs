using System.Net;
using NetPulse.Core.Measurement;
using NetPulse.Core.Timing;

namespace NetPulse.Platform.Tests;

public sealed class PlatformTests
{
    [Fact]
    public async Task LoopbackPingGivesAResultWithoutElevation()
    {
        var probe = new IcmpProbe(TimeSpan.FromMilliseconds(800));
        var result = await probe.ProbeAsync(new ProbeTarget("loopback", "local", IPAddress.Loopback), 0, TestContext.Current.CancellationToken);
        // 루프백은 성공해야 정상이다. 러너 환경이 ICMP를 막으면 명시적 오류여야 하고, 손실로 꾸미면 안 된다.
        Assert.True(result.Outcome is ProbeOutcome.Success or ProbeOutcome.Error, $"결과: {result.Outcome} {result.Status}");
        if (result.Outcome == ProbeOutcome.Success) Assert.NotNull(result.RttMs);
        else Assert.StartsWith("ProbeError", result.Status);
    }

    [Fact]
    public void SystemLogReadGivesCountOrExplicitStatus()
    {
        var result = SystemEventLog.CountRecent(TimeSpan.FromDays(1), 20);
        // 읽었으면 개수, 못 읽었으면 명시적 상태. "이벤트 없음"과 "읽지 못함"을 섞지 않는다.
        if (result.Status == LogReadStatus.Read) Assert.InRange(result.Count, 0, 20);
        else { Assert.Equal(0, result.Count); Assert.False(string.IsNullOrEmpty(result.Detail)); }
    }

    [Fact]
    public async Task EngineRunsWithRealProbeOnLoopback()
    {
        // 실제 시계·실제 ICMP로 상한 3회차. 끝난 상태가 구독자에게 도착하고, 회차 + 건너뜀이 상한과 같은지 본다.
        // (0번부터 시작하는지, 중간 상태 생략 같은 시간 의존 검사는 Core의 가짜 시계 테스트가 맡는다)
        var engine = new MeasurementEngine(new SystemClock(), new IcmpProbe(TimeSpan.FromMilliseconds(800)),
            [new ProbeTarget("loopback", "local", IPAddress.Loopback)], TimeSpan.FromMilliseconds(200), roundLimit: 3);
        var gate = new object();
        EngineState? last = null;
        using var subscription = engine.Subscribe(s => { lock (gate) last = s; });
        engine.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        bool Delivered() { lock (gate) return last is { Finished: true }; }
        while (!Delivered() && DateTime.UtcNow < deadline) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(Delivered(), "끝난 상태가 구독자에게 도착해야 한다");
        EngineState final;
        lock (gate) final = last!;
        Assert.Equal(3, final.Rounds + final.Skipped);
        // 러너가 느려 세 회차를 모두 건너뛰면 마지막 회차가 없다 (M1 G4 3라운드 이월 2)
        if (final.Rounds > 0) Assert.InRange(final.Latest!.Tick.Index, 0, 2);
        else Assert.Null(final.Latest);
        await engine.DisposeAsync();
    }
}
