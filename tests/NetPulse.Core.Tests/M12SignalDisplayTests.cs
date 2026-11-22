using System.Text.Json.Nodes;
using NetPulse.Core.Evidence;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Report;
using static NetPulse.Core.Tests.M8Records;

namespace NetPulse.Core.Tests;

/// <summary>
/// Wi‑Fi 신호 표시 (M12 설계 C 표시 [1-7]): 타임라인 장면의 신호 선(오른쪽 축, 끊김, 어댑터 바뀜), 리포트 그림·관측 근거 줄과 가리기,
/// 신호가 없는 옛 기록은 신호 표시가 없음.
/// </summary>
public sealed class M12SignalDisplayTests
{
    private static SignalRun Sig(int? q, long first, long last, bool continues = true, int? rssi = null) =>
        new(SignalState.Ok, q, rssi, first, last, (last - first) / 1000 + 1, continues);

    private static SignalRun State(SignalState s, long first, long last, bool continues = true) => new(s, null, null, first, last, (last - first) / 1000 + 1, continues);

    private static SignalTrace Trace(params SignalTrack[] tracks) => SignalTimeline.Trace(tracks, 1000);

    private static SignalTrack Track(string alias, long? end, params SignalRun[] runs) => new(alias, 9, "wireless", runs, end);

    // ---------------- 그래프 영역과 오른쪽 축 ----------------

    [Fact]
    public void SignalAxisCarvesTheRightMarginAndMapsQualityToTheRightAxis()
    {
        Assert.Equal(760, TimelineGeometry.PlotWidth(800, signalAxis: true, axisMargin: 40));
        Assert.Equal(800, TimelineGeometry.PlotWidth(800, signalAxis: false, axisMargin: 40));   // 신호가 없는 측정은 지금과 같다
        Assert.Equal(0, TimelineGeometry.PlotWidth(30, signalAxis: true, axisMargin: 40));
        Assert.Equal(10, TimelineGeometry.SignalY(100, top: 10, height: 200));
        Assert.Equal(210, TimelineGeometry.SignalY(0, top: 10, height: 200));
        Assert.Equal(110, TimelineGeometry.SignalY(50, top: 10, height: 200));
        Assert.Equal(10, TimelineGeometry.SignalY(150, top: 10, height: 200));   // 범위 밖은 끝에 붙인다
        Assert.Equal(380, TimelineGeometry.X(60_000, 0, 120_000, 760));
        // 리포트 그림도 같은 규칙: 신호 선이 있으면 오른쪽 축 폭을 뺀 열 수
        Assert.Equal(TimelineFigure.Columns - TimelineFigure.SignalAxisWidth, TimelineFigure.PlotColumnsFor(true));
        Assert.Equal(TimelineFigure.Columns, TimelineFigure.PlotColumnsFor(false));
        Assert.Equal(TimelineFigure.PlotTop, TimelineFigure.SignalY(100));
        Assert.Equal(TimelineFigure.PlotTop + TimelineFigure.PlotHeight, TimelineFigure.SignalY(0));
    }

    // ---------------- 장면의 신호 선 ----------------

    [Fact]
    public void LineStepsThroughRunsAndBreaksOnStatesThatAreNotOk()
    {
        var trace = Trace(Track("a1", 121_000,
            Sig(86, 0, 59_000, continues: false), Sig(62, 60_000, 89_000), State(SignalState.NotConnected, 90_000, 95_000), Sig(82, 96_000, 120_000)));
        Assert.Equal(SignalDisplay.Line, trace.Display);
        Assert.True(trace.Axis);
        Assert.Equal(["a1"], trace.RouteAdapters);
        Assert.Empty(trace.AdapterChanges);
        // 묶음 끝 = 마지막 표본 + 간격 (다음 묶음 시작·관측 종료에서 자름)
        Assert.Equal([60_000L, 90_000, 96_000, 121_000], trace.Runs.Select(r => r.ToMs));

        var f = SignalTimeline.Frame(trace, 0, 121_000, 1000);
        Assert.Equal(2, f.Segments.Count);   // 연결 안 됨에서 끊김
        Assert.Equal([(0.0, 86.0), (60_000.0, 86.0), (60_000.0, 62.0), (90_000.0, 62.0)], f.Segments[0].Select(p => (p.XMs, p.MinMs)));
        Assert.Equal([(96_000.0, 82.0), (121_000.0, 82.0)], f.Segments[1].Select(p => (p.XMs, p.MinMs)));
        Assert.Equal(62, f.MinQuality);

        // 창 안으로 자른다
        var zoom = SignalTimeline.Frame(trace, 30_000, 70_000, 400);
        Assert.Equal(30_000, zoom.Segments[0][0].XMs);
        Assert.Equal(70_000, zoom.Segments[0][^1].XMs);
    }

