using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Analysis;

/// <summary>회차 판정 (M4 설계 B3). 공백·오류만 있는 회차는 판정이 없다(null).</summary>
public enum Verdict
{
    Normal,
    LatencyHigh,
    /// <summary>판단 불가 (경로 보류)</summary>
    RouteHold,
    /// <summary>로컬 링크 단절 관측</summary>
    LinkDown,
    /// <summary>외부 연결 단절 추정</summary>
    ExternalOutage,
    /// <summary>판단 불가 (근거 부족)</summary>
    Insufficient,
    /// <summary>대상 응답 실패</summary>
    TargetFailure,
}

/// <summary>회차 하나의 판정. Notes: 함께 관측된 근거·사유 (예: "link-adjacent", "link-down-virtual", "expected-unrecorded").</summary>
public sealed record RoundJudgment(long Round, Verdict? Verdict, bool Incomplete, IReadOnlyList<string> FailedTargets, IReadOnlyList<string> Notes);

/// <summary>회차 시점의 대상 경로 (M3 route 이벤트에서).</summary>
public sealed record RouteAt(bool Ok, int? InterfaceIndex, string? Kind);

/// <summary>판정에 필요한 측정 맥락. 기록 하나에서 만든다.</summary>
public sealed class JudgeContext
{
    public required IReadOnlyList<Target> Targets { get; init; }
    public required ExpectedConnection? Expected { get; init; }
    public required LinkEvidence Links { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<(long Round, RouteAt Route)>> Routes { get; init; }
    public required int TimeoutMs { get; init; }

    /// <summary>성공을 한 번이라도 한 대상 (M4 설계 B2).</summary>
    public required IReadOnlySet<string> Baseline { get; init; }

    public IEnumerable<Target> BaselineExternals => Targets.Where(t => t.Role == TargetRole.External && Baseline.Contains(t.Id));
    public IEnumerable<Target> BaselineLocals => Targets.Where(t => t.Role == TargetRole.Local && Baseline.Contains(t.Id));

    /// <summary>외부 다양성: 기준선 있는 외부 대상 중 사업자 이름도 주소도 서로 다른 둘이 있음 (M4 설계 B2, 1-6).</summary>
    public bool Diverse
    {
        get
        {
            var ext = BaselineExternals.ToArray();
            for (int i = 0; i < ext.Length; i++)
                for (int j = i + 1; j < ext.Length; j++)
                    if (!string.IsNullOrWhiteSpace(ext[i].Provider) && !string.IsNullOrWhiteSpace(ext[j].Provider)
                        && !string.Equals(ext[i].Provider, ext[j].Provider, StringComparison.OrdinalIgnoreCase)
                        && !ext[i].Address.Equals(ext[j].Address))
                        return true;
            return false;
        }
    }

    /// <summary>회차 r 이전(포함) 마지막으로 성공한 경로의 인터페이스 (링크가 내려가 경로 조회가 실패한 회차에서 쓰던 어댑터를 찾을 때).</summary>
    public int? LastOkInterface(string targetId, long round)
    {
        if (!Routes.TryGetValue(targetId, out var list)) return null;
        int? found = null;
        foreach (var (r, route) in list)
        {
            if (r > round) break;
            if (route.Ok && route.InterfaceIndex is int i) found = i;
        }
        return found;
    }

    /// <summary>회차 r 시점의 대상 경로. 경로 관측이 없으면 null.</summary>
    public RouteAt? RouteOf(string targetId, long round)
    {
        if (!Routes.TryGetValue(targetId, out var list) || list.Count == 0) return null;
        RouteAt? found = null;
        foreach (var (r, route) in list)
        {
            if (r > round) break;
            found = route;
        }
        return found;
    }

    public static JudgeContext From(RecordHeader header, IReadOnlyList<RoundData> rounds, IReadOnlyList<JsonObject> events, LinkEvidence links)
    {
        var baseline = new HashSet<string>(rounds.Where(r => !r.Gap).SelectMany(r => r.Results)
            .Where(x => x.Outcome == ProbeOutcome.Success).Select(x => x.TargetId));
        var routes = new Dictionary<string, List<(long, RouteAt)>>();
        foreach (var e in events.Where(e => Str(e, "type") == "route"))
        {
            var target = Str(e, "target");
            if (target is null || e["round"] is not JsonValue rv || !rv.TryGetValue<long>(out var round)) continue;
            int? index = e["interfaceIndex"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : null;
            var route = new RouteAt(Str(e, "status") == "ok", index, Str(e, "kind"));
            if (!routes.TryGetValue(target, out var list)) routes[target] = list = [];
            list.Add((round, route));
        }
        return new JudgeContext
        {
            Targets = header.Targets, Expected = header.Expected, Links = links, TimeoutMs = header.TimeoutMs, Baseline = baseline,
            Routes = routes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<(long, RouteAt)>)kv.Value.OrderBy(x => x.Item1).ToArray()),
        };
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>회차 판정 (M4 설계 B3). 순수 함수.</summary>
public static class RoundJudge
{
    public static RoundJudgment Judge(RoundData r, JudgeContext ctx, Func<RoundData, bool> latencyHigh)
    {
        var notes = new List<string>();
        var active = ctx.Targets.Select(t => t.Id).ToArray();
        bool incomplete = !r.Gap && (active.Any(t => r.Results.All(x => x.TargetId != t)) || r.Results.Any(x => x.Outcome == ProbeOutcome.Error));
        if (r.Gap) return new RoundJudgment(r.Round, null, false, [], ["gap"]);

        var lost = r.Results.Where(x => x.Outcome == ProbeOutcome.Lost && ctx.Baseline.Contains(x.TargetId)).Select(x => x.TargetId).ToArray();
        bool anyValid = r.Results.Any(x => x.Outcome != ProbeOutcome.Error);
        if (lost.Length == 0)
        {
            if (!anyValid) return new RoundJudgment(r.Round, null, incomplete, [], ["error-only"]);
            return new RoundJudgment(r.Round, latencyHigh(r) ? Verdict.LatencyHigh : Verdict.Normal, incomplete, [], notes);
        }

        // 링크 근거: 확정 다운이 회차와 겹치는지, 인접만 하는지
        long start = r.AtMs;
        long? end = r.EndMs;
        long endBound = end ?? r.AtMs + ctx.TimeoutMs;
        if (end is null) notes.Add("end-estimated");
        // 대상마다: 쓰던 어댑터(경로 조회가 실패했으면 마지막으로 성공한 경로의 것)에 확정 물리 다운이 겹치는지
        var linkDownFor = new HashSet<string>();
        foreach (var t in lost)
        {
            var route = ctx.RouteOf(t, r.Round);
            int? idx = route is { Ok: true } ? route.InterfaceIndex : ctx.LastOkInterface(t, r.Round);
            if (idx is not int i || ctx.Links.AdapterFor(i) is not string alias) continue;
            foreach (var d in ctx.Links.Downs.Where(d => d.Adapter == alias))
            {
                bool overlapConfirmed = end is not null && d.FromMs <= end && d.ToMs >= start;
                bool overlapPossible = d.FromMs <= endBound && d.ToMs >= start;
                bool adjacent = d.FromMs <= endBound + 1000 && d.ToMs >= start - 1000;
                if (overlapConfirmed && d.Physical) linkDownFor.Add(t);
                else if (overlapConfirmed && !d.Physical) Add(notes, "link-down-virtual");
                else if (overlapPossible || adjacent) Add(notes, "link-adjacent");
            }
        }
        bool linkOverlap = linkDownFor.Count > 0;

        // 1. 경로 보류: 유형이 예상과 다르면 링크 근거와 무관하게 보류.
        //    경로 조회 실패는 그 대상의 어댑터 링크 다운이 관측됐으면 링크 다운으로 설명된다 (링크가 내려가면 경로 조회도 실패한다)
        foreach (var t in lost)
        {
            var route = ctx.RouteOf(t, r.Round);
            if (route is null) continue;
            bool mismatch = route.Ok && KindMismatch(route.Kind, ctx.Expected);
            bool unexplainedFailure = !route.Ok && !linkDownFor.Contains(t);
            if (mismatch || unexplainedFailure)
            {
                if (linkOverlap) Add(notes, "link-down");
                Add(notes, mismatch ? "route-kind-mismatch" : "route-failed");
                return new RoundJudgment(r.Round, Verdict.RouteHold, incomplete, lost, notes);
            }
        }
        if (ctx.Expected is null) Add(notes, "expected-unrecorded");

        // 2. 로컬 링크 단절 관측
        if (linkOverlap) return new RoundJudgment(r.Round, Verdict.LinkDown, incomplete, lost, notes);

        // 3·4. 외부
        var ext = ctx.BaselineExternals.Select(t => t.Id).ToArray();
        var valid = ext.Where(id => r.Results.Any(x => x.TargetId == id && x.Outcome != ProbeOutcome.Error)).ToArray();
        bool allValidLost = valid.Length > 0 && valid.All(id => lost.Contains(id));
        if (allValidLost && valid.Length == ext.Length && ctx.Diverse)
            return new RoundJudgment(r.Round, Verdict.ExternalOutage, incomplete, lost, notes);
        if (allValidLost)
        {
            Add(notes, valid.Length < ext.Length ? "external-missing" : "external-not-diverse");
            return new RoundJudgment(r.Round, Verdict.Insufficient, incomplete, lost, notes);
        }

        // 5. 대상 응답 실패
        return new RoundJudgment(r.Round, Verdict.TargetFailure, incomplete, lost, notes);
    }

    private static bool KindMismatch(string? kind, ExpectedConnection? expected) => expected switch
    {
        ExpectedConnection.Wired => kind is not null && kind != "wired",
        ExpectedConnection.Wireless => kind is not null && kind != "wireless",
        _ => false,
    };

    private static void Add(List<string> notes, string note)
    {
        if (!notes.Contains(note)) notes.Add(note);
    }
}
