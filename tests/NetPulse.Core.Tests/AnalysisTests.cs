using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Analysis;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Tests;

/// <summary>판정·구간·후보·등급 (M4 설계 B). 기획서 "수용 기준의 원칙"의 경계 사례.</summary>
public sealed class AnalysisTests
{
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    private static readonly Target Router = new("router", "공유기", IPAddress.Parse("192.0.2.254"), TargetRole.Local, null, ProbeMethod.Icmp);
    private static readonly Target ExtA = new("a", "A", IPAddress.Parse("198.51.100.1"), TargetRole.External, "A사", ProbeMethod.Icmp);
    private static readonly Target ExtB = new("b", "B", IPAddress.Parse("203.0.113.1"), TargetRole.External, "B사", ProbeMethod.Icmp);

    private static RecordHeader Header(IReadOnlyList<Target>? targets = null, ExpectedConnection? expected = ExpectedConnection.Any) =>
        new("m", 0, "t", W0, 0, targets ?? [Router, ExtA, ExtB], 1000, 800, 600_000, "primary", expected);

    /// <summary>대상별 패턴: o 성공(지연 rtt), x 손실, e 오류, _ 결과 없음. 한 글자 = 한 회차. gapRounds의 회차는 공백.</summary>
    private static List<RoundData> Build(IReadOnlyDictionary<string, string> patterns, double rtt = 10, ISet<long>? gapRounds = null, bool withEnd = true)
    {
        int n = patterns.Values.Max(p => p.Length);
        var list = new List<RoundData>();
        for (int r = 0; r < n; r++)
        {
            var results = new List<TargetResult>();
            foreach (var (id, p) in patterns)
            {
                char c = r < p.Length ? p[r] : 'o';
                switch (c)
                {
                    case 'o': results.Add(new(id, ProbeOutcome.Success, rtt, null)); break;
                    case 'x': results.Add(new(id, ProbeOutcome.Lost, null, null)); break;
                    case 'e': results.Add(new(id, ProbeOutcome.Error, null, "E")); break;
                }
            }
            long at = r * 1000L;
            list.Add(new RoundData(r, at, at, W0.AddMilliseconds(at), 0, gapRounds?.Contains(r) ?? false, results, withEnd ? at + 50 : null));
        }
        return list;
    }

    private static Dictionary<string, string> P(string router, string a, string b) => new() { ["router"] = router, ["a"] = a, ["b"] = b };

    private static string Ok(int n) => new('o', n);

    private static AnalysisResult Analyze(List<RoundData> rounds, IReadOnlyList<JsonObject>? events = null, RecordHeader? header = null, IReadOnlyList<GapSpan>? gaps = null,
        RecordSummary? summary = null) =>
        MeasurementAnalysis.Analyze(header ?? Header(), rounds, gaps ?? [], events ?? [], summary);

    private static Verdict? V(AnalysisResult a, long round) => a.Rounds.Single(r => r.Round == round).Verdict;

    // 이벤트 만들기
    private static JsonObject Route(long round, string target, bool ok, int? index = 7, string? kind = "wired")
    {
        var o = new JsonObject { ["id"] = "m", ["type"] = "route", ["round"] = round, ["at"] = round * 1000, ["wall"] = RecordSchema.Iso(W0), ["target"] = target,
            ["status"] = ok ? "ok" : "failed", ["interfaceIndex"] = ok ? index : null, ["kind"] = ok ? kind : null };
        return o;
    }

    private static IEnumerable<JsonObject> Routes(long round, int index = 7, string kind = "wired") =>
        new[] { "router", "a", "b" }.Select(t => Route(round, t, true, index, kind));

    private static JsonObject LinkEvent(string collection, long eventMs, string kind = "wired", bool isVirtual = false, JsonObject? extra = null)
    {
        var o = new JsonObject { ["id"] = "m", ["type"] = "link", ["at"] = eventMs, ["wall"] = RecordSchema.Iso(W0), ["source"] = "direct", ["adapter"] = "a1",
            ["interfaceIndex"] = 7, ["kind"] = kind, ["virtual"] = isVirtual, ["collection"] = collection, ["eventMs"] = eventMs };
        if (extra is not null) foreach (var (k, v) in extra.ToArray()) { extra.Remove(k); o[k] = v; }
        return o;
    }