    [Fact]
    public void BrokenRunsAreNotJoinedAndManyRunsAreThinnedPerColumn()
    {
        // 공백 뒤 묶음(Continues 거짓)은 잇지 않는다
        var gap = Trace(Track("a1", null, Sig(80, 0, 9_000, continues: false), Sig(80, 20_000, 29_000, continues: false)));
        Assert.Equal(2, SignalTimeline.Frame(gap, 0, 30_000, 1000).Segments.Count);

        // 묶음이 열 수보다 많으면 열마다 최솟값·최댓값 (점은 열 수의 두 배를 넘지 않는다)
        var flapping = Enumerable.Range(0, 4000).Select(i => Sig(i % 2 == 0 ? 50 : 60, i * 1000L, i * 1000L, continues: i > 0)).ToArray();
        var f = SignalTimeline.Frame(Trace(Track("a1", null, flapping)), 0, 4_000_000, 100);
        var seg = Assert.Single(f.Segments);
        Assert.True(seg.Count <= 200);
        Assert.All(seg, p => Assert.Equal((50.0, 60.0), (p.MinMs, p.MaxMs)));

        // 간추릴 때도 끊긴 자리에서 끊는다
        flapping[2000] = flapping[2000] with { Continues = false };
        Assert.Equal(2, SignalTimeline.Frame(Trace(Track("a1", null, flapping)), 0, 4_000_000, 100).Segments.Count);
        // 권한 없음 표본이 든 열은 그 열의 점 하나만 따로 두고 앞뒤를 끊는다 (지연 선의 값 없는 열과 같은 규칙)
        flapping[3000] = State(SignalState.Denied, 3_000_000, 3_000_000);
        var broken = SignalTimeline.Frame(Trace(Track("a1", null, flapping)), 0, 4_000_000, 100).Segments;
        Assert.Equal([50, 25, 1, 24], broken.Select(x => x.Count));
    }

    [Fact]
    public void RouteAdapterChangeBreaksTheLineAndIsMarked()
    {
        // 경로가 a1에서 a2로 옮김: 선을 끊고 새 어댑터로 잇고, 바뀐 시각을 표시한다
        var trace = Trace(
            Track("a1", 60_000, Sig(70, 0, 59_000, continues: false)),
            Track("a2", null, Sig(40, 60_000, 119_000, continues: true)));
        Assert.Equal([60_000L], trace.AdapterChanges);
        Assert.Equal(["a1", "a2"], trace.RouteAdapters);
        Assert.Equal(0, trace.OtherAdapters);
        var f = SignalTimeline.Frame(trace, 0, 120_000, 1000);
        Assert.Equal(2, f.Segments.Count);
        Assert.Equal([60_000L], f.AdapterChanges);
        Assert.Contains("무선 어댑터 바뀜", trace.LegendText);
        Assert.EndsWith(", Wi‑Fi 신호 품질 최저 40%, 무선 어댑터 바뀜 1번", SignalTimeline.Describe(f));
        // 바뀐 시각이 창 밖이면 창에는 표시가 없다
        Assert.Empty(SignalTimeline.Frame(trace, 70_000, 120_000, 1000).AdapterChanges);
    }

