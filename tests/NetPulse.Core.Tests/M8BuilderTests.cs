using NetPulse.Core.Analysis;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Report;
using static NetPulse.Core.Tests.M8Records;

namespace NetPulse.Core.Tests;

/// <summary>리포트 내용 (M8 설계 3.1·3.2, 7.1 ReportBuilder·ReportTexts).</summary>
public sealed class M8BuilderTests : IDisposable
{
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private static string Field(ReportDocument d, string name) => d.Fields().First(f => f.Field == name).Value;
    private static string[] All(ReportDocument d, string name) => d.Fields().Where(f => f.Field == name).Select(f => f.Value).ToArray();
    private static string[] SectionIds(ReportDocument d) => d.Sections.Select(s => s.Id).ToArray();

    private static void NoForbiddenWords(ReportDocument d)
    {
        foreach (var r in d.Texts().Where(r => r.Origin == TextOrigin.Generated))
            foreach (var w in ReportTexts.Forbidden)
                Assert.False(r.Text.Contains(w, StringComparison.Ordinal), $"금지어 \"{w}\": {r.Text}");
    }

    [Fact]
    public void ATypicalRecordHasTheEightSectionsInOrderAndTheSummaryValuesFromTheAnalysis()
    {
        var rec = Typical();
        var a = Analyze(rec);
        var d = Build(rec, new ReportOptions(IncludeMemos: true), diagnoses: [Diagnosis(W0.AddMinutes(3))]);
        Assert.Equal(["summary", "timeline", "episodes", "evidence", "details", "marks", "diagnoses", "conditions"], SectionIds(d));
        Assert.Equal(["1. 요약", "2. 타임라인", "3. 끊김 목록", "4. 관측 근거", "5. 끊김 상세", "6. 증상 표시와 메모", "7. 측정 중 진단", "8. 측정 조건"],
            d.Sections.Select(s => s.Title));
        Assert.Equal(Sentences.GradeName(a.Grade.Grade), Field(d, "summary.grade"));
        Assert.Equal(a.Grade.Sentence, Field(d, "summary.sentence"));
        Assert.Equal("2026-11-13 13:20:00 (UTC+09:00)", Field(d, "summary.start"));
        Assert.Equal("2026-11-13 13:30:00 (UTC+09:00)", Field(d, "summary.end"));
        Assert.Equal("계획한 시간을 마침", Field(d, "summary.endReason"));
        Assert.Equal($"{a.Episodes.Count}번", Field(d, "summary.episodes"));
        Assert.Equal(2, a.Episodes.Count);
        Assert.Equal(["1", "2"], All(d, "episode.index"));
        Assert.Equal(["13:21:40", "13:25:00"], All(d, "episode.start"));
        // 혼합 끊김은 하위 구간 줄이 따라온다 (판정 이름은 기획서 6.2 그대로)
        var mixed = a.Episodes[1];
        Assert.True(mixed.Mixed);
        Assert.Equal(mixed.Parts.Select(p => RecordView.VerdictName(p.Verdict)), All(d, "part.verdict"));
        Assert.Equal(mixed.Parts.Select(p => RecordView.CandidateText(p.Candidate)), All(d, "part.candidate"));
        Assert.Equal("NetPulse-리포트-2026-11-13-1320.html", d.FileName);
        NoForbiddenWords(d);
    }

    [Theory]
    [InlineData("outage-long")]
    [InlineData("link-drop")]
    [InlineData("link-drop-long")]
    [InlineData("gap")]
    [InlineData("vpn")]
    public async Task DemoScenariosBuildEverySectionWithoutForbiddenWords(string scenario)
    {
        var rec = await M8Records.Demo(dir, scenario);
        var a = MeasurementAnalysis.Analyze(rec);
        var d = ReportBuilder.Build(new ReportInput("k", "s", rec, a, RecordLifecycle.Ended, [], new ReportOptions(IncludeMemos: true), rec.Header!.StartedAt, "test"));
        Assert.Equal(a.Grade.Sentence, Field(d, "summary.sentence"));
        Assert.Equal(a.Episodes.Count, All(d, "episode.index").Length);
        Assert.Equal(rec.Header.Targets.Count(t => t.Enabled), All(d, "target.name").Length);
        NoForbiddenWords(d);
        var html = ReportHtml.Write(d);
        Assert.Empty(ReportHtml.Verify(html));
    }

    [Fact]
    public void OpenEndsSayMeasurementEndForEndedRecordsAndUnknownAfterTheLastObservationOtherwise()
    {
        var rounds = Rounds(Ok(120), Ok(100) + Lost(20), Ok(100) + Lost(20));
        var ended = Build(Record(rounds));
        Assert.StartsWith("측정 끝까지 이어짐 (", Field(ended, "episode.duration"));
        var noSummary = Build(Record(rounds, summary: false), life: RecordLifecycle.NoSummary);
        Assert.StartsWith("마지막 관측까지 이어짐, 그 뒤는 알 수 없음 (", Field(noSummary, "episode.duration"));
        Assert.StartsWith("정상 종료 기록 없음 · 마지막 관측 ", Field(noSummary, "summary.end"));
        foreach (var d in new[] { ended, noSummary })
            Assert.DoesNotContain(d.Texts(), r => r.Text.Contains("이어지는 중", StringComparison.Ordinal));
    }

