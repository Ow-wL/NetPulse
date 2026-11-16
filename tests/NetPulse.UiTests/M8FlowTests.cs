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

    private static JsonElement WaitReady(AppDriver app, bool? masked = null) =>
        app.WaitDiag(d => d, d => Has(d, "previewReady") && Sd(d).GetProperty("previewReady").GetBoolean() && Sd(d).GetProperty("state").GetString() == "Ready"
            && Sd(d).GetProperty("exportEnabled").GetBoolean() && Sd(d).GetProperty("published").ValueKind == JsonValueKind.Object
            && (masked is null || Sd(d).GetProperty("published").GetProperty("masked").GetBoolean() == masked), "리포트 미리 보기");

    private static JsonElement WaitExport(AppDriver app) =>
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
                app.Toggle("report.memos");
                WaitReady(app);
                app.WaitDiag(d => Sd(d).GetProperty("memos").GetBoolean(), b => b, "메모 넣기");
                WaitReady(app);
                app.Invoke("report.export");
                var done = WaitExport(app);
                Assert.True(Sd(done).GetProperty("export").GetProperty("Ok").GetBoolean());
                app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "report.export", "초점 내보내기");

                var file = Assert.Single(Directory.GetFiles(outDir));
                var html = File.ReadAllText(file);
                Assert.Empty(ReportHtml.Verify(html));
                // 미리 보기 글 순서 = 파일 글 순서 (공백을 빼고 이어 붙여 비교: 조각 경계의 공백만 다를 수 있음)
                Assert.Equal(Squash(ReportHtml.BodyTexts(html)), Squash(PreviewTexts(app)));
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
                // 메모 (넣기 켬, 식별 값 없는 글이라 그대로)
                var marks = SymptomMarks.FromEvents(record.Events, record.NoteLines).Marks.OrderBy(m => m.AtMs).ToArray();
                Assert.Equal(marks.Select(m => Clock(m.Wall)), Values(fields, "mark.time"));
                Assert.Equal(marks.Select(m => m.Memo ?? "메모 없음"), Values(fields, "mark.memo"));
                // 가린 리포트: 원본 식별 값 없음
                foreach (var v in Strong(record)) Assert.False(html.Contains(v, StringComparison.OrdinalIgnoreCase), "남은 값: " + v);
                NoNamesInRecordFields(record, fields);
                Record("flow-report", new { id, episodes = a.Episodes.Count, fields = fields.Count, bytes = new FileInfo(file).Length });
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

            // 가리지 않음: 내보내기 전에 확인을 묻는다
            app.Select("report.mask.unmasked");
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
            Assert.DoesNotContain(Memo, masked);   // 메모는 기본으로 뺀다

            // 옵션: 진단 뺌·끊김 상세 0개 → 그 절이 없다
            app.Toggle("report.diagnoses");
            app.Select("report.details.0");
            app.Select("report.mask.masked");
            var d = app.WaitDiag(x => x, x => Has(x, "published") && Sd(x).GetProperty("published").ValueKind == JsonValueKind.Object
                && Sd(x).GetProperty("state").GetString() == "Ready" && !Sd(x).GetProperty("diagnoses").GetBoolean() && Sd(x).GetProperty("details").GetInt32() == 0
                && Sd(x).GetProperty("published").GetProperty("masked").GetBoolean(), "옵션 반영");
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
