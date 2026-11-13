using System.Net;
using NetPulse.Core.Measurement;
using NetPulse.Core.Timing;

namespace NetPulse.Core.Tests;

internal static class TestSupport
{
    public static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 0, 0, TimeSpan.FromHours(9));

    public static ProbeTarget Target(string id, string role = "external", string address = "192.0.2.10") =>
        new(id, role, IPAddress.Parse(address));

    /// <summary>비동기 작업이 조건을 만족할 때까지 기다린다 (최대 5초).</summary>
    public static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건을 만족하지 못했습니다: " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>엔진이 다음 회차를 기다리기 시작할 때까지 기다린 뒤 시계를 돌린다.</summary>
    public static async Task AdvanceWhenWaiting(ManualClock clock, TimeSpan by)
    {
        await Eventually(() => clock.WaiterCount > 0, "엔진이 다음 회차를 기다림");
        clock.Advance(by);
    }
}

/// <summary>대상과 회차를 그대로 돌려주는 공급자. 호출 기록을 남긴다.</summary>
internal sealed class RecordingProbe(Func<ProbeTarget, long, ProbeResult>? result = null) : IProbe
{
    public readonly List<(string Target, long Round)> Calls = [];

    public Task<ProbeResult> ProbeAsync(ProbeTarget target, long round, CancellationToken cancellationToken)
    {
        lock (Calls) Calls.Add((target.Id, round));
        return Task.FromResult(result?.Invoke(target, round) ?? ProbeResult.Success(10));
    }
}
