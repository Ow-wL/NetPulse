using NetPulse.Core.Analysis;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Presentation;

/// <summary>맵에서 구간 후보가 가리키는 곳 (M4 <see cref="CandidateKind"/>를 그대로 옮김).</summary>
public enum MapCandidate { None, PcToLocal, LocalToInternet, PcToLocalAndLocalNode }

/// <summary>노드 상태 (관측 사실).</summary>
public enum NodeState { Unknown, Responding, NotResponding, NotMeasured, NoBaseline, LinkDownObserved }

public sealed record MapNode(string Name, string Detail, NodeState State);

/// <summary>
/// 경로 맵 한 장면 (M5 설계 3.5, N13). 노드·선은 관측 사실이고, 후보만 판단이며 M4 분석 결과에서만 온다.
/// </summary>
/// <param name="LocalConfirmed">로컬 대상이 최근 5회차 안에 응답 (선 PC–로컬을 실선으로). 경로 증명이 아니다</param>
/// <param name="InternetConfirmed">외부 대상 하나라도 최근 5회차 안에 응답</param>
/// <param name="CandidateNote">후보가 없을 때의 이유 또는 혼합 구간 설명 (없으면 null)</param>
/// <param name="MixedParts">끝난 측정의 가장 긴 구간이 혼합이면 하위 구간 후보를 시간 순서대로</param>
public sealed record PathMapView(
    MapNode Pc, MapNode Local, MapNode Internet, bool LocalConfirmed, bool InternetConfirmed,
    MapCandidate Candidate, string? CandidateNote, IReadOnlyList<MapCandidate> MixedParts, string AccessibleText)
{
    public const string ConceptNote = "개념도 · 실제 경로와 다를 수 있음";
    public const string LegendNote = "응답 확인은 그 대상이 응답했다는 뜻이며, 패킷이 이 경로로 지나갔다는 증명이 아닙니다.";
}

/// <summary>
/// 경로 맵의 값 (순수). 1판은 최근 연속 손실만으로 구간을 칠했지만, 그것은 M4 후보 조건(응답 기준선·외부 다양성·같은 인터페이스 경로)을
/// 건너뛰는 더 강한 판단이었다 (설계 검토 1-1). 2판부터 화면은 구간을 따로 판단하지 않는다.
/// </summary>
public static class PathMapModel
{
    public const int RecentRounds = 5;

    /// <summary>측정하지 않을 때: 구조만 그리고 선은 모두 점선.</summary>
    public static PathMapView Idle(IReadOnlyList<Target> targets, string? connectionKind)
    {
        var local = targets.FirstOrDefault(t => t.Role == TargetRole.Local);
        var pc = new MapNode("이 PC", KindText(connectionKind), NodeState.Unknown);
        var localNode = local is null ? new MapNode("로컬 대상", "측정하지 않음", NodeState.NotMeasured)
            : new MapNode(LocalName(targets), "측정 전", NodeState.Unknown);
        int ext = targets.Count(t => t.Role == TargetRole.External);
        var internet = new MapNode("인터넷", ext == 0 ? "측정하지 않음" : $"외부 대상 {ext}개", ext == 0 ? NodeState.NotMeasured : NodeState.Unknown);
        return new PathMapView(pc, localNode, internet, false, false, MapCandidate.None, null, [],
            $"{PathMapView.ConceptNote}. 측정하지 않는 중.");
    }

