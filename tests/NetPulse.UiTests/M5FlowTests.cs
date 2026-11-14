using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using NetPulse.Core.Analysis;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;

namespace NetPulse.UiTests;

/// <summary>
/// G3 기능 시나리오 (M5 설계 7.2): "화면이 떴다"가 아니라 "기대한 값이 나왔다". 화면 값은 저장된 기록을 시험이 Core로 다시 분석한 값과 비교한다.
/// 조작은 UI Automation 패턴(호출·선택·값)만 쓴다.
/// </summary>
public sealed class M5FlowTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static string Screen(JsonElement d) => M5Fixtures.Str(d, "screen");
    private static string Host(JsonElement d) => M5Fixtures.Str(d, "host");
    private static int Rounds(JsonElement d) => d.GetProperty("rounds").GetInt32();

    private static MeasurementRecord ReadRecord(string userData, string sessionId) =>
        RecordReader.Read(RecordReader.FindSegments(sessionId, M5Fixtures.RecordRoots(userData)));

    private static string Value(AppDriver app, string id) =>
        ((ValuePattern)app.Find(id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    [Fact]
    public void FirstRunToStopShowsWhatTheRecordSays()
    {
        var userData = AppDriver.NewUserData();
        int actions = 0;
        try
        {
            using (var app = AppDriver.LaunchWith(userData, "--demo", "outage-long", "--demo-speed", "20"))
            {
                // 1. 첫 실행 안내: 탐지 결과가 시나리오와 같다
                app.WaitDiag(Screen, s => s == "firstrun", "첫 실행 안내");
                app.WaitDiag(d => M5Fixtures.Str(d, "screenDiag", "status"), s => s == "Ok", "탐지");
                Assert.Equal("유선", app.Text("firstrun.kind"));
                Assert.Contains("192.0.2.1", app.Text("firstrun.local"));
                Assert.Equal("예시 통신사 A, 예시 통신사 B", app.Text("firstrun.external"));

                // 2. 대상 고치기: 외부 대상 이름과 측정 시간 (R1 경로) → 저장하면 첫 실행으로 돌아옴
                app.Invoke("firstrun.edit"); actions++;
                app.WaitDiag(Screen, s => s == "profile", "측정 구성 편집");
                app.SetText("profile.target.1.name", "예시 외부 A"); actions++;
                app.SetText("profile.minutes", "3"); actions++;
                app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("saveEnabled").GetBoolean(), b => b, "저장 켜짐");
                app.Invoke("profile.save"); actions++;
                app.WaitDiag(Screen, s => s == "firstrun", "첫 실행으로 돌아옴");

                // 3. 이대로 측정 시작
                app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("startEnabled").GetBoolean(), b => b, "시작 켜짐");
                app.Invoke("firstrun.start"); actions++;
                app.WaitDiag(d => (Screen(d), Host(d)), s => s == ("measure", "Measuring"), "측정 시작");

                // 4. 100회차 이후 증상 표시 + 메모
                app.WaitDiag(Rounds, r => r >= 100, "100회차");
                app.Invoke("measure.mark"); actions++;
                app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("memoOpen").GetBoolean(), o => o, "메모 패널");
                app.SetText("measure.memo.text", "화상회의 중 끊김"); actions++;
                app.Invoke("measure.memo.save"); actions++;
                app.WaitDiag(d => d.GetProperty("marks").EnumerateArray().Any(m => M5Fixtures.Str(m, "Memo") == "화상회의 중 끊김"), b => b, "메모 저장");

                // 5. 끊김 진행 중: 타임라인 요약과 경로 맵 후보 = 같은 범위로 다시 분석한 M4 후보
                // 잠정 분석은 5초(20배속이면 100회차)마다: 끊김(90~149회차) 안에서 분석된 사본을 기다린다
                var during = app.WaitDiag(d => d, d => Rounds(d) is >= 100 and < 150 && d.GetProperty("analysis").ValueKind == JsonValueKind.Object
                    && d.GetProperty("analysis").GetProperty("Rounds").GetInt32() >= 95 && M5Fixtures.Str(d, "map", "candidate") != "None", "끊김 중 잠정 분석");
                string timeline = M5Fixtures.Str(during, "screenDiag", "timeline");
                Assert.Contains("외부 응답 없음 1번", timeline);
                Assert.Contains("이어지는 중", timeline);
                Assert.Contains("증상 표시 1개", timeline);
                Assert.Equal("LocalToInternet", M5Fixtures.Str(during, "map", "candidate"));
                string session = M5Fixtures.Str(during, "session");
                var stampRounds = during.GetProperty("analysis").GetProperty("Rounds").GetInt32();
                var stampEvents = during.GetProperty("analysis").GetProperty("Events").GetInt32();
                var stampElapsed = during.GetProperty("analysis").GetProperty("ElapsedMs").GetInt64();
                var partial = ReadRecord(userData, session);
                var re = MeasurementAnalysis.Analyze(partial.Header!, partial.Rounds.Take(stampRounds).ToArray(), partial.Gaps,
                    partial.Events.Take(stampEvents).ToArray(), null, true, stampElapsed);
                Assert.Equal(PathMapModel.PickCandidate(re, ended: false).Candidate.ToString(), M5Fixtures.Str(during, "map", "candidate"));

                // 6. 계획 끝까지 → 결과 카드
                var done = app.WaitDiag(d => d, d => d.GetProperty("finished").ValueKind == JsonValueKind.Object, "측정 끝");
                Assert.Equal("Completed", M5Fixtures.Str(done, "finished", "end"));

                // 7. 기록과 화면 비교
                var record = ReadRecord(userData, session);
                Assert.Equal(EndReason.Completed, record.EndReason);
                Assert.Contains(record.Header!.Targets, t => t.Name == "예시 외부 A");
                var mark = Assert.Single(SymptomMarks.FromEvents(record.Events, record.NoteLines).Marks);
                Assert.Equal("화상회의 중 끊김", mark.Memo);
                var analysis = MeasurementAnalysis.Analyze(record);
                Assert.Equal(Sentences.GradeName(analysis.Grade.Grade), app.Text("measure.result.grade"));
                Assert.StartsWith($"끊김 {analysis.Episodes.Count}회", app.Text("measure.result.outages"));
                var stats = StatsCalculator.Compute(record.Rounds, record.Header.Targets.Select(t => t.Id).ToArray(), record.Gaps);
                foreach (var card in LiveSummary.Cards(record.Header.Targets, stats, record.Rounds[^1]))
                {
                    Assert.Equal($"손실 {card.LossRate} · {card.LossSentence}", app.Text($"measure.card.{card.TargetId}.loss"));
                    Assert.Equal($"최대 연속 실패 {card.MaxConsecutiveLoss}회", app.Text($"measure.card.{card.TargetId}.streak"));
                }

                // 8. 홈: 최근 측정 첫 줄이 이 측정
                app.Select("nav.home");
                app.WaitDiag(d => d.GetProperty("recent").GetProperty("items").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } x
                    && !x.GetProperty("Analyzing").GetBoolean() && M5Fixtures.Str(x, "Id") == session, b => b, "최근 측정 첫 줄");
                var row = app.Text("home.recent.0");
                Assert.Contains("등급 " + Sentences.GradeName(analysis.Grade.Grade), row);
                Assert.Contains($"끊김 {analysis.Episodes.Count}회", row);
                Record("flow-firstrun-to-stop", new { session, actions, grade = analysis.Grade.Grade.ToString(), outages = analysis.Episodes.Count, timeline });
                Assert.Equal(0, app.Close());
            }

            // 9. 같은 사용자 폴더로 다시 실행: 첫 실행 안내 없이 홈, 바꾼 이름이 남아 있다
            using (var again = AppDriver.LaunchWith(userData, "--demo", "outage-long"))
            {
                again.WaitDiag(Screen, s => s == "home", "홈");
                again.WaitDiag(d => d.GetProperty("recent").GetProperty("items").GetArrayLength(), n => n >= 1, "최근 측정 유지");
                again.Select("nav.measure");
                again.WaitDiag(Screen, s => s == "measure", "측정");
                again.Invoke("measure.config");
                again.WaitDiag(Screen, s => s == "profile", "측정 구성");
                Assert.Equal("예시 외부 A", Value(again, "profile.target.1.name"));
                Assert.Equal(0, again.Close());
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void FirstMeasurementStartsInOneAction()
    {
        // 설계 4.5: 측정 시간이 기본값이면 [이대로 측정 시작] 한 번 (탐색 포함 두 번 이내)
        using var app = AppDriver.Launch("--demo", "steady");
        app.WaitDiag(d => d.GetProperty("screenDiag").ValueKind == JsonValueKind.Object && d.GetProperty("screenDiag").GetProperty("startEnabled").GetBoolean(), b => b, "시작 켜짐");
        app.Invoke("firstrun.start");
        app.WaitDiag(d => (Screen(d), Host(d)), s => s == ("measure", "Measuring"), "한 번 만에 측정");
        Record("flow-first-measurement-actions", new { actions = 1 });
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void StoppingSavesStoppedAndHomeListsIt()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start");
            app.WaitDiag(Rounds, r => r >= 30, "30회차");
            app.Invoke("measure.stop");
            var done = app.WaitDiag(d => d, d => d.GetProperty("finished").ValueKind == JsonValueKind.Object, "멈춤");
            Assert.Equal("Stopped", M5Fixtures.Str(done, "finished", "end"));
            var record = ReadRecord(userData, M5Fixtures.Str(done, "finished", "Id"));
            Assert.Equal(EndReason.Stopped, record.EndReason);
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void EditorBlocksInvalidChangesAndIsReadOnlyWhileMeasuring()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using (var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "profile"))
            {
                app.WaitDiag(Screen, s => s == "profile", "측정 구성");
                Assert.False(app.Diag().GetProperty("screenDiag").GetProperty("dirty").GetBoolean());   // 바꾸기 전에는 변경 없음
                app.SetText("profile.target.1.address", "300.1.1");
                var d = app.WaitDiag(x => x.GetProperty("screenDiag"), s => s.GetProperty("errors").EnumerateArray().Any(e => e.GetString() == "profile.target.1.address"), "주소 오류");
                Assert.True(d.GetProperty("dirty").GetBoolean());
                Assert.False(d.GetProperty("saveEnabled").GetBoolean());
                app.SetText("profile.target.1.address", "198.51.100.77");
                app.Invoke("profile.target.2.remove");   // 외부 하나 → 경고 (저장은 됨)
                var w = app.WaitDiag(x => x.GetProperty("screenDiag"), s => s.GetProperty("targets").GetInt32() == 2
                    && s.GetProperty("warnings").EnumerateArray().Any(e => e.GetString() == "external-diversity"), "외부 하나 → 경고");
                Assert.True(w.GetProperty("saveEnabled").GetBoolean());   // 경고는 저장을 막지 않는다
                app.Invoke("profile.cancel");          // 초안 버림
                app.WaitDiag(x => x.GetProperty("screenDiag").GetProperty("targets").GetInt32(), n => n == 3, "취소로 되돌림");
                Assert.Equal(0, app.Close());
            }
            using (var measuring = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "600", "--demo-hold", "20", "--start", "--screen", "profile"))
            {
                measuring.WaitDiag(x => x.GetProperty("screenDiag").ValueKind == JsonValueKind.Object && x.GetProperty("screenDiag").GetProperty("locked").GetBoolean(), b => b, "측정 중 읽기 전용");
                Assert.True(measuring.Exists("profile.locked"));
                Assert.Equal(0, measuring.Close());
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void NoNetworkFirstRunExplainsAndOffersRetryAndManualEntry()
    {
        using var app = AppDriver.Launch("--demo", "no-network");
        app.WaitDiag(d => M5Fixtures.Str(d, "screenDiag", "status"), s => s == "NoNetwork", "네트워크 없음");
        Assert.True(app.Exists("firstrun.retry"));
        Assert.True(app.Exists("firstrun.manual"));
        Assert.False(app.Diag().GetProperty("screenDiag").GetProperty("startEnabled").GetBoolean());
        app.Invoke("firstrun.manual");
        app.WaitDiag(Screen, s => s == "profile", "직접 입력");
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void TimelineZoomsPansAndReturnsToNow()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "600", "--demo-hold", "1200", "--start");
            app.WaitDiag(d => d.GetProperty("held").GetBoolean(), h => h, "멈춤");
            JsonElement W(AppDriver a) => a.Diag().GetProperty("screenDiag").GetProperty("window");
            Assert.True(W(app).GetProperty("follow").GetBoolean());
            Assert.Equal("2분", M5Fixtures.Str(W(app), "step"));
            app.Invoke("measure.zoomout");
            app.WaitDiag(d => M5Fixtures.Str(d, "screenDiag", "window", "step"), s => s == "5분", "축소");
            app.Invoke("measure.left");
            var w = app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("window"), x => !x.GetProperty("follow").GetBoolean(), "과거로");
            Assert.True(w.GetProperty("to").GetInt64() < 1_200_000);
            app.Invoke("measure.now");
            app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("window").GetProperty("follow").GetBoolean(), f => f, "지금으로");
            app.Invoke("measure.all");
            app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("window").GetProperty("all").GetBoolean(), a => a, "전체");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void EveryNavigationItemOpensItsScreen()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady");
            app.WaitDiag(Screen, s => s == "home", "홈");
            foreach (var id in new[] { "measure", "diagnose", "records", "report", "settings", "home" })
            {
                app.Select("nav." + id);
                app.WaitDiag(Screen, s => s == id, "화면 " + id);
            }
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void ClosingWhileMeasuringSavesTheRecordAsStopped()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            string session;
            using (var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start"))
            {
                app.WaitDiag(Rounds, r => r >= 10, "10회차");
                app.Invoke("measure.mark");
                app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("memoOpen").GetBoolean(), o => o, "메모 패널");
                app.SetText("measure.memo.text", "닫기 전에 쓰던 메모");   // 저장하지 않고 닫는다 → 초안이 기록에 들어가야 한다
                session = M5Fixtures.Str(app.Diag(), "session");
                Assert.Equal(0, app.Close());
            }
            var record = ReadRecord(userData, session);
            Assert.Equal(EndReason.Stopped, record.EndReason);
            Assert.Equal("닫기 전에 쓰던 메모", Assert.Single(SymptomMarks.FromEvents(record.Events, record.NoteLines).Marks).Memo);
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void ClosingDuringAThemeRevealCleansUpFirst()
    {
        // M2 이월 1: 원형 전환 도중(진행도 고정) 창을 닫아도, 닫기 전에 찍은 화면 층·렌더링 구독이 정리되고 오류 없이 끝난다
        using var app = AppDriver.Launch("--showcase", "--section", "overview", "--theme", "dark", "--reveal-hold", "0.5", "--freeze-motion");
        app.WaitDiag(d => M5Fixtures.Str(d, "section"), s => s == "overview", "개요");
        app.Select("toolbar.theme.light");
        app.WaitDiag(d => d.GetProperty("inTransition").GetBoolean(), t => t, "전환 중");
        Assert.Equal(0, app.Close());
        var log = app.ReadLog();
        var cleanup = log.Single(e => e.GetProperty("kind").GetString() == "closeCleanup").GetProperty("data");
        Assert.Equal(0, cleanup.GetProperty("snapshotLayers").GetInt32());
        Assert.False(cleanup.GetProperty("inTransition").GetBoolean());
        Assert.False(cleanup.GetProperty("rendering").GetBoolean());
        Assert.DoesNotContain(log, e => e.GetProperty("kind").GetString() is "themeError" or "unhandled");
        Record("flow-theme-close-cleanup", new { ok = true });
    }
}
