using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using NetPulse.Core.Analysis;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Report;

namespace NetPulse.UiTests;

/// <summary>
/// G3 리포트 흐름 (M8 설계 7.2). 내보낸 파일에서 <c>data-field</c>로 값을 뽑아, 시험이 기록을 Core로 다시 읽고 분석한 원시 값으로 만든 기대값과 대조한다
/// (같은 <see cref="ReportBuilder"/>로 다시 만든 HTML과 비교하지 않음).
/// </summary>
public sealed class M8FlowTests
{
    private const string Memo = "회의 중 끊김";
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static JsonElement Sd(JsonElement d) => d.GetProperty("screenDiag");
    private static bool Has(JsonElement d, string name) => Sd(d).ValueKind == JsonValueKind.Object && Sd(d).TryGetProperty(name, out _);

    private static string OutDir(string name)
    {
        var d = Path.Combine(AppDriver.OutputDirectory, "m8-out", name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    private static AppDriver Launch(string userData, params string[] args) => AppDriver.LaunchWith(userData, [.. args, "--freeze-motion"]);

    internal static JsonElement WaitReady(AppDriver app, bool? masked = null) =>
        app.WaitDiag(d => d, d => Has(d, "previewReady") && Sd(d).GetProperty("previewReady").GetBoolean() && Sd(d).GetProperty("state").GetString() == "Ready"
            && Sd(d).GetProperty("exportEnabled").GetBoolean() && Sd(d).GetProperty("published").ValueKind == JsonValueKind.Object
            && (masked is null || Sd(d).GetProperty("published").GetProperty("masked").GetBoolean() == masked), "리포트 미리 보기");

    internal static JsonElement WaitExport(AppDriver app) =>
        app.WaitDiag(d => d, d => Has(d, "export") && Sd(d).GetProperty("export").ValueKind == JsonValueKind.Object && !Sd(d).GetProperty("exporting").GetBoolean(), "내보내기 끝");

    private static (MeasurementRecord Record, AnalysisResult Analysis) Reanalyze(string userData, string id)
    {
        var record = RecordReader.Read(RecordReader.FindSegments(id, M5Fixtures.RecordRoots(userData)));
        return (record, MeasurementAnalysis.Analyze(record));
    }

    private static string[] Values(IReadOnlyList<(string Field, string Value)> fields, string name) => fields.Where(f => f.Field == name).Select(f => f.Value).ToArray();

    /// <summary>어디에도 없어야 하는 값: 대상 주소, 측정 ID 꼬리.</summary>
    private static IEnumerable<string> Strong(MeasurementRecord r) =>
        r.Header!.Targets.Select(t => t.Address.ToString()).Append(r.Header.Id[(r.Header.Id.LastIndexOf('_') + 1)..]);

    /// <summary>
    /// 기록의 글 필드가 들어가는 칸 (대상 이름·사업자): 가린 문서에서는 원본 이름이 없어야 한다.
    /// 이름("공유기")이 고정 문구의 낱말과 같을 수 있어 문서 전체가 아니라 이 칸들만 본다 (설계 3.3 안전망과 같은 구분).
    /// </summary>
    private static void NoNamesInRecordFields(MeasurementRecord r, IReadOnlyList<(string Field, string Value)> fields)
    {
        string[] named = ["target.name", "route.target", "condition.target.name", "condition.target.provider", "condition.id"];
        foreach (var (f, v) in fields.Where(x => named.Contains(x.Field)))
            foreach (var t in r.Header!.Targets)
            {
                Assert.NotEqual(t.Name, v);
                if (t.Provider is { Length: > 0 } p) Assert.NotEqual(p, v);
            }
    }

    /// <summary>미리 보기(종이 면)의 글을 UI 자동화 트리 순서대로.</summary>
    private static List<string> PreviewTexts(AppDriver app)
    {
        var list = new List<string>();
        void Walk(AutomationElement e)
        {
            for (var c = TreeWalker.ControlViewWalker.GetFirstChild(e); c is not null; c = TreeWalker.ControlViewWalker.GetNextSibling(c))
            {
                if (c.Current.ControlType == ControlType.Text && c.Current.Name.Length > 0) list.Add(c.Current.Name);
                Walk(c);
            }
        }
        Walk(app.Find("report.paper"));
        return list;
    }

    private static string Squash(IEnumerable<string> texts) => string.Concat(string.Concat(texts).Where(c => !char.IsWhiteSpace(c)));

    // ---------------- 1. 끝에서 끝까지 ----------------

    [Fact]
    public void AFinishedMeasurementBecomesAReportWhoseValuesMatchTheRecord()
    {
        var userData = AppDriver.NewUserData();
        var outDir = OutDir("flow");
        try
        {
            string id;
            M5Fixtures.Prepare(userData, "profile-outage");
            using (var app = Launch(userData, [.. M5Fixtures.Args("--demo outage-long --demo-speed 600 --start")]))
            {
                var finished = app.WaitDiag(d => d.GetProperty("finished"), f => f.ValueKind == JsonValueKind.Object, "측정 끝");
                id = M5Fixtures.Str(finished, "Id");
                Assert.Equal(0, app.Close());
            }
            M7Fixtures.AddMark(userData, id, 95_000, Memo);
            SaveDiagnosisDuring(userData, id);
            using (var app = Launch(userData, "--demo", "outage-long", "--screen", "records", "--report-out", outDir))
            {
                app.WaitDiag(d => Has(d, "rows") && Sd(d).GetProperty("analyzing").GetInt32() == 0 && Sd(d).GetProperty("rows").GetArrayLength() > 0, b => b, "기록 목록");
                var row = Sd(app.Diag()).GetProperty("rows").EnumerateArray().First(r => r.GetProperty("Id").GetString() == id).GetProperty("n").GetInt32();
                app.Invoke($"records.row.{row}");
                app.WaitDiag(d => Has(d, "detailComplete") && Sd(d).GetProperty("detailComplete").GetBoolean(), b => b, "기록 상세");
                app.Invoke("records.report");
                app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "report", "리포트 화면");
                var ready = WaitReady(app);
                Assert.Equal(id, Sd(ready).GetProperty("id").GetString());
                // 가린 리포트에는 메모를 넣지 않는다: 메모 넣기 스위치가 막혀 있다 (G4 M8 1~6라운드 뒤 사용자 결정)
                Assert.False(app.Find("report.memos").Current.IsEnabled, "가린 문서의 메모 넣기가 막혀 있지 않음");
                app.Invoke("report.export");
                var done = WaitExport(app);
                Assert.True(Sd(done).GetProperty("export").GetProperty("Ok").GetBoolean());
                app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "report.export", "초점 내보내기");

                var file = Assert.Single(Directory.GetFiles(outDir));
                var html = File.ReadAllText(file);
                Assert.Empty(ReportHtml.Verify(html));
                // 미리 보기 글 순서 = 파일 글 순서 (공백을 빼고 이어 붙여 비교: 조각 경계의 공백만 다를 수 있음)
                Assert.Equal(Squash(ReportHtml.BodyTexts(html)), Squash(PreviewTexts(app)));
                // 표는 행 이름으로 읽힌다 (설계 4.3): 첫 끊김 줄의 이름에 열 이름과 값이 함께
                var rowNames = app.Window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "report.table.row"))
                    .Cast<AutomationElement>().Select(e => e.Current.Name).ToArray();
                Assert.Contains(rowNames, n => n.StartsWith("번호 1, 시작 ", StringComparison.Ordinal) && n.Contains(", 판정 ", StringComparison.Ordinal));
                Assert.Equal(0, app.Close());

                var (record, a) = Reanalyze(userData, id);
                var h = record.Header!;
                var fields = ReportHtml.FieldValues(html);
                string Clock(DateTimeOffset w) => w.ToOffset(TimeSpan.FromMinutes(h.UtcOffsetMinutes)).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                // 요약
                Assert.Equal([Sentences.GradeName(a.Grade.Grade)], Values(fields, "summary.grade"));
                Assert.Equal([a.Grade.Sentence], Values(fields, "summary.sentence"));
                Assert.Equal([$"{a.Episodes.Count}번"], Values(fields, "summary.episodes"));
                Assert.Equal([$"판단할 수 있는 관측 {a.Coverage.JudgeableRounds}회차 / 관측해야 했던 {a.Coverage.ExpectedRounds}회차 ({LiveSummary.Percent((double)a.Coverage.JudgeableRounds / a.Coverage.ExpectedRounds)})"],
                    Values(fields, "summary.coverage"));
                Assert.Equal([RecordView.EndText(RecordLifecycle.Ended, record.Summary!.EndReason)], Values(fields, "summary.endReason"));
                // 끊김 목록
                var walls = record.Rounds.ToDictionary(r => r.Round, r => r.Wall);
                Assert.Equal(a.Episodes.Select(e => Clock(walls[e.FirstRound])), Values(fields, "episode.start"));
                Assert.Equal(a.Episodes.Select(e => e.Parts.Count > 1 ? $"여러 판정 (하위 구간 {e.Parts.Count}개)" : RecordView.VerdictName(e.Parts[0].Verdict)), Values(fields, "episode.verdict"));
                // 관측 근거: 대상별 수
                var targets = h.Targets.Where(t => t.Enabled).ToArray();
                var stats = targets.Select(t => record.Stats!.Targets.Single(s => s.TargetId == t.Id)).ToArray();
                Assert.Equal(stats.Select(s => $"{s.Success + s.Lost}번"), Values(fields, "target.valid"));
                Assert.Equal(stats.Select(s => $"{s.Lost}번"), Values(fields, "target.lost"));
                Assert.Equal(stats.Select(s => s.LossRate is double r ? LiveSummary.Percent(r) : "값 없음"), Values(fields, "target.lossRate"));
                Assert.Equal(stats.Select(s => $"{s.MaxConsecutiveLoss}번"), Values(fields, "target.consecutive"));
                Assert.Equal(a.Links.Summaries.Count, Values(fields, "link.state").Length);
                // 측정 조건
                Assert.Equal(targets.Select(t => t.Role == NetPulse.Core.Targets.TargetRole.Local ? "로컬" : "외부"), Values(fields, "condition.target.role"));
                Assert.Equal([$"{h.IntervalMs / 1000.0:0.#}초"], Values(fields, "condition.interval"));
                Assert.Equal([$"{h.TimeoutMs / 1000.0:0.#}초"], Values(fields, "condition.timeout"));
                // 증상 표시: 시각만 (가린 리포트에는 메모 없음, 안내 문구)
                var marks = SymptomMarks.FromEvents(record.Events, record.NoteLines).Marks.OrderBy(m => m.AtMs).ToArray();
                Assert.Equal(marks.Select(m => Clock(m.Wall)), Values(fields, "mark.time"));
                Assert.Empty(Values(fields, "mark.memo"));
                Assert.Contains(Squash([ReportTexts.MaskedNoMemos]), Squash(ReportHtml.BodyTexts(html)));
                Assert.DoesNotContain(Markup.Text(Memo), html);
                // 요약 (설계 7.2-1): 시작·끝 시각, 최장 끊김 — 시험이 기록 값에서 직접 만든 글
                string Full(DateTimeOffset w) => w.ToOffset(TimeSpan.FromMinutes(h.UtcOffsetMinutes)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " (UTC" + (h.UtcOffsetMinutes < 0 ? "-" : "+") + TimeSpan.FromMinutes(Math.Abs(h.UtcOffsetMinutes)).ToString(@"hh\:mm", CultureInfo.InvariantCulture) + ")";
                Assert.Equal([Full(h.StartedAt)], Values(fields, "summary.start"));
                Assert.Equal([Full(record.Summary.EndedAt)], Values(fields, "summary.end"));
                var longest = a.Episodes.OrderByDescending(e => e.MinDurationMs).ThenBy(e => e.FirstRound).First();
                Assert.Equal([RecordView.Duration(longest)], Values(fields, "summary.longest"));
                // 끊김: 길이 범위·판정·후보 (하위 구간이 하나인 끊김)
                Assert.Equal(a.Episodes.Select(RecordView.Duration), Values(fields, "episode.duration"));
                Assert.Equal(a.Episodes.Select(e => e.Parts.Count > 1 ? "하위 구간별로 아래 줄에 적음" : RecordView.CandidateText(e.Parts[0].Candidate)), Values(fields, "episode.candidate"));
                // 대상별 평균·최대 지연
                Assert.Equal(stats.Select(s => s.AvgRttMs is double v ? PathMapModel.Ms(v) : "값 없음"), Values(fields, "target.avg"));
                Assert.Equal(stats.Select(s => s.MaxRttMs is double v ? PathMapModel.Ms(v) : "값 없음"), Values(fields, "target.max"));
                // 링크 근거: 네 상태 이름 (기획서 6.2 R2)
                string LinkName(NetPulse.Core.Evidence.LinkDisplay x) => x switch
                {
                    NetPulse.Core.Evidence.LinkDisplay.Down => "끊김 관측", NetPulse.Core.Evidence.LinkDisplay.Denied => "접근 거부",
                    NetPulse.Core.Evidence.LinkDisplay.ObservedNone => "관측했지만 끊김 기록 없음", _ => "이 어댑터는 기록을 지원하지 않음",
                };
                Assert.Equal(a.Links.Summaries.Select(x => LinkName(x.Display)), Values(fields, "link.state"));
                // 경로 유형: 대상마다 첫 경로 이벤트의 유형
                string RouteName(string? kind, bool ok) => !ok ? "경로 없음" : kind switch { "wired" => "유선", "wireless" => "Wi‑Fi", null => "알 수 없음", _ => "기타" };
                var firstRoutes = targets.Select(t => record.Events.FirstOrDefault(e => (string?)e["type"] == "route" && (string?)e["target"] == t.Id)).ToArray();
                Assert.Equal(firstRoutes.Select(e => e is null ? "기록 없음" : RouteName((string?)e["kind"], (string?)e["status"] == "ok")), Values(fields, "route.start"));
                // 측정 조건: 방식·포트·예상 연결·기록 상태
                Assert.Equal(targets.Select(t => t.Method.Kind == NetPulse.Core.Targets.ProbeMethodKind.Icmp ? "Ping" : "TCP"), Values(fields, "condition.target.method"));
                Assert.Equal(targets.Select(t => t.Method.Kind == NetPulse.Core.Targets.ProbeMethodKind.Tcp ? t.Method.Port.ToString(CultureInfo.InvariantCulture) : "없음"), Values(fields, "condition.target.port"));
                Assert.Equal([h.Expected switch { NetPulse.Core.Targets.ExpectedConnection.Wired => "유선", NetPulse.Core.Targets.ExpectedConnection.Wireless => "Wi‑Fi", NetPulse.Core.Targets.ExpectedConnection.Any => "상관없음", _ => "기록 없음" }],
                    Values(fields, "condition.expected"));
                Assert.Equal([record.Damage.Total == 0 && record.Damage.DamagedEvents == 0 && record.MissingSegments.Count == 0 && record.MissingNotice is null ? "손상 없음" : "알림"],
                    Values(fields, "condition.damage").Select(v => v.StartsWith("알림", StringComparison.Ordinal) ? "알림" : v));
                // 저장 진단: 결론 머리말과 검사 일곱의 이름·판정 (저장된 기록을 다시 읽어)
                var stored = new NetPulse.Core.Live.DiagnosisStore(Path.Combine(userData, "diagnoses")).List().Items.Select(x => x.Stored).Single(x => x.MeasurementId == id);
                Assert.Equal([NetPulse.Core.Diagnostics.DiagnosisTexts.Head(stored.MainConclusion).Head], Values(fields, "diagnosis.head"));
                Assert.Equal(stored.Checks.Select(c => NetPulse.Core.Diagnostics.QuickDiagnosis.CheckName(c.Id)), Values(fields, "diagnosis.check.name"));
                Assert.Equal(stored.Checks.Select(c => NetPulse.Core.Diagnostics.QuickDiagnosis.JudgmentName(c.Judgment) + (c.PartlyUnconfirmed ? " (일부 미확인)" : "")), Values(fields, "diagnosis.check.judgment"));
                // 가린 리포트: 원본 식별 값 없음
                foreach (var v in Strong(record)) Assert.False(html.Contains(v, StringComparison.OrdinalIgnoreCase), "남은 값: " + v);
                NoNamesInRecordFields(record, fields);
                Record("flow-report", new { id, episodes = a.Episodes.Count, fields = fields.Count, bytes = new FileInfo(file).Length });
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>측정 중에 한 진단 기록 하나 (데모 진단을 돌려 그 측정 ID로 저장, 시각은 측정 안).</summary>
    private static void SaveDiagnosisDuring(string userData, string measurementId)
    {
        var scenario = M5Fixtures.Load("diag-outside");
        var at = M5Fixtures.DemoNow.AddMinutes(-1);
        var tb = new NetPulse.Core.Demo.DemoTimeBase(at, 1);
        tb.Freeze(TimeSpan.FromSeconds(10));
        var world = new NetPulse.Core.Demo.DemoWorld(scenario, tb);
        var settings = NetPulse.Core.Demo.DemoDiagnosticProbes.Settings(scenario);
        var result = NetPulse.Core.Diagnostics.QuickDiagnosis.RunAsync(new NetPulse.Core.Demo.DemoDiagnosticProbes(world), settings, () => at).GetAwaiter().GetResult();
        var save = new NetPulse.Core.Live.DiagnosisStore(Path.Combine(userData, "diagnoses")).Save(result, "test", measurementId);
        Assert.Null(save.Error);
    }

    // ---------------- 닫기 (G4 M8 1라운드 b1) ----------------

    [Fact]
    public void ClosingDuringAnExportLeavesNoTempFile()
    {
        var userData = AppDriver.NewUserData();
        var outDir = OutDir("close");
        try
        {
            M5Fixtures.SaveProfile(userData, "outage-long");
            M5Fixtures.MakeRecord(userData, "outage-long", M5Fixtures.DemoNow.AddHours(-2), 3);
            using var app = Launch(userData, "--screen", "report", "--report-out", outDir, "--report-fail", "slow");
            WaitReady(app);
            app.Invoke("report.export");
            app.WaitDiag(d => Sd(d).GetProperty("exporting").GetBoolean(), b => b, "내보내는 중");
            Assert.Equal(0, app.Close());
            Assert.Empty(Directory.GetFiles(outDir));   // 임시 파일도 결과 파일도 없음 (쓰기 전에 취소)
            Assert.Contains(app.ReadLog(), e => e.GetProperty("kind").GetString() == "reportExportCancelled");
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>임시 파일에 절반을 쓴 채 멈춘 내보내기 중에 닫기: 닫기가 취소를 보내고 정리가 끝난 뒤에 끝난다 (G4 M8 2라운드 b1).</summary>
    [Fact]
    public void ClosingWhileTheTempFileIsBeingWrittenRemovesIt()
    {
        var userData = AppDriver.NewUserData();
        var outDir = OutDir("close-hold");
        try
        {
            M5Fixtures.SaveProfile(userData, "outage-long");
            M5Fixtures.MakeRecord(userData, "outage-long", M5Fixtures.DemoNow.AddHours(-2), 3);
            using var app = Launch(userData, "--screen", "report", "--report-out", outDir, "--report-fail", "hold");
            WaitReady(app);
            app.Invoke("report.export");
            HoldingWithHalfWritten(app, outDir);
            Assert.Equal(0, app.Close());
            Assert.Empty(Directory.GetFiles(outDir));   // 임시 파일 정리가 끝난 뒤 프로세스가 끝남, 결과 파일도 없음
            Assert.Contains(app.ReadLog(), e => e.GetProperty("kind").GetString() == "reportExportCancelled");
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>앱이 임시 파일에 절반을 쓰고 멈춘 곳에 닿았음(진단 신호)과 그 임시 파일에 내용이 있음을 확인 (G4 M8 3라운드 b8).</summary>
    private static void HoldingWithHalfWritten(AppDriver app, string outDir)
    {
        app.WaitDiag(d => Has(d, "exportHolding") && Sd(d).GetProperty("exportHolding").GetBoolean(), b => b, "임시 파일에 절반 쓰고 멈춤");
        var temp = Assert.Single(Directory.GetFiles(outDir, ReportFile.TempPrefix + "*"));
        Assert.True(new FileInfo(temp).Length > 0, "임시 파일이 비어 있음");
    }

    /// <summary>
    /// 쓰기가 닫기의 공통 한도(9초)보다 오래 멈춰도 닫기는 정리가 끝날 때까지 기다린다: 프로세스가 한도 뒤까지 살아 있고, 끝난 뒤 폴더가 비어 있다
    /// (G4 M8 2·3라운드 b1·b8).
    /// </summary>
    [Fact]
    public void ClosingWaitsPastTheCommonLimitUntilTheTempFileIsGone()
    {
        var userData = AppDriver.NewUserData();
        var outDir = OutDir("close-hold-long");
        try
        {
            M5Fixtures.SaveProfile(userData, "outage-long");
            M5Fixtures.MakeRecord(userData, "outage-long", M5Fixtures.DemoNow.AddHours(-2), 3);
            using var app = Launch(userData, "--screen", "report", "--report-out", outDir, "--report-fail", "hold-long");
            WaitReady(app);
            app.Invoke("report.export");
            HoldingWithHalfWritten(app, outDir);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(0, app.Close());
            watch.Stop();
            Assert.True(watch.Elapsed > NetPulse.Design.Tokens.Waits.CloseWrites * 3, "닫기가 공통 한도 전에 끝남: " + watch.Elapsed);
            Assert.Empty(Directory.GetFiles(outDir));
            var log = app.ReadLog().Select(e => e.GetProperty("kind").GetString()).ToArray();
            Assert.Contains("reportCloseWait", log);
            Assert.Contains("reportExportCancelled", log);
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 혼합 끊김·링크 보조 근거·손상 (G4 M8 2라운드 b2) ----------------

    /// <summary>
    /// 혼합 끊김(외부 둘 3회차 → 외부 하나 2회차), 링크 근거(직접: 관측 범위 50%·알림 넘침·끊김 하나, 로그: 접근 거부), 손상(망가진 회차 줄·누락 조각)이
    /// 실제로 있는 기록을 가려서 내보내, 하위 구간·링크 보조 사유·손상 알림을 기록에서 만든 기대값과 대조한다.
    /// </summary>
    [Fact]
    public void AMixedDamagedRecordWithLinkNotesMatchesTheRecord()
    {
        var userData = AppDriver.NewUserData();
        var outDir = OutDir("mixed");
        const string Nic = "np-test-nic";
        try
        {
            var id = M7Fixtures.Synthetic(userData, M5Fixtures.DemoNow.AddHours(-1), mixed: true);
            var folder = M7Fixtures.Folder(userData, id);
            var start = RecordReader.Read(RecordReader.FindSegments(id, M5Fixtures.RecordRoots(userData))).Header!.StartedAt;
            System.Text.Json.Nodes.JsonObject Nic0(string collection, long? eventMs) => new()
            {
                ["collection"] = collection, ["adapter"] = Nic, ["interfaceIndex"] = 7, ["kind"] = "wired", ["virtual"] = false, ["eventMs"] = eventMs,
            };
            var link = new List<string>
            {
                RecordSchema.EventLine(id, "link", 0, 0, start, Nic0("up", 0)),
                // 링크 끊김은 끊김(400~404회차)과 떨어진 곳에 둔다 (하위 구간 판정이 바뀌지 않게)
                RecordSchema.EventLine(id, "link", 800, 800_000, start.AddSeconds(800), Nic0("down", 800_000)),
                RecordSchema.EventLine(id, "link", 803, 803_000, start.AddSeconds(803), Nic0("up", 803_000)),
                RecordSchema.EventLine(id, "link", 900, 900_000, start.AddSeconds(900), new() { ["collection"] = "overflow", ["spans"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonArray(900_000, 905_000)) }),
                RecordSchema.EventLine(id, "link", 1199, 1_199_000, start.AddSeconds(1199), new() { ["collection"] = "summary", ["adapter"] = Nic, ["capability"] = "supported", ["coverage"] = 0.5 }),
                RecordSchema.EventLine(id, "link", 1199, 1_199_000, start.AddSeconds(1199), new() { ["source"] = "log", ["collection"] = "summary", ["adapter"] = Nic, ["capability"] = "denied", ["code"] = "log-denied" }),
            };
            File.AppendAllLines(Path.Combine(folder, "events.jsonl"), link);
            M7Fixtures.CorruptRounds(userData, id);
            M7Fixtures.AddDetachedSegment(userData, id);

            using (var app = Launch(userData, "--screen", "report", "--report-out", outDir))
            {
                var ready = WaitReady(app, masked: true);
                Assert.Equal(id, Sd(ready).GetProperty("id").GetString());
                app.Invoke("report.export");
                Assert.True(Sd(WaitExport(app)).GetProperty("export").GetProperty("Ok").GetBoolean());
                var html = File.ReadAllText(Assert.Single(Directory.GetFiles(outDir)));
                Assert.Empty(ReportHtml.Verify(html));
                Assert.Equal(Squash(ReportHtml.BodyTexts(html)), Squash(PreviewTexts(app)));
                Assert.Equal(0, app.Close());

                var (record, a) = Reanalyze(userData, id);
                var h = record.Header!;
                var fields = ReportHtml.FieldValues(html);
                string Clock(DateTimeOffset w) => w.ToOffset(TimeSpan.FromMinutes(h.UtcOffsetMinutes)).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                var walls = record.Rounds.ToDictionary(r => r.Round, r => r.Wall);

                // 혼합 끊김: 기록의 모양을 시험이 안다 — 400회차부터 3회차, 403회차부터 2회차, 두 판정이 다름
                var mixed = Assert.Single(a.Episodes, e => e.Parts.Count > 1);
                Assert.Equal([400L, 403L], mixed.Parts.Select(p => p.FirstRound));
                Assert.Equal([3, 2], mixed.Parts.Select(p => p.Count));
                Assert.NotEqual(mixed.Parts[0].Verdict, mixed.Parts[1].Verdict);
                var parts = a.Episodes.Where(e => e.Parts.Count > 1).SelectMany(e => e.Parts).ToArray();
                Assert.Equal(parts.Select(p => Clock(walls[p.FirstRound])), Values(fields, "part.start"));
                Assert.Equal([Clock(start.AddSeconds(400)), Clock(start.AddSeconds(403))], Values(fields, "part.start"));
                Assert.Equal(parts.Select(RecordView.Duration), Values(fields, "part.duration"));
                Assert.Equal(parts.Select(p => RecordView.VerdictName(p.Verdict)), Values(fields, "part.verdict"));
                Assert.Equal(parts.Select(p => RecordView.CandidateText(p.Candidate)), Values(fields, "part.candidate"));
                Assert.Contains($"여러 판정 (하위 구간 2개)", Values(fields, "episode.verdict"));

                // 링크 근거: 직접(끊김 관측, 관측 범위·일부·넘침), 로그(접근 거부, 사유는 고정 문구로만)
                Assert.Equal(["끊김 관측", "접근 거부"], Values(fields, "link.state"));
                var notes = Values(fields, "link.notes");
                Assert.Equal(2, notes.Length);
                Assert.Equal($"관측 범위 {LiveSummary.Percent(0.5)} · 일부만 관측 · 알림이 넘쳐 일부 빠짐", notes[0]);
                Assert.Equal("링크 기록을 읽을 권한이 없습니다", notes[1]);   // 사유 코드 log-denied의 고정 문구 (원문 코드 없음)
                Assert.DoesNotContain(Nic, html);   // 어댑터 이름은 가림

                // 손상: 망가진 회차 줄과 누락 조각 → 알림 개수와 각 알림 글이 파일·미리 보기에 있다
                var notices = RecordView.DamageNotices(record, SymptomMarks.FromEvents(record.Events, record.NoteLines).Ignored);
                Assert.True(notices.Count >= 2, "손상 알림 " + notices.Count);
                Assert.True(record.Damage.Total >= 2);
                Assert.NotEmpty(record.MissingSegments);
                Assert.Equal([$"기록 문제 {notices.Count}건"], Values(fields, "condition.damage"));
                var body = Squash(ReportHtml.BodyTexts(html));
                foreach (var n in notices) Assert.Contains(Squash([n]), body);
                foreach (var v in Strong(record)) Assert.False(html.Contains(v, StringComparison.OrdinalIgnoreCase), "남은 값: " + v);
                Record("flow-report-mixed", new { id, parts = parts.Length, links = notes.Length, notices = notices.Count });
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 2. 가리기 켬/끔, 3. 옵션 ----------------

    [Fact]
    public void MaskedAndUnmaskedExportsDifferOnlyInTheRulesAndOptionsDropSections()
    {
        var userData = AppDriver.NewUserData();
        var outDir = OutDir("mask");
        try
        {
            M5Fixtures.SaveProfile(userData, "outage-long");
            var id = M5Fixtures.MakeRecord(userData, "outage-long", M5Fixtures.DemoNow.AddHours(-2), 3);
            M7Fixtures.AddMark(userData, id, 95_000, Memo);
            var (record, _) = Reanalyze(userData, id);
            using var app = Launch(userData, "--screen", "report", "--report-out", outDir);
            WaitReady(app, masked: true);
            app.Invoke("report.export");
            WaitExport(app);
            var masked = File.ReadAllText(Assert.Single(Directory.GetFiles(outDir)));

            // 가리지 않음: 메모 넣기를 켤 수 있고, 내보내기 전에 확인을 묻는다
            app.Select("report.mask.unmasked");
            WaitReady(app, masked: false);
            app.Toggle("report.memos");
            app.WaitDiag(d => Sd(d).GetProperty("memos").GetBoolean(), b => b, "메모 넣기");
            WaitReady(app, masked: false);
            app.Invoke("report.export");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), b => b, "확인 대화상자");
            app.Invoke("dialog.confirm");
            app.WaitDiag(d => Sd(d).GetProperty("export") is { ValueKind: JsonValueKind.Object } e && !e.GetProperty("Masked").GetBoolean(), b => b, "가리지 않은 내보내기");
            var unmasked = File.ReadAllText(Assert.Single(Directory.GetFiles(outDir)));

            var h = record.Header!;
            foreach (var t in h.Targets)
            {
                Assert.DoesNotContain(t.Address.ToString(), masked);
                Assert.Contains(t.Address.ToString(), unmasked);
                Assert.Contains(Markup.Text(t.Name), unmasked);
                foreach (var html in new[] { masked, unmasked })
                    Assert.DoesNotContain(ReportHtml.BodyTexts(html), x => x == t.Id);   // 대상 ID는 어느 쪽에도 없음
            }
            NoNamesInRecordFields(record, ReportHtml.FieldValues(masked));
            Assert.DoesNotContain(h.Id, masked);
            Assert.Contains(h.Id, unmasked);
            Assert.DoesNotContain(Memo, masked);   // 가린 문서에는 메모 없음
            Assert.Equal([Memo], ReportHtml.FieldValues(unmasked).Where(f => f.Field == "mark.memo").Select(f => f.Value));   // 가리지 않은 문서에는 적은 그대로

            // 옵션: 진단 뺌·끊김 상세 0개 → 그 절이 없다
            app.Toggle("report.diagnoses");
            app.Select("report.details.0");
            app.Select("report.mask.masked");
            var d = app.WaitDiag(x => x, x => Has(x, "published") && Sd(x).GetProperty("published").ValueKind == JsonValueKind.Object
                && Sd(x).GetProperty("state").GetString() == "Ready" && !Sd(x).GetProperty("diagnoses").GetBoolean() && Sd(x).GetProperty("details").GetInt32() == 0
                && Sd(x).GetProperty("published").GetProperty("masked").GetBoolean() && !Sd(x).GetProperty("memos").GetBoolean(), "옵션 반영 (가리면 메모 넣기도 꺼짐)");
            // 다시 가리면 메모 넣기 스위치는 UIA에서도 막히고 꺼져 있다 (M8 이월 3)
            var memoSwitch = app.Find("report.memos");
            Assert.False(memoSwitch.Current.IsEnabled, "다시 가린 뒤 메모 넣기가 막혀 있지 않음");
            Assert.Equal(ToggleState.Off, ((TogglePattern)memoSwitch.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState);
            var sections = Sd(d).GetProperty("published").GetProperty("sections").EnumerateArray().Select(s => s.GetString()).ToArray();
            Assert.DoesNotContain("diagnoses", sections);
            Assert.DoesNotContain("details", sections);
            Assert.Equal(0, app.Close());
            Record("flow-report-mask", new { maskedBytes = masked.Length, unmaskedBytes = unmasked.Length, sections });
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 5. 실패 ----------------

    [Theory]
    [InlineData("write")]
    [InlineData("move")]
    public void AFailedWriteLeavesNoTempFileAndKeepsTheExistingFile(string fault)
    {
        var userData = AppDriver.NewUserData();
        var outDir = OutDir("fail-" + fault);
        try
        {
            M5Fixtures.SaveProfile(userData, "outage-long");
            M5Fixtures.MakeRecord(userData, "outage-long", M5Fixtures.DemoNow.AddHours(-2), 3);
            using var app = Launch(userData, "--screen", "report", "--report-out", outDir, "--report-fail", fault);
            var ready = WaitReady(app);
            var existing = Path.Combine(outDir, Sd(ready).GetProperty("published").GetProperty("fileName").GetString()!);
            File.WriteAllText(existing, "원래 파일");
            app.Invoke("report.export");
            var done = WaitExport(app);
            Assert.False(Sd(done).GetProperty("export").GetProperty("Ok").GetBoolean());
            Assert.Equal("원래 파일", File.ReadAllText(existing));
            Assert.Equal([existing], Directory.GetFiles(outDir));
            Assert.True(app.Exists("report.export.failed"));
            Assert.True(Sd(done).GetProperty("previewReady").GetBoolean());   // 미리 보기는 그대로
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void AnUnwritableFolderFailsWithoutAFile()
    {
        var userData = AppDriver.NewUserData();
        var missing = Path.Combine(AppDriver.OutputDirectory, "m8-out", "no-such-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            M5Fixtures.SaveProfile(userData, "outage-long");
            M5Fixtures.MakeRecord(userData, "outage-long", M5Fixtures.DemoNow.AddHours(-2), 3);
            using var app = Launch(userData, "--screen", "report", "--report-out", missing);
            WaitReady(app);
            app.Invoke("report.export");
            var done = WaitExport(app);
            Assert.False(Sd(done).GetProperty("export").GetProperty("Ok").GetBoolean());
            Assert.False(Directory.Exists(missing));
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void ALeakStopsTheExportAndSaysWhichKind()
    {
        var userData = AppDriver.NewUserData();
        var outDir = OutDir("leak");
        try
        {
            M5Fixtures.SaveProfile(userData, "outage-long");
            M5Fixtures.MakeRecord(userData, "outage-long", M5Fixtures.DemoNow.AddHours(-2), 3);
            using var app = Launch(userData, "--screen", "report", "--report-out", outDir, "--report-fail", "leak");
            WaitReady(app);
            app.Invoke("report.export");
            var d = app.WaitDiag(x => x, x => Has(x, "leak") && Sd(x).GetProperty("leak").GetArrayLength() > 0, "안전망");
            Assert.Equal(["대상 주소"], Sd(d).GetProperty("leak").EnumerateArray().Select(k => k.GetString()));
            Assert.False(Sd(d).GetProperty("exportEnabled").GetBoolean());
            Assert.True(app.Exists("report.redaction.retry"));
            Assert.Empty(Directory.GetFiles(outDir));
            Assert.Contains(app.ReadLog(), e => e.GetProperty("kind").GetString() == "reportLeak");
            Assert.DoesNotContain(app.ReadLog(), e => e.GetRawText().Contains(outDir.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 6. 스냅숏 ----------------

    [Fact]
    public void SwitchingToMaskedNeverLetsTheUnmaskedDocumentBeExported()
    {
        var userData = AppDriver.NewUserData();
        var outDir = OutDir("snapshot");
        try
        {
            M5Fixtures.SaveProfile(userData, "outage-long");
            var id = M5Fixtures.MakeRecord(userData, "outage-long", M5Fixtures.DemoNow.AddHours(-2), 3);
            var (record, _) = Reanalyze(userData, id);
            using var app = Launch(userData, "--screen", "report", "--report-out", outDir);
            WaitReady(app);
            app.Select("report.mask.unmasked");
            WaitReady(app, masked: false);
            app.Select("report.mask.masked");
            // 바꾼 바로 뒤 (진단 값은 한 차례 늦게 쓰이므로 새 옵션이 보인 첫 값): 내보낼 수 있으면 게시 문서는 이미 가린 문서여야 한다
            var now = app.WaitDiag(d => d, d => Sd(d).GetProperty("masked").GetBoolean(), "가리기 켬 반영");
            Assert.False(Sd(now).GetProperty("exportEnabled").GetBoolean() && !Sd(now).GetProperty("published").GetProperty("masked").GetBoolean());
            WaitReady(app, masked: true);
            app.Invoke("report.export");
            WaitExport(app);
            var html = File.ReadAllText(Assert.Single(Directory.GetFiles(outDir)));
            foreach (var t in record.Header!.Targets) Assert.DoesNotContain(t.Address.ToString(), html);
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 빈 상태·들어오는 길 ----------------

    [Fact]
    public void WithoutAFinishedMeasurementTheScreenSaysSoAndLeadsToMeasuring()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            M5Fixtures.SaveProfile(userData, "steady");
            using var app = Launch(userData, "--screen", "report");
            app.WaitDiag(d => Has(d, "candidates") && Sd(d).GetProperty("listed").GetBoolean(), b => b, "목록");
            Assert.Equal(0, Sd(app.Diag()).GetProperty("candidates").GetInt32());
            Assert.False(Sd(app.Diag()).GetProperty("exportEnabled").GetBoolean());
            app.Invoke("report.empty.measure");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "measure", "측정 화면");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