    private static JsonObject LinkSummary(double coverage = 1.0) => new()
    {
        ["id"] = "m", ["type"] = "link", ["at"] = 0, ["wall"] = RecordSchema.Iso(W0), ["source"] = "direct", ["adapter"] = "a1", ["interfaceIndex"] = 7,
        ["kind"] = "wired", ["virtual"] = false, ["collection"] = "summary", ["capability"] = "supported", ["coverage"] = coverage,
    };

    // ---------------- 회차 판정 ----------------

    [Fact]
    public void ExternalOutageWithRouterUpPointsAfterRouter()
    {
        var a = Analyze(Build(P("ooooo", "ooxxo", "ooxxo")));
        Assert.Equal(Verdict.ExternalOutage, V(a, 2));
        var ep = Assert.Single(a.Episodes);
        Assert.False(ep.Mixed);
        Assert.Equal(CandidateKind.AfterRouter, ep.Parts[0].Candidate.Kind);
        Assert.Equal((1000L, 3000L), (ep.MinDurationMs, ep.MaxDurationMs!.Value));
    }

    [Fact]
    public void EverythingLostPointsToPcOrRouter()
    {
        var a = Analyze(Build(P("ooxxo", "ooxxo", "ooxxo")));
        Assert.Equal(Verdict.ExternalOutage, V(a, 2));
        Assert.Equal(CandidateKind.PcToRouterOrRouter, a.Episodes[0].Parts[0].Candidate.Kind);
    }

    [Fact]
    public void NearFailsFarSucceedsIsOnlyTargetFailure()
    {
        // 가까운 쪽 실패·먼 쪽 성공: 구간을 단정하지 않는다
        var a = Analyze(Build(P("ooxxo", "ooooo", "ooooo")));
        Assert.Equal(Verdict.TargetFailure, V(a, 2));
        Assert.Equal(["router"], a.Rounds.Single(r => r.Round == 2).FailedTargets);
        Assert.Equal(CandidateKind.None, a.Episodes[0].Parts[0].Candidate.Kind);
    }

    [Fact]
    public void OneExternalDownIsNotInternetOutage()
    {
        var a = Analyze(Build(P("ooooo", "ooxxo", "ooooo")));
        Assert.Equal(Verdict.TargetFailure, V(a, 2));
    }

    [Fact]
    public void LossPlusErrorOnExternalsIsInsufficient()
    {
        // 외부 셋 중 둘 손실·하나 오류 → 근거 부족 (1-6)
        var c = new Target("c", "C", IPAddress.Parse("203.0.113.9"), TargetRole.External, "C사", ProbeMethod.Icmp);
        var rounds = Build(new Dictionary<string, string> { ["router"] = "ooooo", ["a"] = "ooxoo", ["b"] = "ooxoo", ["c"] = "ooeoo" });
        var a = Analyze(rounds, header: Header([Router, ExtA, ExtB, c]));
        var j = a.Rounds.Single(r => r.Round == 2);
        Assert.Equal(Verdict.Insufficient, j.Verdict);
        Assert.True(j.Incomplete);
        Assert.Contains("external-missing", j.Notes);
    }

    [Theory]
    [InlineData("A사", "A사", "203.0.113.1")]          // 같은 사업자
    [InlineData("A사", "B사", "198.51.100.1")]         // 같은 주소
    [InlineData(null, "B사", "203.0.113.1")]           // 사업자 미기재
    public void NotDiverseExternalsCannotProveOutage(string? providerA, string providerB, string addressB)
    {
        var a2 = ExtA with { Provider = providerA };
        var b2 = ExtB with { Provider = providerB, Address = IPAddress.Parse(addressB) };
        var a = Analyze(Build(P(Ok(70), "oo" + "xx" + Ok(66), "oo" + "xx" + Ok(66))), header: Header([Router, a2, b2]));
        Assert.Equal(Verdict.Insufficient, V(a, 2));
        Assert.Contains("external-not-diverse", a.Rounds.Single(r => r.Round == 2).Notes);
        Assert.Equal((Grade.Insufficient, "not-diverse"), (a.Grade.Grade, a.Grade.Reason));
    }

