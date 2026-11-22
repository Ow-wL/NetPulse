using System.IO;
using System.Text.Json;
using NetPulse.Design.Tokens;

namespace NetPulse.UiTests;

/// <summary>구간 종류: idle(95백분위), 전환·조작, 최악 열기, 최악 스크롤.</summary>
internal enum PerfKind { Idle, Transition, WorstOpen, WorstScroll }

/// <summary>
/// 성능 판정 (M12 설계 G2, DECISIONS "M12 노트북 성능 기준"). 프로필은 <c>NETPULSE_PERF_PROFILE</c>(ui-test.ps1 <c>-PerfProfile</c>)로만 정한다.
/// 구간마다 지금 프로필로 판정하고, 회귀를 숨기지 않도록 standard 판정도 늘 함께 남긴다 (판정에는 지금 프로필만 쓴다).
/// 주사율을 읽지 못했거나 구간 중에 바뀌었으면 실패다 (기본값으로 넘기지 않는다).
/// </summary>
internal static class Perf
{
    public static PerfProfile Profile => FrameContract.Parse(Environment.GetEnvironmentVariable(FrameContract.ProfileVariable));

    private static (double Max, int LongGaps) Caps(FrameLimits l, PerfKind kind) => kind switch
    {
        PerfKind.WorstOpen => (l.WorstOpenMax, l.WorstOpenLongGaps),
        PerfKind.WorstScroll => (l.WorstScrollMax, l.WorstScrollLongGaps),
        _ => (l.TransitionMax, l.TransitionLongGaps),
    };

    /// <summary>한 구간을 판정한다. 문제(지금 프로필)를 <paramref name="problems"/>에 더하고, 기록용 판정 요약을 돌려준다.</summary>
    public static object Check(JsonElement s, PerfKind kind, List<string> problems)
    {
        string seg = s.GetProperty("Segment").GetString()!;
        double? hz = s.TryGetProperty("Hz", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : null;
        string? source = s.TryGetProperty("RefreshSource", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() : null;
        bool changed = s.TryGetProperty("RefreshChanged", out var ch) && ch.ValueKind == JsonValueKind.True;
        if (hz is not { } rate || changed)
        {
            problems.Add($"{seg} 주사율을 확정하지 못함 (출처 {source ?? "없음"}{(changed ? ", 구간 중 바뀜" : "")})");
            return new { segment = seg, kind = kind.ToString(), hz, source, changed, pass = false };
        }
        double p95 = s.GetProperty("P95").GetDouble(), max = s.GetProperty("Max").GetDouble();
        int longGaps = s.GetProperty("LongGaps").GetInt32();
        bool Pass(FrameLimits l) => kind == PerfKind.Idle ? p95 <= l.IdleP95 : max <= Caps(l, kind).Max && longGaps <= Caps(l, kind).LongGaps;
        var limits = FrameContract.For(Profile, rate);
        var standard = FrameContract.For(PerfProfile.Standard, rate);
        bool pass = Pass(limits);
        if (!pass)
        {
            if (kind == PerfKind.Idle) problems.Add($"{seg} 95백분위 {p95:0.00} ms > {limits.IdleP95} ({limits.Profile}, {rate} Hz)");
            else
            {
                var (capMax, capGaps) = Caps(limits, kind);
                problems.Add($"{seg} 최대 {max:0.0} ms, 긴 간격(> {limits.LongGapMs} ms) {longGaps}회 (기준 {capMax} ms·{capGaps}회, {limits.Profile}, {rate} Hz)");
            }
        }
        return new
        {
            segment = seg, kind = kind.ToString(), profile = limits.Profile.ToString(), version = limits.Version, hz = rate, source,
            p95, max, longGaps, longGapMs = limits.LongGapMs, over33 = s.GetProperty("Over33").GetInt32(), missed = s.TryGetProperty("Missed", out var mi) ? mi.GetInt32() : 0,
            applied = kind == PerfKind.Idle ? (object)new { idleP95 = limits.IdleP95 } : new { max = Caps(limits, kind).Max, longGaps = Caps(limits, kind).LongGaps },
            pass, standardPass = Pass(standard),
        };
    }

    /// <summary>판정 요약을 g5 폴더에 남긴다 (계약 버전·프로필·실제 적용 상한·주사율·출처, standard 판정 포함).</summary>
    public static void Record(string name, IEnumerable<object> verdicts)
    {
        var dir = Path.Combine(AppDriver.OutputDirectory, "g5");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "perf-" + name + ".json"), JsonSerializer.Serialize(
            new { version = FrameContract.Version, profile = Profile.ToString(), verdicts = verdicts.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
