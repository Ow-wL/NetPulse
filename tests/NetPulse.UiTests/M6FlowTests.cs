using System.IO;
using System.Text.Json;
using NetPulse.Core.Diagnostics;

namespace NetPulse.UiTests;

/// <summary>
/// G3 진단 흐름 (M6 설계 7.2). 장면마다 기대 결론 번호와 검사 판정 일곱을 시험이 적어 두고 화면과 비교한다.
/// 저장된 진단 기록을 다시 읽어 그 판정으로 <see cref="QuickDiagnosis.Conclude"/>를 부른 결과도 화면 결론과 같아야 한다.
/// 조작은 UI Automation 패턴만 쓴다 (키보드만으로 하는 흐름은 <see cref="KeyboardTests"/>).
/// </summary>
public sealed class M6FlowTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static JsonElement Sd(JsonElement d) => d.GetProperty("screenDiag");
    private static string S(JsonElement d, params string[] path) => M5Fixtures.Str(Sd(d), path);

    /// <summary>검사 순서대로의 판정 일곱: Adapter, Gateway, Tcp, Dns, Https, Portal, Link.</summary>
    private static readonly CheckId[] Order = [CheckId.Adapter, CheckId.Gateway, CheckId.Tcp, CheckId.Dns, CheckId.Https, CheckId.Portal, CheckId.Link];

    private const string N = "Normal", C = "Check", F = "Fail", U = "Unmeasured";

    /// <summary>결론별 장면 8종 (설계 7.2-2): 시나리오, 기대 결론, 판정 일곱.</summary>
    private static readonly (string Scenario, int Conclusion, string[] Judgments)[] Conclusions =
    [
        ("diag-ok", 11, [N, N, N, N, N, N, N]),
        ("diag-dns", 3, [N, N, N, F, F, N, N]),
        ("diag-outside", 6, [N, N, F, F, F, N, N]),
        ("diag-link", 1, [F, N, F, F, F, N, F]),
        ("diag-portal", 4, [N, N, N, N, F, C, N]),
        ("diag-partial", 10, [N, N, C, N, N, N, N]),
        ("diag-insufficient", 12, [N, N, U, U, U, N, U]),
        ("diag-ipv4", 2, [N, N, F, N, N, N, N]),
    ];

    private static AppDriver Open(string userData, string prep, string args)
    {
        M5Fixtures.Prepare(userData, prep);
        return AppDriver.LaunchWith(userData, [.. M5Fixtures.Args(args), "--freeze-motion"]);
    }

    /// <summary>진단이 하나 더 끝날 때까지 (저장 파일 이름이 바뀌거나 저장 오류). 돌려주는 값은 그때의 진단 값.</summary>
    private static JsonElement WaitRun(AppDriver app, string? previousSaved = null) =>
        app.WaitDiag(d => d, d => S(d, "operation") == "None" && Sd(d).GetProperty("conclusion").ValueKind == JsonValueKind.Number
            && (S(d, "saved") is { Length: > 0 } f && f != previousSaved || S(d, "saveError").Length > 0), "진단 끝");

    private static void AssertJudgments(JsonElement d, string[] expected, string scene)
    {
        var checks = Sd(d).GetProperty("checks");
        for (int i = 0; i < Order.Length; i++)
            Assert.True(checks.GetProperty(Order[i].ToString()).GetString() == expected[i], $"{scene}: {Order[i]} 판정 {checks.GetProperty(Order[i].ToString()).GetString()} (기대 {expected[i]})");
    }

    /// <summary>저장된 기록을 다시 읽어: 결론 번호·판정이 화면과 같고, 그 판정으로 다시 결론을 내도 같다.</summary>
    private static StoredDiagnosis AssertRecordMatches(string userData, JsonElement d)
    {
        var file = Path.Combine(userData, "diagnoses", S(d, "saved"));
        var stored = DiagnosisRecord.Parse(File.ReadAllText(file));
        int screen = Sd(d).GetProperty("conclusion").GetInt32();
        Assert.Equal(screen, stored.MainConclusion);
        foreach (var c in stored.Checks) Assert.Equal(Sd(d).GetProperty("checks").GetProperty(c.Id.ToString()).GetString(), c.Judgment.ToString());
        var again = QuickDiagnosis.Conclude(stored.Checks.Select(c => new CheckResult(c.Id, c.Judgment, c.PartlyUnconfirmed, c.State, c.Code, c.Targets,
            TimeSpan.FromMilliseconds(c.ElapsedMs), new Dictionary<string, string>())).ToArray());
        Assert.Equal(screen, again.Main.Number);
        return stored;
    }

    private static int Calls(JsonElement d, string call) => Sd(d).GetProperty("actions").EnumerateArray().Count(a => a.GetString() == call);

    // ---------------- 1. 처음 → 진행 → 결과 ----------------

    [Fact]
    public void FirstDiagnosisShowsProgressThenTheResultAndSavesTheSameRecord()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "profile", "--demo tests:diag-slow-ok --screen diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            Assert.Equal("아직 진단하지 않았습니다", app.Text("diagnose.conclusion.head"));
            app.Invoke("diagnose.start");
            // 진행 장면: 앞 검사는 끝나고 늦춘 검사는 아직 (끝난 검사 카드부터 채움)
            var running = app.WaitDiag(d => d, d => S(d, "operation") == "Diagnosing" && Sd(d).GetProperty("partial").GetInt32() is >= 1 and < 7, "진행 중");
            Assert.StartsWith("검사 중", app.Text("diagnose.conclusion.head"));
            Assert.False(app.Find("diagnose.start").Current.IsEnabled);
            var d = WaitRun(app);
            Assert.Equal(11, Sd(d).GetProperty("conclusion").GetInt32());
            AssertJudgments(d, Conclusions[0].Judgments, "diag-slow-ok");
            Assert.Equal(S(d, "head"), app.Text("diagnose.conclusion.head"));
            AssertRecordMatches(userData, d);
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 2. 결론별 장면 8종 ----------------

    [Fact]
    public void EachConclusionSceneShowsTheExpectedConclusionJudgmentsAndScope()
    {
        var seen = new List<object>();
        foreach (var (scenario, conclusion, judgments) in Conclusions)
        {
            var userData = AppDriver.NewUserData();
            try
            {
                using var app = Open(userData, "profile", $"--demo tests:{scenario} --screen diagnose");
                app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
                app.Invoke("diagnose.start");
                var d = WaitRun(app);
                Assert.True(Sd(d).GetProperty("conclusion").GetInt32() == conclusion, $"{scenario}: 결론 {Sd(d).GetProperty("conclusion").GetInt32()} (기대 {conclusion})");
                AssertJudgments(d, judgments, scenario);
                // 관측 범위 줄: 저장된 검사 판정으로 Core가 만드는 범위 줄이 있을 때만 (일부 성공·게이트웨이 무응답 단서)
                var stored = AssertRecordMatches(userData, d);
                var scope = DiagnosisTexts.Scope(stored.Checks.Select(c => new CheckResult(c.Id, c.Judgment, c.PartlyUnconfirmed, c.State, c.Code, c.Targets,
                    TimeSpan.FromMilliseconds(c.ElapsedMs), new Dictionary<string, string>())).ToArray());
                Assert.True(scope.Count > 0 == app.Exists("diagnose.conclusion.scope"), $"{scenario}: 관측 범위 줄 {scope.Count}개, 화면 {app.Exists("diagnose.conclusion.scope")}");
                // 그 자리 도구: 결론 3 → 비우기, 결론 1·5 → 네트워크 설정
                Assert.Equal(conclusion == 3, app.Exists("diagnose.conclusion.flush"));
                Assert.Equal(conclusion is 1 or 5, app.Exists("diagnose.conclusion.settings"));
                seen.Add(new { scenario, conclusion, judgments, head = S(d, "head") });
                Assert.Equal(0, app.Close());
            }
            finally { AppDriver.DeleteUserData(userData); }
        }
        Record("flow-diagnose", new { conclusions = seen });
    }

    // ---------------- 3. 다시 검사 → 비교 띠 ----------------

    [Fact]
    public void RunningAgainComparesWithTheRecordThenWithTheLastRun()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            // 지난 기록 (결론 6: 외부 응답 없음) → 이번 정상 → 바뀐 검사 셋
            using var app = Open(userData, "diagrecord", "--demo tests:diag-ok --screen diagnose");
            app.WaitDiag(d => Sd(d).GetProperty("recent").ValueKind, k => k == JsonValueKind.Number, "지난 진단 기록");
            app.Invoke("diagnose.start");
            var first = WaitRun(app);
            Assert.Equal("지난 진단 기록과 달라진 검사 3개", S(first, "compare"));
            Assert.Equal(["Tcp", "Dns", "Https"], Sd(first).GetProperty("changes").EnumerateArray().Select(e => e.GetString()));
            app.Invoke("diagnose.start");
            var second = WaitRun(app, S(first, "saved"));
            Assert.Equal("지난 검사와 같습니다.", S(second, "compare"));
            Assert.Equal(0, Sd(second).GetProperty("changes").GetArrayLength());
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 4. 결과 복사 → 실제 클립보드 ----------------

    [Fact]
    public void CopyPutsExactlyTheCopyTextOnTheRealClipboard()
    {
        // 시작 때 클립보드에 글이 아닌 내용이 있으면 덮어쓰지 않고 미완료로 둔다 (구현 중 결정 "클립보드 되읽기·시험 복원")
        var saved = ClipboardText.Save();
        if (saved.Blocked) Assert.Skip("미완료: 클립보드에 그대로 되돌릴 수 없는 형식이 있어 덮어쓰지 않았습니다.");
        var userData = AppDriver.NewUserData();
        string? ours = null;   // 시험(앱)이 클립보드에 쓴 글. null이면 쓰지 않았다 → 되돌리지 않는다
        try
        {
            using var app = Open(userData, "profile", "--demo tests:diag-dns --screen diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            app.Invoke("diagnose.start");
            var d = WaitRun(app);
            // 보관한 뒤 다른 곳에서 클립보드를 바꿨으면 (사용자가 새로 복사) 덮어쓰지 않고 미완료 (G4 3라운드 2)
            if (ClipboardText.Sequence() != saved.Sequence) Assert.Skip("미완료: 시험 중 클립보드가 바뀌어 덮어쓰지 않았습니다.");
            ours = S(d, "copyText");
            app.Invoke("diagnose.copy");
            app.WaitDiag(d2 => S(d2, "copyState"), s => s.Length > 0, "복사 결과");
            Assert.Equal("ok", S(app.Diag(), "copyState"));
            Assert.Equal(S(d, "copyText"), ClipboardText.Read());
            Assert.Equal(0, app.Close());
        }
        finally
        {
            ClipboardText.Restore(saved, ours);
            AppDriver.DeleteUserData(userData);
        }
    }

    // ---------------- 5. 사이트 점검 ----------------

    [Fact]
    public void SiteCheckRejectsBadInputAndShowsOkFailAndTimeout()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "profile", "--demo tests:diag-ok --screen diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            foreach (var (input, code) in new[] { ("https://shop.example/path", "not-domain"), ("192.0.2.10", "ip-address"), ("shop", "no-dot") })
            {
                app.SetText("diagnose.site.input", input);
                app.Invoke("diagnose.site.run");
                app.WaitDiag(d => S(d, "site", "InputError"), s => s == code, "입력 오류 " + code);
                var d = app.Diag();
                Assert.False(Sd(d).GetProperty("site").GetProperty("Running").GetBoolean());   // 실행하지 않음
                Assert.Equal("", S(d, "site", "dns"));
                Assert.True(app.Exists("diagnose.site.input.error"), "칸 오류가 보여야 한다: " + input);
            }
            int done = 0;
            foreach (var (domain, https, state) in new[] { ("shop.example", "Normal", "Run"), ("down.example", "Fail", "Run"), ("slow.example", "Unmeasured", "Timeout") })
            {
                app.SetText("diagnose.site.input", domain);
                app.Invoke("diagnose.site.run");
                // 끝난 점검 수가 늘어난 그 진단 값으로 판정하고, 도메인은 화면 글로 본다 (진단 값에 도메인을 넣지 않음, 설계 결정)
                var d = app.WaitDiag(x => x, x => !Sd(x).GetProperty("site").GetProperty("Running").GetBoolean() && Sd(x).GetProperty("site").GetProperty("checks").GetInt32() == done + 1, "사이트 점검 " + domain);
                done++;
                Assert.Equal(domain, app.Text("diagnose.site.domain"));
                Assert.Equal("Normal", S(d, "site", "dns"));
                Assert.Equal(https, S(d, "site", "https"));
                Assert.Equal(state, S(d, "site", "httpsState"));
            }
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>
    /// 점검 중 입력을 고치고 Enter를 다시 누르면 바쁨으로 끝나고, 진행 중인 점검의 사용자 조작 기준을 덮지 않는다 (G4 3라운드 1).
    /// 끝났을 때 기준보다 조작이 많으므로 완료 처리는 초점·스크롤을 건드리지 않는다.
    /// </summary>
    [Fact]
    public void EnterWhileASiteCheckRunsKeepsThatChecksBaseline()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "profile", "--demo tests:diag-ok --screen diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            app.SetText("diagnose.site.input", "slow.example");
            app.Invoke("diagnose.site.run");
            var running = app.WaitDiag(x => x, x => Sd(x).GetProperty("site").GetProperty("Running").GetBoolean()
                && Sd(x).GetProperty("guard").GetProperty("site").GetInt64() >= 0, "점검 중, 기준 잡힘");
            long baseline = Sd(running).GetProperty("guard").GetProperty("site").GetInt64();
            app.SetText("diagnose.site.input", "shop.example");   // 진행 중 입력 수정
            app.PressEnter("diagnose.site.input");                 // 진행 중 Enter → 바쁨
            var after = app.WaitDiag(x => x, x => Sd(x).GetProperty("guard").GetProperty("inputs").GetInt64() > baseline, "조작이 세어짐");
            Assert.True(Sd(after).GetProperty("site").GetProperty("Running").GetBoolean(), "앞 점검이 아직 진행 중이어야 한다 (시나리오 시간 부족)");
            Assert.Equal(baseline, Sd(after).GetProperty("guard").GetProperty("site").GetInt64());
            var done = app.WaitDiag(x => x, x => !Sd(x).GetProperty("site").GetProperty("Running").GetBoolean(), "앞 점검 끝남");
            Assert.Equal(baseline, Sd(done).GetProperty("guard").GetProperty("site").GetInt64());
            Assert.Equal("slow.example", app.Text("diagnose.site.domain"));   // 끝난 것은 앞 점검
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 6. DNS 비우기 ----------------

    [Fact]
    public void FlushAsksFirstCancelDoesNothingConfirmFlushesOnceAndRechecks()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "profile", "--demo tests:diag-dns --screen diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            app.Invoke("diagnose.start");
            var first = WaitRun(app);
            Assert.Equal(3, Sd(first).GetProperty("conclusion").GetInt32());
            app.Invoke("diagnose.conclusion.flush");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "확인 대화상자");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "dialog.cancel", "초점은 취소");
            app.Invoke("dialog.cancel");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => !o, "닫힘");
            Assert.Equal(0, Calls(app.Diag(), "flushDns"));
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "diagnose.conclusion.flush", "누른 단추로 초점 복귀");
            app.Invoke("diagnose.conclusion.flush");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "다시 열림");
            app.Invoke("dialog.confirm");
            var after = WaitRun(app, S(first, "saved"));
            Assert.Equal("ok", S(after, "flushResult"));
            Assert.Equal(1, Calls(after, "flushDns"));
            Assert.Equal("DNS 캐시를 비웠습니다. 다시 검사한 결과입니다.", app.Text("diagnose.flushresult"));
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void FlushDuringADiagnosisWaitsForItThenFlushesAndRechecks()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "profile", "--demo tests:diag-queued --screen diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            app.Invoke("diagnose.start");
            app.WaitDiag(d => S(d, "operation"), o => o == "Diagnosing", "진단 중");
            app.Invoke("diagnose.flush");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "확인 대화상자");
            app.Invoke("dialog.confirm");
            app.WaitDiag(d => S(d, "flush"), f => f == "Waiting", "예약 (진단이 끝나기를 기다림)");
            Assert.Equal(0, Calls(app.Diag(), "flushDns"));   // 진단이 끝나기 전에는 비우지 않는다
            var done = app.WaitDiag(d => d, d => S(d, "operation") == "None" && S(d, "flushResult") == "ok", "비우고 다시 진단");
            Assert.Equal(1, Calls(done, "flushDns"));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(userData, "diagnoses"), "*.json").Length);   // 앞 진단 + 다시 진단
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    public static TheoryData<string, string> FlushFailures => new()
    {
        { "diag-flush-fail", "DNS 캐시를 비우지 못했습니다: 명령이 실패했습니다 (종료 코드 1). 다시 검사한 결과입니다." },
        { "diag-flush-timeout", "DNS 캐시 비우기가 10초 안에 끝나지 않았습니다. 다시 검사한 결과입니다." },
    };

    [Theory, MemberData(nameof(FlushFailures))]
    public void FlushFailureAndTimeoutAreSaidAndTheRecheckStillRuns(string scenario, string message)
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "profile", $"--demo tests:{scenario} --screen diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            app.Invoke("diagnose.start");
            var first = WaitRun(app);
            app.Invoke("diagnose.flush");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "확인 대화상자");
            app.Invoke("dialog.confirm");
            var after = WaitRun(app, S(first, "saved"));
            Assert.NotEqual("ok", S(after, "flushResult"));
            Assert.Equal(message, app.Text("diagnose.flushresult"));
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 7. 측정 중 진단 → 진단 중 측정 중지 ----------------

    [Fact]
    public void DiagnosisKeepsTheMeasurementIdItStartedWithAndHomeCardOpensIt()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "profile", "--demo tests:diag-running --demo-speed 20 --start");
            app.WaitDiag(d => M5Fixtures.Str(d, "host"), s => s == "Measuring", "측정 중");
            string session = M5Fixtures.Str(app.Diag(), "session");
            app.Select("nav.diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            app.Invoke("diagnose.start");
            app.WaitDiag(d => S(d, "operation"), o => o == "Diagnosing", "진단 중");
            // 진단 중에 측정을 멈춘다 (늦춘 검사가 끝나기 전)
            app.Select("nav.measure");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "measure", "측정 화면");
            app.Invoke("measure.stop");
            app.WaitDiag(d => d.GetProperty("finished").ValueKind, k => k == JsonValueKind.Object, "측정 끝");
            app.Select("nav.diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            var d = WaitRun(app);
            Assert.Equal(session, S(d, "measurementId"));
            var stored = AssertRecordMatches(userData, d);
            Assert.Equal(session, stored.MeasurementId);   // 진단을 시작한 순간의 측정
            app.Select("nav.home");
            app.WaitDiag(x => M5Fixtures.Str(x, "screen"), s => s == "home", "홈");
            AppDriver.WaitFor(() => app.Exists("home.diagnosis.last"), e => e, "홈 진단 카드");   // 홈은 이동 뒤 그려진다
            app.Invoke("home.diagnosis.open");
            app.WaitDiag(x => M5Fixtures.Str(x, "screen"), s => s == "diagnose", "홈 카드 → 진단 화면");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 8. 기록 폴더 열기 ----------------

    [Fact]
    public void RecordFolderOpensTheMeasurementSegmentWhileMeasuringOtherwiseTheRoot()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "profile", "--demo tests:diag-ok --demo-speed 20 --start");
            app.WaitDiag(d => d.GetProperty("rounds").GetInt32(), r => r >= 3, "기록 시작");
            app.Select("nav.diagnose");
            app.WaitDiag(d => Sd(d).GetProperty("folderEnabled").GetBoolean(), b => b, "폴더 단추 켜짐");
            app.Invoke("diagnose.folder");
            app.WaitDiag(d => S(d, "lastAction"), a => a == "folder:segment", "측정 중 → 측정 조각 폴더");
            app.Select("nav.measure");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "measure", "측정 화면");
            app.Invoke("measure.stop");
            app.WaitDiag(d => d.GetProperty("finished").ValueKind, k => k == JsonValueKind.Object, "측정 끝");
            app.Select("nav.diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            app.Invoke("diagnose.folder");
            app.WaitDiag(d => S(d, "lastAction"), a => a == "folder:root", "측정 밖 → 기록 뿌리");
            Assert.Equal(["folder:segment", "folder:root"], Sd(app.Diag()).GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 10. 작업 중 창 닫기 반복 (설계 6.1) ----------------

    public static TheoryData<int> Repeat => new() { 1, 2, 3, 4, 5 };

    /// <summary>진단 중 닫기: 종료 코드 0, 종료 뒤 끝난 결과는 저장하지 않는다 (진단 기록 없음).</summary>
    [Theory, MemberData(nameof(Repeat))]
    public void ClosingDuringADiagnosisExitsCleanlyWithoutSaving(int round)
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "profile", "--demo tests:diag-running --screen diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            app.Invoke("diagnose.start");
            app.WaitDiag(d => Sd(d).GetProperty("partial").GetInt32(), n => n >= 1, "진행 중");
            Assert.Equal(0, app.Close());   // 비정상 종료면 Close가 증거를 남기고 실패시킨다
            var folder = Path.Combine(userData, "diagnoses");
            Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder, "*.json").Length == 0, $"{round}회: 종료 뒤 진단 기록이 저장됐다");
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>비우기 흐름 중 닫기: 종료 코드 0, 앞 진단 기록 하나만 (다시 진단 결과는 저장하지 않음).</summary>
    [Theory, MemberData(nameof(Repeat))]
    public void ClosingDuringAFlushExitsCleanlyKeepingOnlyTheFirstRecord(int round)
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = Open(userData, "profile", "--demo tests:diag-flush-slow --screen diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            app.Invoke("diagnose.start");
            WaitRun(app);
            app.Invoke("diagnose.flush");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "확인 대화상자");
            app.Invoke("dialog.confirm");
            app.WaitDiag(d => S(d, "flush"), f => f == "Flushing", "비우는 중");
            Assert.Equal(0, app.Close());
            Assert.True(Directory.GetFiles(Path.Combine(userData, "diagnoses"), "*.json").Length == 1, $"{round}회: 진단 기록은 앞 진단 하나여야 한다");
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>테마 전환(원형 공개) 중 닫기 5회: M5 시험과 같은 조건을 되풀이해 종료 코드 0과 정리를 본다.</summary>
    [Theory, MemberData(nameof(Repeat))]
    public void ClosingDuringAThemeRevealExitsCleanly(int round)
    {
        using var app = AppDriver.Launch("--showcase", "--section", "overview", "--theme", "dark", "--reveal-hold", "0.5", "--freeze-motion");
        app.WaitDiag(d => M5Fixtures.Str(d, "section"), s => s == "overview", "개요");
        app.Select("toolbar.theme.light");
        app.WaitDiag(d => d.GetProperty("inTransition").GetBoolean(), t => t, "전환 중");
        Assert.True(app.Close() == 0, $"{round}회");
        Assert.DoesNotContain(app.ReadLog(), e => e.GetProperty("kind").GetString() is "themeError" or "unhandled");
    }
}