    [Fact]
    public void TargetWithoutBaselineIsExcludedButShown()
    {
        // 공유기가 한 번도 응답하지 않음 (Ping을 막는 장비일 수 있음)
        var a = Analyze(Build(P("xxxxx", "ooxxo", "ooxxo")));
        Assert.Equal(["router"], a.NoBaselineTargets);
        Assert.Equal(Verdict.Normal, V(a, 0));            // 기준선 없는 대상의 손실은 판정에 안 들어감
        Assert.Equal(Verdict.ExternalOutage, V(a, 2));
        Assert.Equal("no-local-baseline", a.Episodes[0].Parts[0].Candidate.Reason);
        Assert.Contains("no-baseline-target", a.Grade.Notes);
    }

    [Fact]
    public void AllTargetsNeverAnsweredMeansStartedDisconnected()
    {
        var a = Analyze(Build(P("xxxxx", "xxxxx", "xxxxx")));
        Assert.True(a.StartedDisconnected);
        Assert.Equal((Grade.Undecidable, "started-disconnected"), (a.Grade.Grade, a.Grade.Reason));
        Assert.All(a.Rounds, r => Assert.Equal(Verdict.Normal, r.Verdict));   // 기준선 있는 대상이 없으니 손실 판정 없음
    }

    [Fact]
    public void RouteKindMismatchHoldsJudgment()
    {
        var events = Routes(0).Concat([Route(2, "a", true, 9, "wireless")]).ToList();
        var a = Analyze(Build(P("ooooo", "ooxxo", "ooxxo")), events, Header(expected: ExpectedConnection.Wired));
        var j = a.Rounds.Single(r => r.Round == 2);
        Assert.Equal(Verdict.RouteHold, j.Verdict);
        Assert.Contains("route-kind-mismatch", j.Notes);
        // 예상이 "상관없음"이면 유형을 비교하지 않는다
        Assert.Equal(Verdict.ExternalOutage, V(Analyze(Build(P("ooooo", "ooxxo", "ooxxo")), events, Header(expected: ExpectedConnection.Any)), 2));
    }

    [Fact]
    public void RouteFailureWithoutLinkEvidenceHolds()
    {
        var events = Routes(0).Concat([Route(2, "a", false)]).ToList();
        var a = Analyze(Build(P("ooooo", "ooxxo", "ooxxo")), events);
        Assert.Equal(Verdict.RouteHold, V(a, 2));
        Assert.Contains("route-failed", a.Rounds.Single(r => r.Round == 2).Notes);
    }

    [Fact]
    public void PhysicalLinkDownOverlappingRoundIsLocalLinkDown()
    {
        // 링크가 내려가면 경로 조회도 실패한다: 쓰던 어댑터의 확정 다운으로 설명되면 링크 단절 (경로 보류 아님)
        var events = Routes(0).Concat(new[] { "router", "a", "b" }.Select(t => Route(2, t, false)))
            .Concat([LinkEvent("up", 0), LinkEvent("down", 1900), LinkEvent("up", 4100), LinkSummary()]).ToList();
        var a = Analyze(Build(P("ooxxo", "ooxxo", "ooxxo")), events);
        Assert.Equal(Verdict.LinkDown, V(a, 2));
        Assert.Equal(Verdict.LinkDown, V(a, 3));
        var part = Assert.Single(a.Episodes).Parts[0];
        Assert.Equal((CandidateKind.PcLink, "wired"), (part.Candidate.Kind, part.Candidate.LinkKind));
    }