    [Fact]
    public void MoreThanThreeHundredEpisodesListTheLongestThreeHundredInTimeOrderAndCountTheRestInTheStats()
    {
        // 외부 둘이 함께 3회차마다 1회차 손실 (끊김 약 330개) + 긴 끊김 셋
        string ext = Every(990, 3) + Lost(4) + Ok(3) + Lost(6) + Ok(3) + Lost(5) + Ok(20);
        var rec = Record(Rounds(Ok(ext.Length), ext, ext));
        var a = Analyze(rec);
        Assert.True(a.Episodes.Count > ReportBuilder.ListLimit);
        var d = Build(rec);
        var starts = All(d, "episode.start");
        Assert.Equal(ReportBuilder.ListLimit, starts.Length);
        Assert.Equal(starts.Order(StringComparer.Ordinal), starts);   // 시간순
        // 선정: 최소 길이 내림차순, 같으면 먼저 시작한 것 → 긴 끊김 셋은 들어 있다
        var expected = a.Episodes.Select((e, i) => (e, i)).OrderByDescending(x => x.e.MinDurationMs).ThenBy(x => x.e.FirstRound).Take(ReportBuilder.ListLimit)
            .OrderBy(x => x.e.FirstRound).Select(x => (x.i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected, All(d, "episode.index"));
        Assert.Contains(d.Texts(), r => r.Text == ReportTexts.ListCapTitle(ReportBuilder.ListLimit, a.Episodes.Count));
        Assert.Contains(d.Texts(), r => r.Text == ReportTexts.ListCapNote(a.Episodes.Count, ReportBuilder.ListLimit));
        // 판정별 통계는 전부를 센다
        Assert.Equal(a.Stats.Where(s => s.Verdict != Verdict.Normal && s.Count > 0).Select(s => $"{s.Count}번"), All(d, "verdict.count"));
    }

    [Fact]
    public void ARunningMeasurementAndUnreadableRecordsAreRefused()
    {
        var rec = Typical();
        Assert.Equal(ReportError.Running, Assert.Throws<ReportException>(() => Build(rec, life: RecordLifecycle.Running)).Error);
        var unsupported = rec with { Status = RecordStatus.Unsupported };
        Assert.Equal(ReportError.Unsupported, Assert.Throws<ReportException>(() => Build(unsupported, a: Analyze(rec))).Error);
        var unreadable = rec with { Status = RecordStatus.Unreadable };
        Assert.Equal(ReportError.Unreadable, Assert.Throws<ReportException>(() => Build(unreadable, a: Analyze(rec))).Error);
    }

    [Fact]
    public void LinkEvidenceKeepsTheFourStatesAndSecondaryReasonsAndNeverCopiesRawReasonText()
    {
        var rec = Typical();
        var a = Analyze(rec) with { Links = FourStates() };
        var d = Build(rec, a: a);
        Assert.Equal(["끊김 관측", "접근 거부", "이 어댑터는 기록을 지원하지 않음", "관측했지만 끊김 기록 없음"], All(d, "link.state"));
        Assert.Equal(["직접 관측", "시스템 기록", "직접 관측", "시스템 기록"], All(d, "link.source"));
        var notes = All(d, "link.notes");
        Assert.Contains("관측 범위 97%", notes[0]);
        Assert.Contains("일부만 관측", notes[2]);
        Assert.Contains("알림이 넘쳐 일부 빠짐", notes[2]);
        Assert.Contains("기타 사유", notes[3]);
        var html = ReportHtml.Write(d);
        Assert.DoesNotContain("np-test-logs", html);
        Assert.DoesNotContain("driver.etl", html);
        Assert.DoesNotContain("probe.log", html);   // 회차의 검사 오류 사유도 넣지 않는다

        var none = Build(rec, a: Analyze(rec) with { Links = Evidence.LinkEvidence.None });
        Assert.Contains(none.Texts(), r => r.Text == ReportTexts.NoLinkEvidence);
        Assert.Empty(All(none, "link.state"));
    }

    [Fact]
    public void EmptyEpisodeListsTellNotObservedFromCouldNotObserve()
    {
        var quiet = Record(Rounds(Ok(600), Ok(600), Ok(600)));
        var d = Build(quiet);
        Assert.Equal("관측되지 않음", Field(d, "summary.episodes"));
        Assert.Contains(d.Texts(), r => r.Text == "이 측정에서는 끊김이 관측되지 않았습니다.");
        Assert.DoesNotContain("details", SectionIds(d));

        var shortOne = Record(Rounds(Ok(30), Ok(30), Ok(30)));
        var s = Build(shortOne);
        Assert.Equal(Grade.Insufficient, Analyze(shortOne).Grade.Grade);
        Assert.Equal("관측되지 않음 (관측 부족)", Field(s, "summary.episodes"));
        Assert.Equal("관측 부족", Field(s, "summary.grade"));

        var blind = Record(Rounds(new string('_', 120), new string('_', 120), new string('_', 120)));
        var b = Build(blind);
        Assert.Equal("관측하지 못함", Field(b, "summary.episodes"));
        Assert.Contains(b.Texts(), r => r.Text.StartsWith("끊김을 관측하지 못했습니다", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUndecidableGradeIsReportedAsTheAnalysisSaysIt()
    {
        var rec = Typical();
        var a = Analyze(rec);
        var undecidable = a with { Grade = new GradeResult(Grade.Undecidable, "undecidable", Sentences.For(Grade.Undecidable, "undecidable", a.Stats, a.Coverage), []) };
        var d = Build(rec, a: undecidable);
        Assert.Equal("판단 불가", Field(d, "summary.grade"));
        Assert.Equal(undecidable.Grade.Sentence, Field(d, "summary.sentence"));
    }

    [Fact]
    public void DamagedRecordsSayWhatWasLostInTheConditions()
    {
        var rec = Record(Rounds(Ok(600), Ok(600), Ok(600)), damage: new DamageCounts(3, 0, 0, 0, 0, 0, 0, 0, 2));
        var d = Build(rec);
        Assert.Equal("기록 문제 2건", Field(d, "condition.damage"));
        var warnings = d.Sections.Single(s => s.Id == "conditions").Blocks.OfType<ReportParagraph>().Where(p => p.Kind == ParagraphKind.Warning).ToArray();
        Assert.Equal(RecordView.DamageNotices(rec, 0), warnings.Select(w => w.Runs[0].Text));
    }

    [Fact]
    public void OptionsLeaveOutMemosDiagnosesAndDetails()
    {
        var rec = Typical();
        var d = Build(rec, new ReportOptions(IncludeMemos: false, IncludeDiagnoses: false, EpisodeDetails: 0), diagnoses: [Diagnosis(W0.AddMinutes(3))]);
        Assert.Equal(["summary", "timeline", "episodes", "evidence", "marks", "conditions"], SectionIds(d));
        Assert.Empty(All(d, "mark.memo"));
        Assert.Single(All(d, "mark.time"));
        Assert.DoesNotContain(d.Texts(), r => r.Origin == TextOrigin.Memo);
        Assert.Equal(["1. 요약", "2. 타임라인", "3. 끊김 목록", "4. 관측 근거", "5. 증상 표시와 메모", "6. 측정 조건"], d.Sections.Select(s => s.Title));

        var withAll = Build(rec, new ReportOptions(Masked: false, IncludeMemos: true, EpisodeDetails: 1), diagnoses: [Diagnosis(W0.AddMinutes(3)), Diagnosis(W0.AddMinutes(4), "other")]);
        Assert.Equal(["회의 중 끊김"], All(withAll, "mark.memo"));   // 메모는 가리기를 끈 문서에서만
        Assert.Single(All(withAll, "diagnosis.head"));   // 그 측정 ID의 진단만
        Assert.Equal(7, All(withAll, "diagnosis.check.judgment").Length);
        Assert.Single(withAll.Sections.Single(s => s.Id == "details").Blocks.OfType<ReportHeading>());
    }

    [Fact]
    public void ADiagnosisSectionWithoutDiagnosesSaysSo()
    {
        var d = Build(Typical());
        Assert.Contains(d.Sections.Single(s => s.Id == "diagnoses").Blocks.OfType<ReportParagraph>(), p => p.Runs[0].Text == "이 측정 중에 실행한 빠른 진단이 없습니다.");
    }

    [Fact]
    public void CodesBecomeFixedPhrasesWithoutTheirRawText()
    {
        Assert.Equal("기타 사유", ReportTexts.Code(@"\\server\share\x"));
        Assert.Equal("링크 기록을 읽을 권한이 없습니다", ReportTexts.Code("log-denied"));
        Assert.Equal("어댑터 상태를 읽지 못했습니다", ReportTexts.Code("GetIfEntry2_1168"));
        Assert.Equal("사유 없음", ReportTexts.Code(null));
    }

    [Fact]
    public void FixedPhrasesHaveNoForbiddenWordsAndCandidatesStayCandidates()
    {
        string[] fixedTexts = [ReportTexts.Intro, ReportTexts.Scope, ReportTexts.MaskedNote, ReportTexts.UnmaskedNote, ReportTexts.MaskedNoMemos, ReportTexts.NoLinkEvidence, ReportTexts.PrintHint];
        foreach (var t in fixedTexts)
            foreach (var w in ReportTexts.Forbidden) Assert.DoesNotContain(w, t);
        foreach (var k in Enum.GetValues<CandidateKind>())
        {
            var text = ReportTexts.Candidate(new Candidate(k, null, "wired"));
            Assert.StartsWith("가능성이 높은 곳", text);
            foreach (var w in ReportTexts.Forbidden) Assert.DoesNotContain(w, text);
        }
        Assert.Equal(["이 PC 연결 끊김 관측", "외부 연결 끊김 추정", "판단 불가 (근거 부족)", "대상 응답 실패"],
            new[] { Verdict.LinkDown, Verdict.ExternalOutage, Verdict.Insufficient, Verdict.TargetFailure }.Select(RecordView.VerdictName));
    }
}
