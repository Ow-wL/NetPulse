using System.Text;
using NetPulse.Core.Measurement;

namespace NetPulse.Core.Demo;

/// <summary>
/// 시나리오를 재생하는 측정 공급자. 실제 ICMP 자리에 들어간다 (기획서 4.3: 가장 바깥에서만 바꿔 끼운다).
/// 결과는 (회차, 대상)만으로 정해진다. 실제 Windows를 조회하지 않는다.
/// </summary>
public sealed class DemoProbe(Scenario scenario) : IProbe
{
    public Task<ProbeResult> ProbeAsync(ProbeTarget target, long round, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Resolve(target.Id, round));
    }

    public ProbeResult Resolve(string targetId, long round)
    {
        ScenarioSegment? match = null;
        foreach (var segment in scenario.Segments)   // 뒤에 적힌 구간이 이긴다
            if (round >= segment.From && round < segment.To && (segment.Target == "*" || segment.Target == targetId))
                match = segment;
        if (match is null) return ProbeResult.Error("scenario-undefined");
        if (match.Result == "lost") return ProbeResult.Lost("TimedOut");
        if (match.Result == "error") return ProbeResult.Error("scenario-error");
        var rtt = match.Rtt!;
        double u = Unit(scenario.Seed, targetId, round) * 2 - 1;   // -1 ~ 1
        return ProbeResult.Success(Math.Max(0, Math.Round(rtt.Base + rtt.Jitter * u, 1)));
    }

    /// <summary>
    /// (시드, 대상, 회차)로 정해지는 0~1 값. FNV-1a 64비트 뒤에 섞기 단계(splitmix64 끝 단계)를 둔다:
    /// FNV-1a만으로는 회차 끝자리만 다를 때 높은 비트가 거의 안 바뀌어 10회차 동안 같은 값이 나왔다 (M5 화면에서 발견).
    /// </summary>
    internal static double Unit(long seed, string targetId, long round)
    {
        const ulong offset = 14695981039346656037, prime = 1099511628211;
        ulong hash = offset;
        foreach (byte b in Encoding.UTF8.GetBytes($"{seed}|{targetId}|{round}")) { hash ^= b; hash *= prime; }
        hash ^= hash >> 30; hash *= 0xbf58476d1ce4e5b9;
        hash ^= hash >> 27; hash *= 0x94d049bb133111eb;
        hash ^= hash >> 31;
        return (hash >> 11) / (double)(1UL << 53);
    }
}