    [Fact]
    public void RouteFailureWithAdjacentPhysicalDownIsLinkDownButRouteOkNeedsOverlap()
    {
        // 직접 관측은 회차가 끝난 뒤 읽는다: 첫 다운 회차와는 "인접"으로만 잡힌다 (다운 처음 관측 2100 > 회차 끝 2050).
        // 같은 회차에 경로 조회도 실패했으면 그 실패가 함께 근거 → 링크 단절 (M4 구현 판정)
        var failed = Routes(0).Concat(new[] { "router", "a", "b" }.Select(t => Route(2, t, false)))
            .Concat([LinkEvent("up", 0), LinkEvent("down", 2100), LinkEvent("up", 2900), LinkSummary()]).ToList();
        Assert.Equal(Verdict.LinkDown, V(Analyze(Build(P("ooxoo", "ooxoo", "ooxoo")), failed), 2));
        // 경로가 멀쩡하면 인접만으로는 링크 단절이 아니다
        var ok = Routes(0).Concat([LinkEvent("up", 0), LinkEvent("down", 2100), LinkEvent("up", 2900), LinkSummary()]).ToList();
        Assert.Equal(Verdict.ExternalOutage, V(Analyze(Build(P("ooxoo", "ooxoo", "ooxoo")), ok), 2));
    }

    [Fact]
    public void VirtualAdapterDownIsOnlyAFact()
    {
        var events = Routes(0).Concat([LinkEvent("up", 0, "tunnel", true), LinkEvent("down", 1900, "tunnel", true), LinkEvent("up", 4100, "tunnel", true), LinkSummary()]).ToList();
        var a = Analyze(Build(P("ooxxo", "ooxxo", "ooxxo")), events);
        Assert.Equal(Verdict.ExternalOutage, V(a, 2));
        Assert.Contains("link-down-virtual", a.Rounds.Single(r => r.Round == 2).Notes);
    }

    [Fact]
    public void AdjacentOnlyLinkDownIsNotLocalLinkDown()
    {
        // 회차 2: [2000, 2050]. 다운이 2600~2700 (끝 + 1초 안): 인접 근거로만
        var events = Routes(0).Concat([LinkEvent("up", 0), LinkEvent("down", 2600), LinkEvent("up", 2700), LinkSummary()]).ToList();
        var a = Analyze(Build(P("ooxoo", "ooxoo", "ooxoo")), events);
        Assert.Equal(Verdict.ExternalOutage, V(a, 2));
        Assert.Contains("link-adjacent", a.Rounds.Single(r => r.Round == 2).Notes);
    }

    [Fact]
    public void EstimatedRoundEndDoesNotConfirmLinkDown()
    {
        // 옛 기록(회차 끝 없음): 추정 범위에서만 겹치는 다운은 인접 근거 (2-4)
        var events = Routes(0).Concat([LinkEvent("up", 0), LinkEvent("down", 2300), LinkEvent("up", 2400), LinkSummary()]).ToList();
        var a = Analyze(Build(P("ooxoo", "ooxoo", "ooxoo"), withEnd: false), events);
        var j = a.Rounds.Single(r => r.Round == 2);
        Assert.Equal(Verdict.ExternalOutage, j.Verdict);
        Assert.Contains("end-estimated", j.Notes);
        Assert.Contains("link-adjacent", j.Notes);
    }

    [Fact]
    public void ErrorOnlyRoundIsIncompleteWithoutVerdict()
    {
        var a = Analyze(Build(P("ooeoo", "ooeoo", "ooeoo")));
        var j = a.Rounds.Single(r => r.Round == 2);
        Assert.Null(j.Verdict);
        Assert.True(j.Incomplete);
        Assert.Empty(a.Episodes);
    }

    // ---------------- 구간 ----------------

    [Fact]
    public void MixedEpisodeIsSummarizedAsMixedAndGradedBySubRuns()
    {
        // 외부 단절 1회차 + 대상 응답 실패 39회차: 전체를 외부 단절 40초로 보지 않는다 (1-8)
        string router = Ok(100);
        string a = Ok(10) + "x" + new string('x', 39) + Ok(50);
        string b = Ok(10) + "x" + Ok(89);
        var r = Analyze(Build(P(router, a, b)));
        var ep = Assert.Single(r.Episodes);
        Assert.True(ep.Mixed);
        Assert.Null(ep.Summary);
        Assert.Equal([Verdict.ExternalOutage, Verdict.TargetFailure], ep.Parts.Select(p => p.Verdict));
        var outage = r.Stats.Single(s => s.Verdict == Verdict.ExternalOutage);
        Assert.Equal((1, 0L), (outage.Count, outage.LongestMinMs));
        Assert.Equal(Grade.VeryBad, r.Grade.Grade);   // 외부 손실률 20% (a 40회) → 매우 나쁨, 그러나 "30초 이상 단절" 사유는 아님
        Assert.Equal("external-loss-high", r.Grade.Reason);
    }