    /// <summary>
    /// 측정 중 또는 끝난 뒤. <paramref name="analysis"/>는 잠정(측정 중) 또는 확정 분석, 없으면(첫 분석 전) 후보 없음.
    /// <paramref name="links"/>는 측정 중 직접 관측 (끝난 뒤면 null).
    /// </summary>
    public static PathMapView From(IReadOnlyList<Target> targets, IReadOnlyList<RoundData> rounds, string? connectionKind,
        AnalysisResult? analysis, LinkLive? links, bool ended)
    {
        var ordered = rounds.Where(r => !r.Gap).OrderBy(r => r.Round).ToArray();
        var recent = ordered.Skip(Math.Max(0, ordered.Length - RecentRounds)).ToArray();
        var localTargets = targets.Where(t => t.Role == TargetRole.Local).ToArray();
        var externals = targets.Where(t => t.Role == TargetRole.External).ToArray();
        var first = localTargets.FirstOrDefault();

        bool linkDown = !ended && links?.Adapters.Any(a => a.State == LinkState.Down) == true;
        var pc = new MapNode("이 PC", linkDown ? "링크 끊김 관측" : KindText(connectionKind), linkDown ? NodeState.LinkDownObserved : NodeState.Unknown);

        MapNode localNode;
        bool localConfirmed = false;
        if (first is null) localNode = new MapNode("로컬 대상", "측정하지 않음", NodeState.NotMeasured);
        else
        {
            localConfirmed = recent.Any(r => Outcome(r, first.Id) == ProbeOutcome.Success);
            var latest = ordered.LastOrDefault();
            string name = LocalName(targets);
            if (analysis?.NoBaselineTargets.Contains(first.Id) == true && !localConfirmed)
                localNode = new MapNode(name, "응답 기준선 없음", NodeState.NoBaseline);
            else if (latest is not null && Outcome(latest, first.Id) == ProbeOutcome.Success && Rtt(latest, first.Id) is double ms)
                localNode = new MapNode(name, Ms(ms), NodeState.Responding);
            else
            {
                int lost = ConsecutiveLoss(ordered, first.Id);
                localNode = lost > 0 ? new MapNode(name, $"응답 없음 {lost}회 연속", NodeState.NotResponding)
                    : new MapNode(name, latest is null ? "측정 전" : "검사 오류", NodeState.Unknown);
            }
        }

        int responded = recent.Length == 0 ? 0 : externals.Count(t => Outcome(recent[^1], t.Id) == ProbeOutcome.Success);
        bool internetConfirmed = recent.Any(r => externals.Any(t => Outcome(r, t.Id) == ProbeOutcome.Success));
        string internetDetail;
        NodeState internetState;
        if (externals.Length == 0) { internetDetail = "측정하지 않음"; internetState = NodeState.NotMeasured; }
        else if (recent.Length == 0) { internetDetail = $"외부 대상 {externals.Length}개"; internetState = NodeState.Unknown; }
        else
        {
            var rep = TimelineModel.Representative(recent[^1], targets).Internet;
            internetDetail = $"{externals.Length}개 중 {responded}개 응답" + (rep is double v ? " · " + Ms(v) : "");
            internetState = responded > 0 ? NodeState.Responding : NodeState.NotResponding;
        }
        var internet = new MapNode("인터넷", internetDetail, internetState);

        var (candidate, note, mixed) = PickCandidate(analysis, ended);
        var text = Accessible(localNode, internet, candidate, note, mixed, linkDown);
        return new PathMapView(pc, localNode, internet, localConfirmed, internetConfirmed, candidate, note, mixed, text);
    }

    /// <summary>
    /// 후보 고르기 (설계 검토 2-3). 후보는 끊김 구간이 아니라 하위 구간에 있다.
    /// 측정 중: 진행 중인 구간(뒤쪽 성공 없음)의 마지막 하위 구간. 끝난 뒤: 가장 긴 구간(최소 길이, 같으면 먼저 것), 혼합이면 고르지 않는다.
    /// </summary>
    public static (MapCandidate Candidate, string? Note, IReadOnlyList<MapCandidate> Mixed) PickCandidate(AnalysisResult? analysis, bool ended)
    {
        if (analysis is null) return (MapCandidate.None, null, []);
        if (!ended)
        {
            var ongoing = analysis.Episodes.LastOrDefault(e => e.MaxDurationMs is null && e.Parts.Count > 0);
            if (ongoing is null || !IsTail(analysis, ongoing)) return (MapCandidate.None, "지금은 끊김이 관측되지 않습니다.", []);
            return FromCandidate(ongoing.Parts[^1].Candidate);
        }
        var longest = analysis.Episodes.Where(e => e.Parts.Count > 0)
            .OrderByDescending(e => e.MinDurationMs).ThenBy(e => e.FirstRound).FirstOrDefault();
        if (longest is null) return (MapCandidate.None, "끊김이 관측되지 않았습니다.", []);
        if (longest.Mixed)
        {
            var parts = new List<MapCandidate>();
            foreach (var p in longest.Parts)
            {
                var c = Map(p.Candidate.Kind);
                if (parts.Count == 0 || parts[^1] != c) parts.Add(c);
            }
            return (MapCandidate.None, "여러 종류가 섞인 끊김이라 한 곳을 고르지 않습니다: " + MixedText(parts), parts);
        }
        return FromCandidate(longest.Parts[^1].Candidate);
    }

    /// <summary>진행 중인 구간이 마지막 판정 회차까지 닿는지 (뒤에 성공 판정이 없다).</summary>
    private static bool IsTail(AnalysisResult a, Episode e)
    {
        var lastJudged = a.Rounds.LastOrDefault(r => r.Verdict is not null);
        return lastJudged is null || lastJudged.Round <= e.LastRound;
    }

    private static (MapCandidate, string?, IReadOnlyList<MapCandidate>) FromCandidate(Candidate c)
    {
        var kind = Map(c.Kind);
        return kind == MapCandidate.None ? (kind, ReasonText(c.Reason), []) : (kind, null, []);
    }