    [Fact]
    public void OverlappingWirelessAdaptersAreCountedButNotDrawn()
    {
        // 기본 경로 어댑터(표본이 많은 a1)만 그린다. 시간이 겹치는 a3은 이름 없이 수만
        var trace = Trace(
            Track("a1", null, Sig(70, 0, 119_000, continues: false)),
            Track("a3", null, Sig(20, 30_000, 50_000, continues: false)));
        Assert.Equal(["a1"], trace.RouteAdapters);
        Assert.Equal(1, trace.OtherAdapters);
        Assert.All(trace.Runs, r => Assert.Equal("a1", r.Alias));
        Assert.Contains("그 밖의 무선 어댑터 1개", trace.LegendText);
        Assert.DoesNotContain("a3", trace.LegendText);
        Assert.Equal(70, SignalTimeline.Frame(trace, 0, 120_000, 500).MinQuality);
    }

    [Fact]
    public void DeniedOrUnsupportedShowStateTextInsteadOfALine()
    {
        var denied = Trace(Track("a1", null, State(SignalState.Denied, 0, 59_000, continues: false)));
        Assert.Equal(SignalDisplay.State, denied.Display);
        Assert.False(denied.Axis);   // 선이 없으면 오른쪽 축 여백도 없다
        Assert.Null(denied.LegendText);
        Assert.Contains("권한", denied.StateText);
        var f = SignalTimeline.Frame(denied, 0, 60_000, 800);
        Assert.Empty(f.Segments);
        Assert.Equal(", " + denied.StateText, SignalTimeline.Describe(f));

        var unsupported = Trace(Track("a1", null, State(SignalState.Unsupported, 0, 59_000, continues: false)));
        Assert.Contains("읽을 수 없습니다", unsupported.StateText);
        // 품질 없이 RSSI만 읽힌 측정도 선 대신 상태 글
        var rssiOnly = Trace(Track("a1", null, Sig(null, 0, 59_000, continues: false, rssi: -70)));
        Assert.Equal(SignalDisplay.State, rssiOnly.Display);
        Assert.Contains("RSSI", rssiOnly.StateText);
    }

    [Fact]
    public void MeasurementsWithoutSignalAreUnchanged()
    {
        Assert.Same(SignalTrace.None, Trace());
        Assert.Same(SignalTrace.None, SignalTrace.FromEvidence(SignalEvidence.None, 1000));
        Assert.Same(SignalTrace.None, SignalTrace.FromLive(NetPulse.Core.Measurement.SignalLive.None));
        var rec = Typical();
        var h = rec.Header!;
        var bands = TimelineModel.Bands(rec.Rounds, h.Targets, [], h.IntervalMs);
        var plain = TimelineModel.Frame(rec.Rounds, h.Targets, [], [], [], bands, 0, 600_000, 800, h.IntervalMs, "전체");
        var none = TimelineModel.Frame(rec.Rounds, h.Targets, [], [], [], bands, 0, 600_000, 800, h.IntervalMs, "전체", signal: SignalTrace.None);
        Assert.Same(SignalFrame.None, plain.Signal);
        Assert.Equal(plain.Summary, none.Summary);
        Assert.Equal(SignalDisplay.None, none.Signal.Display);
        // 기록 화면·리포트가 옛 기록(신호 이벤트 없음)에서 만드는 표시도 없음
        Assert.Same(SignalTrace.None, SignalTrace.FromEvidence(SignalEvidenceBuilder.FromEvents(rec.Events, h.IntervalMs), h.IntervalMs));
    }

    [Fact]
    public void LiveSnapshotDrawsUpToTheLastSample()
    {
        var live = new NetPulse.Core.Measurement.SignalLive(true, 1000,
        [
            new NetPulse.Core.Measurement.SignalAdapterLive("a1", 9, "wireless", [Sig(80, 0, 9_000, continues: false)], Sig(30, 10_000, 14_000), "realtime", null, null),
        ], false);
        var trace = SignalTrace.FromLive(live);
        Assert.Equal([10_000L, 15_000], trace.Runs.Select(r => r.ToMs));
        var f = SignalTimeline.Frame(trace, 0, 14_000, 1000);
        Assert.Equal(14_000, Assert.Single(f.Segments)[^1].XMs);   // 지금(창 끝)에서 자른다
    }

    // ---------------- 리포트 ----------------