    [Fact]
    public void EpisodesBreakOnGapAndHaveOpenEnds()
    {
        var rounds = Build(P("oooooo", "oxxxxo", "oxxxxo"), gapRounds: new HashSet<long> { 3 });
        var a = Analyze(rounds);
        Assert.Equal(2, a.Episodes.Count);
        Assert.Null(a.Episodes[0].MaxDurationMs);   // 뒤쪽이 공백 → 열린 끝
        Assert.Null(a.Episodes[1].MaxDurationMs);
        var startsLost = Analyze(Build(P("ooo", "xxo", "xxo")));
        Assert.Null(startsLost.Episodes[0].MaxDurationMs);   // 앞쪽 성공 없음
    }

    // ---------------- 등급 ----------------

    [Fact]
    public void CleanMeasurementIsGood()
    {
        var a = Analyze(Build(P(Ok(120), Ok(120), Ok(120))), [LinkSummary()]);
        Assert.Equal((Grade.Good, "good"), (a.Grade.Grade, a.Grade.Reason));
        Assert.Equal("측정하는 동안 끊김이 관측되지 않았습니다.", a.Grade.Sentence);
        Assert.DoesNotContain("no-link-evidence", a.Grade.Notes);
    }

    [Fact]
    public void GoodWithoutLinkEvidenceSaysSo()
    {
        var a = Analyze(Build(P(Ok(120), Ok(120), Ok(120))));
        Assert.Equal(Grade.Good, a.Grade.Grade);
        Assert.Contains("no-link-evidence", a.Grade.Notes);
    }

    [Fact]
    public void MostlyErrorsIsInsufficientNotGood()
    {
        // 1판 반례: 첫 회차만 성공하고 나머지 99회차 검사 오류 → 관측 부족 (1-9)
        var a = Analyze(Build(P("o" + new string('e', 99), "o" + new string('e', 99), "o" + new string('e', 99))));
        Assert.Equal((Grade.Insufficient, "too-few-rounds"), (a.Grade.Grade, a.Grade.Reason));
    }

    [Fact]
    public void ManySkippedRoundsAreLowCoverage()
    {
        // 2판 반례: 60회 모두 성공했지만 사이마다 9회씩 건너뜀 → 관측해야 했던 회차 600 중 60 (2-5)
        var rounds = Build(P(Ok(60), Ok(60), Ok(60))).Select((r, i) => r with { Round = i * 10L, Skipped = i == 0 ? 0 : 9 }).ToList();
        var a = Analyze(rounds);
        Assert.Equal((Grade.Insufficient, "low-coverage"), (a.Grade.Grade, a.Grade.Reason));
        Assert.Equal(591, a.Coverage.ExpectedRounds);
    }

    [Fact]
    public void SummaryObservedBeyondRereadRoundsCountsAsMissing()
    {
        // 저장 누락: 요약은 마지막 확정 회차 199, 원본은 앞 100회차뿐 → 관측해야 했던 200 중 100
        var a = Analyze(Build(P(Ok(100), Ok(100), Ok(100))),
            summary: new RecordSummary("m", EndReason.Stopped, W0, 200, 200, 0, 0, 0, 199, false, null, 0));
        Assert.Equal((Grade.Insufficient, "low-coverage"), (a.Grade.Grade, a.Grade.Reason));
    }

    [Fact]
    public void SuspendGapRoundsAreNotRequired()
    {
        // 절전 공백은 관측해야 했던 회차에서 뺀다
        var rounds = Build(P(Ok(100), Ok(100), Ok(100))).Select((r, i) => i >= 50 ? r with { Round = r.Round + 300 } : r).ToList();
        rounds[50] = rounds[50] with { Skipped = 300 };
        var gap = new GapSpan(GapReason.Suspend, 49_500, 349_500, W0, W0.AddMinutes(5), 49, 350);
        var a = Analyze(rounds, gaps: [gap]);
        Assert.Equal(Grade.Good, a.Grade.Grade);
    }