/// <summary>
/// 시험이 쓰는 실제 클립보드 (STA 스레드). 시작 때 모든 형식을 값째 보관하고 끝나면 되돌린다. 그대로 되돌릴 수 없는 형식이 있으면 막힘(덮어쓰지 않음).
/// 클립보드 내용은 기록에 남기지 않는다.
/// </summary>
internal static class ClipboardText
{
    /// <summary>보관한 형식과 그 값 (글은 string, 그 밖은 바이트). 막힘이면 보관하지 않았다.</summary>
    public sealed record Saved(bool Blocked, IReadOnlyList<KeyValuePair<string, object>> Items, uint Sequence);

    /// <summary>클립보드가 바뀔 때마다 느는 시스템 번호.</summary>
    public static uint Sequence() => Native.GetClipboardSequenceNumber();

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    }

    private static T Sta<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("클립보드 접근 실패", error);
        return result;
    }

    /// <summary>
    /// 지금 클립보드의 모든 형식을 값째 보관한다. 하나라도 글(string)이나 바이트 흐름(MemoryStream)으로 읽히지 않으면
    /// 그대로 되돌릴 수 없으므로 막힘 (그림·핸들 형식 등, 덮어쓰지 않는다, G4 1라운드 b3). 클립보드 관리 도구가 붙이는
    /// 표시 형식처럼 바이트로 읽히는 형식은 보관했다가 그대로 되돌린다.
    /// </summary>
    public static Saved Save() => Sta(() =>
    {
        uint sequence = Sequence();
        var data = System.Windows.Forms.Clipboard.GetDataObject();
        var items = new List<KeyValuePair<string, object>>();
        foreach (var f in data?.GetFormats(autoConvert: false) ?? [])
        {
            object? v;
            try { v = data!.GetData(f, autoConvert: false); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or OutOfMemoryException or InvalidOperationException) { return new Saved(true, [], sequence); }
            switch (v)
            {
                case string text: items.Add(new(f, text)); break;
                case MemoryStream ms: items.Add(new(f, ms.ToArray())); break;
                default: return new Saved(true, [], sequence);
            }
        }
        // 읽는 사이에 바뀌었으면 섞인 보관일 수 있다: 막힘
        return Sequence() == sequence ? new Saved(false, items, sequence) : new Saved(true, [], sequence);
    });

    public static string Read() => Sta(() => System.Windows.Forms.Clipboard.GetText());

    /// <summary>
    /// 시험이 쓴 글(<paramref name="ours"/>)이 아직 그대로 있을 때만 보관한 것을 되돌린다 (G4 3라운드 2).
    /// 시험이 쓰지 않았거나(null), 그 뒤 다른 곳에서 바꿨으면(사용자가 새로 복사) 그 내용을 지우거나 덮지 않는다.
    /// </summary>
    public static void Restore(Saved saved, string? ours)
    {
        if (saved.Blocked || ours is null) return;
        Sta(() =>
        {
            // 글이 시험의 글 그대로인지로 본다 (클립보드 관리 도구가 표시 형식을 덧붙여도 시험의 내용이다)
            bool stillOurs = System.Windows.Forms.Clipboard.ContainsText() && System.Windows.Forms.Clipboard.GetText() == ours;
            if (!stillOurs) return false;
            if (saved.Items.Count == 0) { System.Windows.Forms.Clipboard.Clear(); return true; }
            var o = new System.Windows.Forms.DataObject();
            foreach (var (f, v) in saved.Items) o.SetData(f, false, v is byte[] bytes ? new MemoryStream(bytes) : v);
            System.Windows.Forms.Clipboard.SetDataObject(o, copy: true, retryTimes: 10, retryDelay: 100);
            return true;
        });
    }
}