    /// <summary>a1(인터페이스 9, 무선)의 신호 이벤트: 변경 → … → 마지막 체크포인트 → 요약 (세션이 쓰는 형식).</summary>
    private static IEnumerable<JsonObject> SignalEvents(long endMs, params SignalRun[] runs)
    {
        JsonObject Adapter(string collection) => new() { ["adapter"] = "a1", ["interfaceIndex"] = 9, ["kind"] = "wireless", ["collection"] = collection };
        void Value(JsonObject o, SignalRun r, string prefix = "")
        {
            o[prefix == "" ? "state" : "prevState"] = SignalText.Of(r.State);
            if (r.Quality is int q) o[prefix == "" ? "quality" : "prevQuality"] = q;
            if (r.RssiDbm is int s) o[prefix == "" ? "rssi" : "prevRssi"] = s;
        }
        for (int i = 0; i < runs.Length; i++)
        {
            var o = Adapter("change");
            Value(o, runs[i]);
            o["sampleMs"] = runs[i].FirstMs;
            if (i > 0)
            {
                Value(o, runs[i - 1], "prev");
                o["prevFirstMs"] = runs[i - 1].FirstMs;
                o["prevLastMs"] = runs[i - 1].LastMs;
                o["prevSamples"] = runs[i - 1].Samples;
                if (!runs[i].Continues) o["break"] = "gap";
            }
            yield return Ev(Id, SignalEvidenceBuilder.EventType, runs[i].FirstMs / 1000, runs[i].FirstMs, W0.AddMilliseconds(runs[i].FirstMs), o);
        }
        var last = runs[^1];
        var end = Adapter("checkpoint");
        end["reason"] = "end";
        Value(end, last);
        end["firstMs"] = last.FirstMs;
        end["lastMs"] = last.LastMs;
        end["runSamples"] = last.Samples;
        end["endMs"] = endMs;
        yield return Ev(Id, SignalEvidenceBuilder.EventType, null, endMs, W0.AddMilliseconds(endMs), end);
        var summary = Adapter("summary");
        SignalEvidenceBuilder.WriteStats(summary, SignalStatistics.Compute(runs, 1000, endMs));
        yield return Ev(Id, SignalEvidenceBuilder.EventType, null, endMs, W0.AddMilliseconds(endMs), summary);
    }

    /// <summary>10분 기록, 외부 둘이 100~104초에 함께 응답 없음. 신호: 약해졌다가(14%) 연결 안 됨, 다시 82%.</summary>
    private static MeasurementRecord WifiRecord()
    {
        string ext = Ok(100) + Lost(5) + Ok(495);
        var link = Ev(Id, "link", 0, 0, W0, new JsonObject
        {
            ["source"] = "direct", ["collection"] = "up", ["adapter"] = "a1", ["interfaceIndex"] = 9, ["kind"] = "wireless", ["virtual"] = false, ["eventMs"] = 0,
        });
        var signal = SignalEvents(600_000,
            Sig(86, 0, 89_000, continues: false, rssi: -51), Sig(14, 90_000, 99_000, rssi: -85), State(SignalState.NotConnected, 100_000, 104_000),
            Sig(82, 105_000, 599_000, rssi: -54));
        return Record(Rounds(Ok(600), ext, ext), Routes("wireless").Append(link).Concat(signal));
    }

    private static TimelineFigure Figure(ReportDocument d) => ((ReportFigure)d.Sections.Single(s => s.Id == "timeline").Blocks[0]).Figure;

    private static ReportTable SignalTable(ReportDocument d) =>
        d.Sections.Single(s => s.Id == "evidence").Blocks.OfType<ReportTable>().Single(t => t.Caption == "무선 어댑터별 신호 (관측)");

