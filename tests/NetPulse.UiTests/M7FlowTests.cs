using System.IO;
using System.Text.Json;
using NetPulse.Core.Analysis;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;

namespace NetPulse.UiTests;

/// <summary>
/// G3 기록 화면 흐름 (M7 설계 7.2). 화면 값은 앱이 저장한 기록 파일을 시험이 Core로 다시 읽고 분석한 값과 비교한다.
/// 조작은 UI Automation 패턴만 (키보드만으로 하는 흐름은 <see cref="KeyboardTests"/>).
/// </summary>
public sealed class M7FlowTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static JsonElement Sd(JsonElement d) => d.GetProperty("screenDiag");
    private static JsonElement Detail(JsonElement d) => Sd(d).GetProperty("detail");

    private static AppDriver Open(string userData, string prep, string args)
    {
        if (prep.Length > 0) M5Fixtures.Prepare(userData, prep);
        return AppDriver.LaunchWith(userData, [.. M5Fixtures.Args(args.Replace("{userData}", userData, StringComparison.Ordinal)), "--freeze-motion"]);
    }

    private static void WaitAnalyzed(AppDriver app) =>
        app.WaitDiag(d => Sd(d).ValueKind == JsonValueKind.Object && Sd(d).TryGetProperty("listed", out var l) && l.GetBoolean() && Sd(d).GetProperty("analyzing").GetInt32() == 0, b => b, "기록 목록 분석");

    private static JsonElement WaitDetail(AppDriver app) =>
        app.WaitDiag(d => d, d => Sd(d).ValueKind == JsonValueKind.Object && Sd(d).TryGetProperty("detail", out var x) && x.ValueKind == JsonValueKind.Object
            && Sd(d).TryGetProperty("detailComplete", out var c) && c.GetBoolean(), "기록 상세");

    private static (MeasurementRecord Record, AnalysisResult Analysis) Reanalyze(string userData, string id)
    {
        var record = RecordReader.Read(RecordReader.FindSegments(id, M5Fixtures.RecordRoots(userData)));
        return (record, MeasurementAnalysis.Analyze(record));
    }

    /// <summary>상세 값 = 시험이 다시 분석한 값.</summary>
    private static object AssertDetailMatches(JsonElement d, MeasurementRecord record, AnalysisResult a)
    {
        var x = Detail(d);
        Assert.Equal(a.Grade.Grade.ToString(), x.GetProperty("grade").GetString());
        Assert.Equal(a.Grade.Sentence, x.GetProperty("sentence").GetString());
        Assert.Equal(a.Episodes.Count, x.GetProperty("episodes").GetInt32());
        Assert.Equal(RecordView.EndText(RecordLifecycle.Ended, record.Summary?.EndReason ?? record.EndReason), x.GetProperty("end").GetString());
        return new { grade = a.Grade.Grade.ToString(), episodes = a.Episodes.Count };
    }

    // ---------------- 1. 기록에서 다시 열기 ----------------

    [Fact]
    public void AFinishedMeasurementReopensWithTheSameValuesBeforeAndAfterRestart()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            string id;
            object first;
            using (var app = Open(userData, "profile-outage", "--demo outage-long --demo-speed 600 --start"))
            {
                var finished = app.WaitDiag(d => d.GetProperty("finished"), f => f.ValueKind == JsonValueKind.Object, "측정 끝");
                id = M5Fixtures.Str(finished, "Id");
                app.Invoke("measure.result.records");
                app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "records", "기록 화면");
                var d = WaitDetail(app);
                Assert.Equal(id, Detail(d).GetProperty("Id").GetString());
                var (record, analysis) = Reanalyze(userData, id);
                first = AssertDetailMatches(d, record, analysis);
                Assert.Equal(0, app.Close());
            }
            using (var app = Open(userData, "", "--demo outage-long --screen records"))
            {
                WaitAnalyzed(app);
                var row = Sd(app.Diag()).GetProperty("rows").EnumerateArray().First(r => r.GetProperty("Id").GetString() == id).GetProperty("n").GetInt32();
                app.Invoke($"records.row.{row}");
                var d = WaitDetail(app);
                var (record, analysis) = Reanalyze(userData, id);
                var again = AssertDetailMatches(d, record, analysis);
                Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(again));
                Assert.Equal(0, app.Close());
            }
            Record("flow-records", new { reopened = true, values = first });
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 2. 목록 ----------------

    [Fact]
    public void ListRowsMatchTheCatalogAndShowProblemsAndMore()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using (var app = Open(userData, "rec-mix", "--demo steady --screen records"))
            {
                WaitAnalyzed(app);
                var rows = Sd(app.Diag()).GetProperty("rows").EnumerateArray().ToArray();
                var catalog = RecordCatalog.List(M5Fixtures.RecordRoots(userData), int.MaxValue, null);
                Assert.Equal(catalog.Select(e => e.Id), rows.Select(r => r.GetProperty("Id").GetString()));
                foreach (var (entry, row) in catalog.Zip(rows))
                {
                    Assert.Equal(entry.Lifecycle.ToString(), row.GetProperty("life").GetString());
                    Assert.Equal(entry.Integrity.ToString(), row.GetProperty("integrity").GetString());
                    if (entry.Integrity == RecordIntegrity.Unreadable) continue;
                    var a = MeasurementAnalysis.Analyze(RecordReader.Read(entry.Segments));
                    Assert.Equal(a.Grade.Grade.ToString(), row.GetProperty("grade").GetString());
                    Assert.Equal(a.Episodes.Count, row.GetProperty("outages").GetInt32());
                }
                Assert.True(Sd(app.Diag()).GetProperty("unreadable").GetInt32() >= 1);
                Assert.Contains(rows, r => r.GetProperty("damage").GetInt32() > 0);   // 회차 손상 기록
                Assert.Equal(0, app.Close());
            }
        }
        finally { AppDriver.DeleteUserData(userData); }

        var many = AppDriver.NewUserData();
        try
        {
            using var app = Open(many, "rec-many", "--demo steady --screen records");
            WaitAnalyzed(app);
            Assert.Equal((35, 30), (Sd(app.Diag()).GetProperty("count").GetInt32(), Sd(app.Diag()).GetProperty("shown").GetInt32()));
            app.Invoke("records.more");
            app.WaitDiag(d => Sd(d).GetProperty("shown").GetInt32(), n => n == 35, "더 보기");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(many); }
    }

    // ---------------- 3·4. 구간 상세·복사, 타임라인 이동 ----------------

    [Fact]
    public void EpisodeDetailCopiesExactlyAndMovesTheTimeline()
    {
        var saved = ClipboardText.Save();
        if (saved.Blocked) Assert.Skip("미완료: 클립보드에 그대로 되돌릴 수 없는 형식이 있어 덮어쓰지 않았습니다.");
        var userData = AppDriver.NewUserData();
        uint? owned = null;   // 앱이 쓴 뒤 자리 잡은 클립보드 번호. null이면 시험의 내용이 아니다 → 되돌리지 않는다 (M6 G4 4라운드 이월 1)
        try
        {
            using var app = Open(userData, "rec-mixed", "--demo steady --screen records");
            WaitAnalyzed(app);
            app.Invoke("records.row.0");
            var d = WaitDetail(app);
            string id = Detail(d).GetProperty("Id").GetString()!;
            app.Invoke("records.episode.0");
            AppDriver.WaitFor(() => app.Exists("records.episode.0.detail"), e => e, "구간 상세");
            var (record, analysis) = Reanalyze(userData, id);
            var episode = analysis.Episodes[0];
            Assert.True(episode.Parts.Count >= 2, "준비한 끊김은 판정이 바뀌어야 한다");
            // 타임라인이 그 구간을 보인다
            long start = record.Rounds.First(r => r.Round >= episode.FirstRound).AtMs;
            var window = app.WaitDiag(x => Detail(x).GetProperty("window").GetString()!, w => w.Length > 0, "보기 창");
            var parts = window.Trim('(', ')').Split(',').Select(p => long.Parse(p.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.True(parts[0] <= start && start <= parts[1], $"보기 창 {window}에 구간 시작 {start}가 없다");
            Assert.Equal(0, Detail(app.Diag()).GetProperty("expanded").GetInt32());

            var expected = RecordView.Copy(record.Header!, EpisodeDetail.For(record, analysis, episode));
            // 보관한 뒤 다른 곳에서 클립보드를 바꿨으면 덮어쓰지 않고 미완료
            if (ClipboardText.Sequence() != saved.Sequence) Assert.Skip("미완료: 시험 중 클립보드가 바뀌어 덮어쓰지 않았습니다.");
            app.Invoke("records.episode.0.copy");
            app.WaitDiag(x => M5Fixtures.Str(Sd(x), "copyState"), s => s.Length > 0, "복사");
            owned = ClipboardText.Claim(expected);
            Assert.True(owned is not null, "앱이 쓴 클립보드로 확인하지 못함 (형식: " + ClipboardText.LastFormats + ")");
            Assert.Equal(expected, ClipboardText.Read());
            Assert.Equal(0, app.Close());
        }
        finally
        {
            ClipboardText.Restore(saved, owned);
            AppDriver.DeleteUserData(userData);
        }
    }

    // ---------------- 5. 메모 고치기 ----------------

    [Fact]
    public void EditedMemoSurvivesReopenAndRestartAndEmptyClears()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using (var app = Open(userData, "rec-memo", "--demo steady --screen records"))
            {
                WaitAnalyzed(app);
                app.Invoke("records.row.0");
                WaitDetail(app);
                app.Invoke("records.mark.0.edit");
                app.SetText("records.mark.0.text", "끝난 뒤 고친 메모");
                app.Invoke("records.mark.0.save");
                app.WaitDiag(d => Detail(d).GetProperty("memos").EnumerateArray().Select(m => m.GetString()).ToArray(), m => m.Contains("끝난 뒤 고친 메모"), "고친 메모");
                Assert.Equal(0, app.Close());
            }
            using (var app = Open(userData, "", "--demo steady --screen records"))
            {
                WaitAnalyzed(app);
                app.Invoke("records.row.0");
                var d = WaitDetail(app);
                Assert.Contains("끝난 뒤 고친 메모", Detail(d).GetProperty("memos").EnumerateArray().Select(m => m.GetString()));
                app.Invoke("records.mark.0.edit");
                app.SetText("records.mark.0.text", "");
                app.Invoke("records.mark.0.save");
                app.WaitDiag(x => Detail(x).GetProperty("memos").EnumerateArray().Select(m => m.ValueKind == JsonValueKind.Null).First(), b => b, "빈 글 = 지움");
                Assert.Equal(0, app.Close());
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void FailedMemoSaveKeepsTheTextAndSaysSo()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "rec-memo-blocked", "--demo steady --screen records");
            WaitAnalyzed(app);
            app.Invoke("records.row.0");
            WaitDetail(app);
            app.Invoke("records.mark.0.edit");
            app.SetText("records.mark.0.text", "못 쓴 메모");
            app.Invoke("records.mark.0.save");
            app.WaitDiag(d => Sd(d).GetProperty("memoState").EnumerateArray().Select(v => v.GetString()).ToArray(), s => s.Contains("저장하지 못했습니다"), "저장 실패");
            // 상태 기록과 오류 줄 그리기는 같은 프레임이 아닐 수 있다 (M7 UI 시험 1차: 한 번 먼저 읽어 실패)
            AppDriver.WaitFor(() => app.Exists("records.mark.0.text.error"), e => e, "메모 오류 줄");
            // 초점은 사용자가 누른 저장 단추에 그대로 (다시 그리면 사이드바로 가서 화면이 맨 위로 올라가 오류가 안 보였다, M7 UI 시험 2차.
            // 실패는 그 칸에만 반영하고 초점을 옮기지 않는다, G4 M7 6라운드 1)
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "records.mark.0.save", "실패 뒤 초점이 저장 단추에 남음");
            Assert.Equal("못 쓴 메모", ((System.Windows.Automation.ValuePattern)app.Find("records.mark.0.text").GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value);
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void MemoDraftSurvivesExpandingAnEpisodeAndSavingShowsItsState()
    {
        // 같은 상세 안에서 끊김을 펼쳐도 입력한 글이 남고(설계 3.5), 저장을 누르면 "저장하는 중…"이 화면에 보인다 (G4 M7 1라운드 b1·b9)
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "rec-mixed-memo-blocked", "--demo steady --screen records");
            WaitAnalyzed(app);
            app.Invoke("records.row.0");
            WaitDetail(app);
            app.Invoke("records.mark.0.edit");
            AppDriver.WaitFor(() => app.Exists("records.mark.0.text"), e => e, "메모 칸");
            app.SetText("records.mark.0.text", "펼쳐도 남는 초안");
            app.Invoke("records.episode.0");
            AppDriver.WaitFor(() => app.Exists("records.episode.0.detail"), e => e, "구간 상세");
            WaitDetail(app);
            string Value() => ((System.Windows.Automation.ValuePattern)app.Find("records.mark.0.text").GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value;
            AppDriver.WaitFor(() => app.Exists("records.mark.0.text"), e => e, "다시 그린 메모 칸");
            Assert.Equal("펼쳐도 남는 초안", Value());
            app.Invoke("records.mark.0.save");
            // 화면의 상태 글 (진단 값이 아니라 보이는 요소). 막힌 쓰기는 1초·3초 재시도 동안 저장 중
            AppDriver.WaitFor(() => app.Exists("records.mark.0.state") ? app.Text("records.mark.0.state") : "", s => s == "저장하는 중…", "저장하는 중 표시");
            AppDriver.WaitFor(() => app.Exists("records.mark.0.text.error"), e => e, "저장 실패 줄", TimeSpan.FromSeconds(20));
            Assert.Equal("펼쳐도 남는 초안", Value());
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void AnEarlierMemoFailingWhileEditingAnotherLeavesFocusAndTextThere()
    {
        // 메모 A가 재시도하는 동안 메모 B를 고치는 중 A가 실패해도, 초점과 B의 글은 B에 남고 A에는 오류만 (G4 M7 6라운드 1:
        // 실패 때 다시 그리며 A로 초점을 가져가 B에 치던 글이 A로 갈 수 있었다)
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "rec-two-memos-blocked", "--demo steady --screen records");
            WaitAnalyzed(app);
            app.Invoke("records.row.0");
            WaitDetail(app);
            string Value(string id) => ((System.Windows.Automation.ValuePattern)app.Find(id).GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value;
            string Focused() => app.WaitDiag(d => d.GetProperty("focused").ToString(), _ => true, "초점");
            app.Invoke("records.mark.0.edit");
            AppDriver.WaitFor(() => app.Exists("records.mark.0.text"), e => e, "메모 A 칸");
            app.SetText("records.mark.0.text", "A 저장 실패할 글");
            app.Invoke("records.mark.0.save");
            AppDriver.WaitFor(() => app.Exists("records.mark.0.state") ? app.Text("records.mark.0.state") : "", s => s == "저장하는 중…", "A 저장하는 중");
            app.Invoke("records.mark.1.edit");
            AppDriver.WaitFor(() => app.Exists("records.mark.1.text"), e => e, "메모 B 칸");
            app.Find("records.mark.1.text").SetFocus();
            app.SetText("records.mark.1.text", "B 쓰는 중");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "records.mark.1.text", "B 칸 초점");
            Assert.False(app.Exists("records.mark.0.text.error"));   // 아직 재시도 중이어야 이 시험이 그 경우를 본다
            AppDriver.WaitFor(() => app.Exists("records.mark.0.text.error"), e => e, "A 저장 실패 줄", TimeSpan.FromSeconds(20));
            Assert.Equal("records.mark.1.text", Focused());
            Assert.Equal("B 쓰는 중", Value("records.mark.1.text"));
            Assert.Equal("A 저장 실패할 글", Value("records.mark.0.text"));
            Assert.Equal("저장하지 못했습니다", app.Text("records.mark.0.state"));
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 6. 비교 ----------------

    [Fact]
    public void ComparisonMatchesCoreAndMismatchedConditionsSuppressArrows()
    {
        foreach (var (prep, mismatch) in new[] { ("rec-compare", false), ("rec-compare-mismatch", true) })
        {
            var userData = AppDriver.NewUserData();
            try
            {
                using var app = Open(userData, prep, "--demo steady --screen records");
                WaitAnalyzed(app);
                var ids = Sd(app.Diag()).GetProperty("rows").EnumerateArray().Take(2).Select(r => r.GetProperty("Id").GetString()!).ToArray();
                app.Invoke("records.compare");
                app.Invoke("records.row.0");
                app.Invoke("records.row.1");
                var c = app.WaitDiag(d => Sd(d).GetProperty("comparison"), x => x.ValueKind == JsonValueKind.Object, "비교");
                var a = Reanalyze(userData, ids[0]);
                var b = Reanalyze(userData, ids[1]);
                var expected = MeasurementComparison.Compare(new CompareSide(a.Record, a.Analysis, RecordLifecycle.Ended), new CompareSide(b.Record, b.Analysis, RecordLifecycle.Ended));
                Assert.Equal(expected.Conditions.Select(x => x.ToString()), c.GetProperty("conditions").EnumerateArray().Select(x => x.GetString()));
                Assert.Equal(expected.Grade.Lower.ToString(), c.GetProperty("grade").GetString());
                Assert.Equal(expected.PerHour.Lower.ToString(), c.GetProperty("perHour").GetString());
                Assert.Equal(expected.Targets.Select(t => t.LossRate.ToString()), c.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("loss").GetString()));
                if (mismatch)
                {
                    Assert.Contains("TimeoutDiffers", c.GetProperty("conditions").EnumerateArray().Select(x => x.GetString()));
                    Assert.All(c.GetProperty("targets").EnumerateArray(), t => Assert.Equal("None", t.GetProperty("loss").GetString()));
                }
                Assert.Equal(0, app.Close());
            }
            finally { AppDriver.DeleteUserData(userData); }
        }
    }

    [Fact]
    public void UnreadableRecordsCannotBePicked()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "rec-mix", "--demo steady --screen records");
            WaitAnalyzed(app);
            app.Invoke("records.compare");
            var rows = Sd(app.Diag()).GetProperty("rows").EnumerateArray().ToArray();
            var broken = rows.First(r => r.GetProperty("error").GetBoolean()).GetProperty("n").GetInt32();
            AppDriver.WaitFor(() => !app.Find($"records.row.{broken}").Current.IsEnabled, b => b, "고를 수 없는 줄은 꺼짐");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 7. 진단 기록 ----------------

    [Fact]
    public void DiagnosisHistoryReopensTheSavedConclusionAndTheMeasurementShowsItsDiagnosis()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "rec-diag", "--demo steady --screen records");
            WaitAnalyzed(app);
            app.Invoke("records.row.0");
            WaitDetail(app);
            AppDriver.WaitFor(() => app.Exists("records.detail.diagnosis.0"), e => e, "이 측정 중 진단");
            Assert.False(app.Exists("records.detail.diagnosis.1"));
            app.Invoke("records.back");
            app.Select("records.tabs.diagnoses");
            var d = app.WaitDiag(x => x, x => Sd(x).GetProperty("diagnoses").GetInt32() >= 0, "진단 기록");
            var (items, skipped) = new NetPulse.Core.Live.DiagnosisStore(Path.Combine(userData, "diagnoses")).List();
            Assert.Equal((items.Count, skipped), (Sd(d).GetProperty("diagnoses").GetInt32(), Sd(d).GetProperty("diagnosesSkipped").GetInt32()));
            app.Invoke("records.diagnosis.0");
            var open = app.WaitDiag(x => Sd(x).GetProperty("diagnosis"), x => x.ValueKind == JsonValueKind.Object, "지난 진단");
            Assert.Equal(items[0].Stored.MainConclusion, open.GetProperty("MainConclusion").GetInt32());
            Assert.Equal(DiagnosisTexts.Head(items[0].Stored.MainConclusion).Head, app.Text("records.diagnosis.head"));
            Assert.False(app.Exists("diagnose.conclusion.flush"));   // 지난 결과에는 시스템 동작 단추가 없다
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void OpeningARecordFromHomeAfterTheDiagnosesTabStaysInTheDetail()
    {
        // 진단 기록 탭을 본 뒤 홈의 최근 측정을 누르면 그 상세가 열린 채 남는다: 탭 맞추기가 목록으로 덮지 않는다 (G4 M7 1라운드 b3)
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "rec-diag", "--demo steady --screen records");
            WaitAnalyzed(app);
            app.Select("records.tabs.diagnoses");
            app.WaitDiag(x => Sd(x).GetProperty("diagnoses").GetInt32(), n => n >= 0, "진단 기록");
            app.Select("nav.home");
            app.WaitDiag(d => d.GetProperty("recent").GetProperty("listed").GetBoolean()
                && d.GetProperty("recent").GetProperty("items").EnumerateArray().All(x => !x.GetProperty("Analyzing").GetBoolean()), r => r, "최근 측정");
            app.Invoke("home.recent.0");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "records", "기록 화면");
            WaitDetail(app);
            Thread.Sleep(1000);
            Assert.Equal("Detail", Sd(app.Diag()).GetProperty("mode").GetString());
            Assert.True(((System.Windows.Automation.SelectionItemPattern)app.Find("records.tabs.measurements").GetCurrentPattern(System.Windows.Automation.SelectionItemPattern.Pattern)).Current.IsSelected, "탭은 측정 목록");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void ADiagnosisRunAfterVisitingRecordsShowsUpWhenComingBack()
    {
        // 기록 화면을 먼저 본 뒤 새로 돌린 빠른 진단이 다시 들어오면 진단 기록에 나온다 (G4 M7 1라운드 b2)
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "rec-diag-empty", "--demo steady --screen records");
            WaitAnalyzed(app);
            app.Select("records.tabs.diagnoses");
            app.WaitDiag(x => Sd(x).GetProperty("diagnoses").GetInt32(), n => n == 0, "진단 기록 없음");
            app.Select("nav.diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            app.Invoke("diagnose.start");
            app.WaitDiag(x => x, x => M5Fixtures.Str(Sd(x), "operation") == "None" && Sd(x).GetProperty("saved").ValueKind == JsonValueKind.String
                && Sd(x).GetProperty("saved").GetString()!.Length > 0, "진단 저장");
            app.Select("nav.records");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "records", "기록 화면");
            app.WaitDiag(x => Sd(x).GetProperty("diagnoses").GetInt32(), n => n == 1, "새 진단 기록");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 8. 다른 위치의 기록 ----------------

    [Fact]
    public void ExternalRecordOpensWithNoticesAndItsPathIsNotLogged()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using (var app = Open(userData, "rec-external", "--demo steady --screen records --open-record {userData}\\usb\\picked"))
            {
                var d = WaitDetail(app);
                Assert.True(app.Exists("records.external.notice"));
                var found = RecordLocator.FindExternal(Path.Combine(userData, "usb", "picked"), M5Fixtures.RecordRoots(userData));
                Assert.Single(found.Segments);   // 머리가 다른 이웃 조각은 뺐다
                var record = RecordReader.Read(found.Segments);
                var analysis = MeasurementAnalysis.Analyze(record);
                Assert.Equal(analysis.Grade.Grade.ToString(), Detail(d).GetProperty("grade").GetString());
                Assert.Equal(0, app.Close());
                var log = string.Join("\n", app.ReadLog().Select(e => e.GetRawText()));
                Assert.DoesNotContain("usb", log, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("picked", log, StringComparison.OrdinalIgnoreCase);
            }
            using (var app = Open(userData, "rec-notrecord", "--demo steady --screen records --open-record {userData}\\photos"))
            {
                app.WaitDiag(x => M5Fixtures.Str(Sd(x), "externalError"), s => s.Length > 0, "형식 아님");
                Assert.True(app.Exists("records.external.error"));
                Assert.Equal(0, app.Close());
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 9. 홈 최근 측정 → 상세 ----------------

    [Fact]
    public void HomeRecentRowOpensThatRecord()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "rec-ok", "--demo steady --screen home");
            app.WaitDiag(d => d.GetProperty("recent").GetProperty("listed").GetBoolean()
                && d.GetProperty("recent").GetProperty("items").EnumerateArray().All(x => !x.GetProperty("Analyzing").GetBoolean()), r => r, "최근 측정");
            string id = d0(app);
            app.Invoke("home.recent.0");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "records", "기록 화면");
            var detail = WaitDetail(app);
            Assert.Equal(id, Detail(detail).GetProperty("Id").GetString());
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }

        static string d0(AppDriver app) => app.Diag().GetProperty("recent").GetProperty("items").EnumerateArray().First().GetProperty("Id").GetString()!;
    }
}