    public static MapCandidate Map(CandidateKind k) => k switch
    {
        CandidateKind.PcLink => MapCandidate.PcToLocal,
        CandidateKind.AfterRouter => MapCandidate.LocalToInternet,
        CandidateKind.PcToRouterOrRouter => MapCandidate.PcToLocalAndLocalNode,
        _ => MapCandidate.None,
    };

    /// <summary>후보가 없는 이유 (M4 Candidate.Reason 코드).</summary>
    public static string ReasonText(string? reason) => reason switch
    {
        "no-local" => "로컬 대상을 측정하지 않아 구간을 판단하지 않습니다.",
        "no-local-baseline" => "로컬 대상의 응답 기준선이 없어 구간을 판단하지 않습니다.",
        "no-route" or "route-failed" => "경로를 확인하지 못해 구간을 판단하지 않습니다.",
        "different-interface" => "대상들이 서로 다른 연결을 써서 구간을 판단하지 않습니다.",
        "local-mixed" => "로컬 대상 응답이 엇갈려 구간을 판단하지 않습니다.",
        "single-target" => "외부 대상이 하나뿐이라 구간을 판단하지 않습니다.",
        "route-hold" => "경로가 예상과 달라 구간을 판단하지 않습니다.",
        "insufficient" => "관측이 부족해 구간을 판단하지 않습니다.",
        null => "구간을 판단할 근거가 없습니다.",
        _ => "구간을 판단하지 않습니다.",
    };

    public static string PlaceText(MapCandidate c) => c switch
    {
        MapCandidate.PcToLocal => "이 PC 쪽 링크",
        MapCandidate.LocalToInternet => "공유기 너머",
        MapCandidate.PcToLocalAndLocalNode => "이 PC와 공유기 사이 또는 공유기",
        _ => "판단하지 않음",
    };

    private static string MixedText(IReadOnlyList<MapCandidate> parts) =>
        string.Join(", 이어서 ", parts.Select(PlaceText).Select((t, i) => i == 0 ? "먼저 " + t : t));

    private static string Accessible(MapNode local, MapNode internet, MapCandidate c, string? note, IReadOnlyList<MapCandidate> mixed, bool linkDown)
    {
        var parts = new List<string> { "개념도" };
        if (linkDown) parts.Add("이 PC 링크 끊김 관측");
        parts.Add($"{local.Name} {local.Detail}");
        parts.Add($"인터넷 {internet.Detail}");
        if (c != MapCandidate.None) parts.Add("가능성이 높은 곳: " + PlaceText(c));
        else if (note is not null) parts.Add(note);
        return string.Join(". ", parts) + ".";
    }

    /// <summary>로컬 대상 이름: 사용자가 정한 첫 로컬 대상 이름 (여럿이면 "외 n개").</summary>
    public static string LocalName(IReadOnlyList<Target> targets)
    {
        var locals = targets.Where(t => t.Role == TargetRole.Local).ToArray();
        return locals.Length switch { 0 => "로컬 대상", 1 => locals[0].Name, _ => $"{locals[0].Name} 외 {locals.Length - 1}개" };
    }

    public static string KindText(string? kind) => kind switch { "wired" => "유선", "wireless" => "Wi‑Fi", _ => "연결 종류 알 수 없음" };

    public static string Ms(double ms) => ms < 10 ? $"{ms:0.#} ms" : $"{ms:0} ms";

    private static ProbeOutcome? Outcome(RoundData r, string id) => r.Results.FirstOrDefault(x => x.TargetId == id)?.Outcome;
    private static double? Rtt(RoundData r, string id) => r.Results.FirstOrDefault(x => x.TargetId == id)?.RttMs;

    private static int ConsecutiveLoss(RoundData[] ordered, string id)
    {
        int n = 0;
        for (int i = ordered.Length - 1; i >= 0; i--)
        {
            if (i < ordered.Length - 1 && ordered[i].Round != ordered[i + 1].Round - 1) break;   // 누락을 건너 세지 않는다
            if (Outcome(ordered[i], id) != ProbeOutcome.Lost) break;
            n++;
        }
        return n;
    }

    /// <summary>가장 최근 경로의 연결 종류 (경로 이벤트의 kind). 없으면 null.</summary>
    public static string? ConnectionKind(IEnumerable<System.Text.Json.Nodes.JsonObject> events)
    {
        string? kind = null;
        foreach (var e in events)
            if ((string?)e["type"] == "route" && (string?)e["status"] == "ok" && e["kind"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var k))
                kind = k;
        return kind;
    }
}