    [Fact]
    public void ReportFigureCarvesTheSignalAxisAndDrawsTheQualityLine()
    {
        var d = Build(WifiRecord());
        var f = Figure(d);
        Assert.Equal(TimelineFigure.Columns - TimelineFigure.SignalAxisWidth, f.PlotColumns);
        Assert.True(f.SignalLine);
        Assert.Equal(2, f.Signal.Count);   // 연결 안 됨에서 끊김
        Assert.Equal(14, f.Signal.SelectMany(s => s).Min(p => p.ValueMs));
        Assert.All(f.Signal.SelectMany(s => s), p => Assert.InRange(p.Column, 0, f.PlotColumns - 1));
        Assert.All(f.Bands, b => Assert.True(b.ToColumn <= f.PlotColumns));   // 띠도 남은 영역 안
        Assert.Equal(TimelineFigure.SignalLegendLine, f.Legend[^1]);
        Assert.Contains("관측한 최저 14%", f.Description);

        var html = ReportHtml.Write(d);
        Assert.Contains("class=\"line-signal\"", html);
        Assert.Contains(">100%</text>", html);
        Assert.Contains(ReportPalette.Document.Signal, html);
        Assert.Equal(d.Texts().Select(r => ReportHtml.Normalize(r.Text)).Where(t => t.Length > 0), ReportHtml.BodyTexts(html));   // 글 순서 = 문서 순서
    }

    [Fact]
    public void ReportEvidenceHasAWirelessRowWithAveragesMinimumsAndStatesMaskedLikeLinks()
    {
        var masked = Build(WifiRecord());
        var row = Assert.Single(SignalTable(masked).Rows);
        string Cell(ReportRow r, string field) => r.Cells.Single(c => c.Field == field).Value.Text;
        Assert.Equal("무선 어댑터 1", Cell(row, "signal.adapter"));   // 링크 근거와 같은 가림 (종류 + 순번)
        Assert.Equal(TextOrigin.Generated, row.Cells[0].Value.Origin);
        Assert.Equal("14%", Cell(row, "signal.qualityMin"));
        Assert.Equal("-85 dBm", Cell(row, "signal.rssiMin"));
        Assert.StartsWith("읽음 99.2%", Cell(row, "signal.state"));
        Assert.Contains("연결 안 됨 0.8%", Cell(row, "signal.state"));
        Assert.Empty(masked.Guard!.Check(masked));

        var unmasked = Build(WifiRecord(), new ReportOptions(Masked: false));
        var raw = Assert.Single(SignalTable(unmasked).Rows);
        Assert.Equal("a1 (인터페이스 9)", raw.Cells[0].Value.Text);
        Assert.Equal(TextOrigin.Record, raw.Cells[0].Value.Origin);

        // 끊김 동안의 신호는 관측만 적고, 판정·가능성이 높은 곳은 신호 없는 같은 기록과 같다
        var texts = masked.Sections.Single(s => s.Id == "evidence").Blocks.OfType<ReportParagraph>().SelectMany(p => p.Runs).Select(r => r.Text).ToArray();
        Assert.Contains(texts, t => t.StartsWith("끊김 1번 동안 관측된 Wi‑Fi 신호: ", StringComparison.Ordinal) && t.Contains("'연결 안 됨' 상태"));
        Assert.Contains(ReportTexts.SignalScope, texts);
        foreach (var word in ReportTexts.Forbidden)
            Assert.DoesNotContain(masked.Texts(), r => r.Origin == TextOrigin.Generated && r.Text.Contains(word, StringComparison.Ordinal));
        var withoutSignal = WifiRecord() with { Events = WifiRecord().Events.Where(e => (string?)e["type"] != SignalEvidenceBuilder.EventType).ToArray() };
        static string[] Verdicts(MeasurementRecord r) =>
            Analyze(r).Episodes.SelectMany(e => e.Parts).Select(p => $"{p.Verdict}|{ReportTexts.Candidate(p.Candidate)}").ToArray();
        Assert.NotEmpty(Verdicts(withoutSignal));
        Assert.Equal(Verdicts(withoutSignal), Verdicts(WifiRecord()));
    }

    [Fact]
    public void OldRecordsWithoutSignalHaveNoSignalPartsInTheReport()
    {
        var d = Build(Typical());
        var f = Figure(d);
        Assert.Equal(TimelineFigure.Columns, f.PlotColumns);
        Assert.False(f.SignalLine);
        Assert.Null(f.SignalLegend);
        Assert.Equal(TimelineFigure.LegendLabels, f.Legend);
        Assert.Empty(f.Signal);
        Assert.DoesNotContain(d.Sections.Single(s => s.Id == "evidence").Blocks.OfType<ReportHeading>(), h => h.Text.Text == "Wi‑Fi 신호");
        Assert.DoesNotContain("line-signal", ReportHtml.Write(d));
    }
}
