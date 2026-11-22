using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Analysis;
using NetPulse.Core.Measurement;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Tests;

/// <summary>기록 상세·구간 상세·복사 (M7 설계 3.2·3.3).</summary>
public sealed class M7RecordViewTests
{
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 4, 20, 0, TimeSpan.Zero);

    // 이름에 주소·어댑터 이름을 넣은 구성: 복사에 이름이 새지 않는지 본다 [1-6]
    private static readonly Target Router = new("router", "192.0.2.254 내 라우터 Ethernet 2", IPAddress.Parse("192.0.2.254"), TargetRole.Local, null, ProbeMethod.Icmp);
    private static readonly Target ExtA = new("a", "198.51.100.1 집 회선", IPAddress.Parse("198.51.100.1"), TargetRole.External, "A사", ProbeMethod.Icmp);
    private static readonly Target ExtB = new("b", "B 서버", IPAddress.Parse("203.0.113.1"), TargetRole.External, "B사", ProbeMethod.Icmp);

    private static RecordHeader Header() => new("m", 0, "t", W0, 540, [Router, ExtA, ExtB], 1000, 800, 600_000, "primary", ExpectedConnection.Any);

    /// <summary>o 성공, x 손실, e 오류, _ 결과 없음.</summary>
    internal static List<RoundData> Build(string router, string a, string b, double rtt = 10)
    {
        var list = new List<RoundData>();
        for (int r = 0; r < router.Length; r++)
        {
            var results = new List<TargetResult>();
            foreach (var (id, p) in new[] { ("router", router), ("a", a), ("b", b) })
                switch (p[r])
                {
                    case 'o': results.Add(new(id, ProbeOutcome.Success, rtt, null)); break;
                    case 'x': results.Add(new(id, ProbeOutcome.Lost, null, null)); break;
                    case 'e': results.Add(new(id, ProbeOutcome.Error, null, "E")); break;
                }
            long at = r * 1000L;
            list.Add(new RoundData(r, at, at, W0.AddMilliseconds(at), 0, false, results, at + 50));
        }
        return list;
    }

    private static IEnumerable<JsonObject> Routes() => new[] { "router", "a", "b" }.Select(t => new JsonObject
    {
        ["id"] = "m", ["type"] = "route", ["round"] = 0, ["at"] = 0, ["wall"] = RecordSchema.Iso(W0), ["target"] = t,
        ["status"] = "ok", ["interfaceIndex"] = 7, ["kind"] = "wired",
    });

    private static JsonObject Mark(long atMs, string memo) => new()
    {
        ["id"] = "m", ["type"] = SymptomMarks.MarkType, ["mark"] = "k1", ["round"] = atMs / 1000, ["at"] = atMs, ["wall"] = RecordSchema.Iso(W0.AddMilliseconds(atMs)), ["memo"] = memo,
    };

    private static MeasurementRecord Record(List<RoundData> rounds, IReadOnlyList<JsonObject>? events = null, IReadOnlyList<GapSpan>? gaps = null, DamageCounts? damage = null) =>
        new(RecordStatus.Ok, null, Header(), rounds, gaps ?? [], events ?? Routes().ToArray(), null, EndReason.Completed,
            damage ?? new DamageCounts(0, 0, 0, 0, 0, 0, 0, 0, 0), [], null, null);

    private static AnalysisResult Analyze(MeasurementRecord r) => MeasurementAnalysis.Analyze(r.Header!, r.Rounds, r.Gaps, r.Events, r.Summary);

    private static string Ok(int n) => new('o', n);
    private static string Lost(int n) => new('x', n);

    [Fact]
    public void DetailShowsTwoRoundsAroundAndMarksEpisodeRows()
    {
        var rec = Record(Build(Ok(80), Ok(40) + Lost(3) + Ok(37), Ok(40) + Lost(3) + Ok(37)));
        var a = Analyze(rec);
        var e = Assert.Single(a.Episodes);
        var d = EpisodeDetail.For(rec, a, e);
        Assert.Equal([38L, 39, 40, 41, 42, 43, 44], d.Rows.Select(r => r.Round!.Value));
        Assert.Equal([false, false, true, true, true, false, false], d.Rows.Select(r => r.InEpisode));
        Assert.Equal(0, d.OmittedRounds);
        Assert.False(d.NothingBefore);
        var cells = d.Rows.Single(r => r.Round == 40).Cells;
        Assert.Equal([CellKind.Success, CellKind.Lost, CellKind.Lost], cells.Select(c => c.Kind));
        Assert.All(d.Routes, r => Assert.Equal("wired", r.Kind));
    }

    [Fact]
    public void LongEpisodesKeepThreeAtEachEndAndSayHowManyWereLeftOut()
    {
        var rec = Record(Build(Ok(80), Ok(30) + Lost(12) + Ok(38), Ok(30) + Lost(12) + Ok(38)));
        var a = Analyze(rec);
        var d = EpisodeDetail.For(rec, a, Assert.Single(a.Episodes));
        Assert.Equal(6, d.OmittedRounds);
        Assert.Equal(32, d.OmitAfter);
        Assert.Equal([28L, 29, 30, 31, 32, 39, 40, 41, 42, 43], d.Rows.Select(r => r.Round!.Value));
    }

    [Fact]
    public void EpisodeAtTheStartHasNothingBeforeAndGapsShowAsGapRows()
    {
        var rec0 = Record(Build(Ok(80), Lost(3) + Ok(77), Lost(3) + Ok(77)));
        var a0 = Analyze(rec0);
        if (a0.Episodes.Count > 0)
        {
            var d0 = EpisodeDetail.For(rec0, a0, a0.Episodes[0]);
            Assert.Equal(d0.Rows[0].Round == a0.Episodes[0].FirstRound, d0.NothingBefore);
        }

        // 구간 바로 뒤 공백: 공백 줄이 끼고 칸이 없다 (정상으로 그리지 않음)
        var gap = new GapSpan(GapReason.Suspend, 42_500, 43_500, W0.AddMilliseconds(42_500), W0.AddMilliseconds(43_500), 42, 43);
        var rec = Record(Build(Ok(80), Ok(40) + Lost(3) + Ok(37), Ok(40) + Lost(3) + Ok(37)), gaps: [gap]);
        var a = Analyze(rec);
        var d = EpisodeDetail.For(rec, a, a.Episodes.First());
        var gapRow = Assert.Single(d.Rows, r => r.Round is null);
        Assert.True(gapRow.Gap);
        Assert.Empty(gapRow.Cells);
    }

    [Fact]
    public void CopyUsesRolesAndOrdinalsOnlyNoNamesAddressesOrMemos()
    {
        var events = Routes().Append(Mark(41_000, "198.51.100.77 회의 중 끊김")).ToArray();
        var rec = Record(Build(Ok(80), Ok(40) + Lost(3) + Ok(37), Ok(40) + Lost(3) + Ok(37)), events);
        var a = Analyze(rec);
        var d = EpisodeDetail.For(rec, a, Assert.Single(a.Episodes));
        Assert.Single(d.Marks);   // 화면에는 겹친 증상 표시가 있다
        var text = RecordView.Copy(rec.Header!, d);
        // "공유기"는 '가능성이 높은 곳' 문구의 일반 단어라 이름 검사는 사용자 이름에만 있는 글로 한다
        foreach (var forbidden in new[] { "192.0.2", "198.51.100", "203.0.113", "내 라우터", "집 회선", "B 서버", "Ethernet", "회의 중", "A사", "B사" })
            Assert.DoesNotContain(forbidden, text);
        Assert.Contains("로컬 대상 1", text);
        Assert.Contains("외부 대상 2", text);
        Assert.Contains("응답 없음", text);
    }

    [Fact]
    public void DetailSaysNoOutageOnlyWithCoverageAndValueNoneIsNotZero()
    {
        var rec = Record(Build(Ok(80), Ok(80), new string('e', 80)));
        var a = Analyze(rec);
        var v = RecordView.Detail(rec, a, RecordLifecycle.Ended);
        Assert.Empty(v.Episodes);
        Assert.Equal("이 측정에서는 끊김이 관측되지 않았습니다.", v.NoEpisodes);
        Assert.StartsWith("판단할 수 있는 관측", v.Coverage);
        var b = v.TargetLines.Single(t => t.TargetId == "b");
        Assert.StartsWith("값 없음", b.Loss);       // 검사 오류만 → 손실률 값 없음 (0% 아님), 오류 수는 따로
        Assert.Equal("값 없음", b.Average);
        Assert.Equal("계획한 시간을 마침", v.End);
        Assert.Equal("대상 3개 · 로컬 1 · 외부 2", v.Targets);
    }

    [Fact]
    public void DamageNoticesSeparateRoundsEvidenceAndMemos()
    {
        var rounds = Build(Ok(80), Ok(80), Ok(80));
        Assert.Equal(["경로·링크 근거 2줄을 읽지 못해 끊김 상세의 근거가 빠질 수 있습니다."],
            RecordView.DamageNotices(Record(rounds, damage: new DamageCounts(0, 0, 0, 0, 0, 0, 0, 0, 2)), 0));
        Assert.Equal(["이 측정의 회차 3줄을 읽지 못해 그 회차는 관측하지 못한 것으로 셉니다."],
            RecordView.DamageNotices(Record(rounds, damage: new DamageCounts(3, 0, 0, 0, 0, 0, 0, 0, 0)), 0));
        Assert.Equal(["증상 표시·메모 1줄을 읽지 못했습니다."], RecordView.DamageNotices(Record(rounds), 1));
    }

    [Fact]
    public void EndReasonAndSaveStateAreSeparate()
    {
        Assert.Equal("측정 중", RecordView.EndText(RecordLifecycle.Running, null));
        Assert.StartsWith("정상 종료 기록 없음", RecordView.EndText(RecordLifecycle.NoSummary, null));
        Assert.Equal("사용자가 멈춤", RecordView.EndText(RecordLifecycle.Ended, EndReason.Stopped));
        var s = new RecordSummary("m", EndReason.Stopped, W0, 10, 8, 1, 1, 0, 7, true, null, 0);
        Assert.Equal("저장하지 못한 회차 2개 · 정상 종료 기록을 쓰지 못함", RecordView.SaveText(s));
        Assert.Null(RecordView.SaveText(s with { Pending = 0, Dropped = 0, SealFailed = false }));
    }

    [Fact]
    public void DurationsNeverClaimAnExactLength()
    {
        Assert.Equal("약 7초~9초", RecordView.Duration(7000, 9000, 8));
        Assert.Equal("7초 이상", RecordView.Duration(7000, null, 8));
        Assert.Equal("1회차", RecordView.Duration(0, null, 1));
        Assert.Equal("1회차 · 최대 약 2초", RecordView.Duration(0, 2000, 1));
    }

    [Fact]
    public void TrendBreaksAtMissingWindowsAndGapsAndSaysWhenThereIsNoBaseline()
    {
        var b = new LatencyBaseline("a", 12, 42, [(0L, 11.0), (300_000L, 12.0), (900_000L, 30.0), (1_200_000L, 31.0)]);
        Assert.Equal([2, 2], RecordView.Trend(ExtA, b, []).Segments.Select(s => s.Count));
        var gap = new GapSpan(GapReason.Suspend, 620_000, 650_000, W0, W0, null, null);
        var withGap = new LatencyBaseline("a", 12, 42, [(0L, 11.0), (300_000L, 12.0), (600_000L, 13.0), (900_000L, 14.0)]);
        Assert.Equal([3, 1], RecordView.Trend(ExtA, withGap, [gap]).Segments.Select(s => s.Count));
        Assert.True(RecordView.Trend(ExtA, new LatencyBaseline("a", null, null, []), []).NoBaseline);
    }

    [Fact]
    public void MixedEpisodesListEveryPartAndDoNotPickOneCandidate()
    {
        // 외부 둘 다 손실(외부 단절 추정) → 이어서 하나만 손실(대상 응답 실패)
        var rec = Record(Build(Ok(80), Ok(40) + Lost(5) + Ok(35), Ok(40) + Lost(3) + Ok(37)));
        var a = Analyze(rec);
        var e = a.Episodes.First();
        Assert.True(e.Parts.Count >= 2, "전제: 판정이 바뀌는 끊김 (" + string.Join(",", e.Parts.Select(p => p.Verdict)) + ")");
        var line = RecordView.Detail(rec, a, RecordLifecycle.Ended).Episodes[0];
        Assert.True(line.Mixed);
        Assert.Null(line.Candidate);
        Assert.StartsWith("여러 판정", line.Verdicts);
        Assert.Equal(e.Parts.Count, RecordView.Parts(e).Count);
    }
}