    [Fact]
    public void ExternalOutagesDriveBadGrades()
    {
        string ok = Ok(120);
        string one = Ok(50) + "xx" + Ok(68);
        var bad = Analyze(Build(P(ok, one, one)));
        Assert.Equal((Grade.Bad, "outage"), (bad.Grade.Grade, bad.Grade.Reason));
        Assert.Contains("1번", bad.Grade.Sentence);

        string three = Ok(20) + "x" + Ok(20) + "x" + Ok(20) + "x" + Ok(57);
        Assert.Equal("outage-many", Analyze(Build(P(ok, three, three))).Grade.Reason);

        string longOne = Ok(20) + new string('x', 35) + Ok(65);
        var veryBad = Analyze(Build(P(ok, longOne, longOne)));
        Assert.Equal(Grade.VeryBad, veryBad.Grade.Grade);
    }

    [Fact]
    public void UndecidableWhenHoldsDominate()
    {
        var events = Routes(0).Concat(Enumerable.Range(50, 4).Select(r => Route(r, "a", true, 9, "wireless"))).Concat([Route(54, "a", true, 7, "wired")]).ToList();
        string ok = Ok(120);
        string loss = Ok(50) + "xxxx" + Ok(66);
        var a = Analyze(Build(P(ok, loss, loss)), events, Header(expected: ExpectedConnection.Wired));
        Assert.Equal((Grade.Undecidable, "undecidable"), (a.Grade.Grade, a.Grade.Reason));
    }

    [Fact]
    public void MissingExpectedAndDroppedEventsAreNoted()
    {
        var a = Analyze(Build(P(Ok(100), Ok(100), Ok(100))), header: Header(expected: null),
            summary: new RecordSummary("m", EndReason.Stopped, W0, 100, 100, 0, 0, 0, 99, false, null, null));
        Assert.Contains("expected-unrecorded", a.Grade.Notes);
        Assert.Contains("events-dropped-unrecorded", a.Grade.Notes);
    }

    // ---------------- 지연 기준선 ----------------

    [Fact]
    public void LatencyBaselineNeedsTwentySamplesAndUsesMedianThreshold()
    {
        var few = Analyze(Build(P(Ok(19), Ok(19), Ok(19))));
        Assert.All(few.Latency, l => Assert.Null(l.MedianMs));

        var rounds = Build(P(Ok(100), Ok(100), Ok(100)), rtt: 10);
        // 회차 60에서 a 지연 50 ms: 기준 10 + max(20, 30) = 40 이상 → 지연 상승
        rounds[60] = rounds[60] with { Results = rounds[60].Results.Select(x => x.TargetId == "a" ? x with { RttMs = 50 } : x).ToArray() };
        rounds[61] = rounds[61] with { Results = rounds[61].Results.Select(x => x.TargetId == "a" ? x with { RttMs = 39 } : x).ToArray() };
        var a = Analyze(rounds);
        Assert.Equal(40, a.Latency.Single(l => l.TargetId == "a").ThresholdMs);
        Assert.Equal(Verdict.LatencyHigh, V(a, 60));
        Assert.Equal(Verdict.Normal, V(a, 61));
    }

    [Fact]
    public void SustainedDegradationIsNotCaughtButVisibleInWindows()
    {
        // 지속 악화(10 ms 20회 → 100 ms 80회)는 이상치가 아니다 (한계, 1-10). 5분 창 중앙값으로 보인다
        var rounds = Build(P(Ok(100), Ok(100), Ok(100)), rtt: 10);
        for (int i = 20; i < 100; i++) rounds[i] = rounds[i] with { Results = rounds[i].Results.Select(x => x with { RttMs = 100 }).ToArray() };
        var a = Analyze(rounds);
        Assert.DoesNotContain(a.Rounds, r => r.Verdict == Verdict.LatencyHigh);
        Assert.NotEmpty(a.Latency.Single(l => l.TargetId == "a").WindowMedians);
    }

    [Fact]
    public void ProvisionalAndVersionAreCarried()
    {
        var a = MeasurementAnalysis.Analyze(Header(), Build(P(Ok(5), Ok(5), Ok(5))), [], [], null, provisional: true);
        Assert.True(a.Provisional);
        Assert.Equal(1, a.AnalyzerVersion);
    }
}
