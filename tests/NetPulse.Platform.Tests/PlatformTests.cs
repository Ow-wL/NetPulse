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
        // 실제 시계·실제 ICMP로 3회차. 회차 번호가 0부터 이어지는지 본다.
        var engine = new MeasurementEngine(new SystemClock(), new IcmpProbe(TimeSpan.FromMilliseconds(800)),
            [new ProbeTarget("loopback", "local", IPAddress.Loopback)], TimeSpan.FromMilliseconds(200), roundLimit: 3);
        var indices = new List<long>();
        using var subscription = engine.Subscribe(s => { if (s.Latest is { } l) lock (indices) if (!indices.Contains(l.Tick.Index)) indices.Add(l.Tick.Index); });
        engine.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!engine.State.Finished && DateTime.UtcNow < deadline) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(engine.State.Finished);
        Assert.Equal(3, engine.State.Rounds + engine.State.Skipped);
        Assert.Equal(0, indices[0]);
        await engine.DisposeAsync();
    }
}
