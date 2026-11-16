using System.IO;
using System.Text.Json;
using NetPulse.Core.Analysis;
using NetPulse.Core.Records;
using NetPulse.Core.Report;

namespace NetPulse.UiTests;

/// <summary>
/// 실측 리포트 (M8 설계 7.3, 로컬, 공개하지 않음): 데모 없이 실제 측정 30회차 → 중지 → 기록 상세 → 리포트 만들기 → 가린 리포트 내보내기
/// → 파일의 요약 값 = 시험이 그 기록을 다시 분석한 값, 파일에 이 PC의 실제 대상 주소·측정 ID 꼬리가 없고 어댑터 별칭이 어댑터 칸에 없음.
/// 이 PC의 정보가 다루어지므로 출력은 <c>.loop/</c> 아래만, 게이트에서는 특성으로 뺀다:
/// <c>NETPULSE_UITEST_OUT=.loop/shots/m8-live dotnet test --project tests/NetPulse.UiTests -- --filter-trait Kind=Live</c>
/// </summary>
[Trait("Kind", "Live")]
public sealed class M8LiveRunTests
{
    private static JsonElement Sd(JsonElement d) => d.GetProperty("screenDiag");

    [Fact]
    public void RealMeasurementBecomesAMaskedReport()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NetPulse.slnx"))) root = root.Parent;
        Assert.True(root is not null, "저장소 루트를 찾지 못했습니다");
        string loop = Path.GetFullPath(Path.Combine(root!.FullName, ".loop")) + Path.DirectorySeparatorChar;
        string output = Path.GetFullPath(AppDriver.OutputDirectory) + Path.DirectorySeparatorChar;
        Assert.True(output.StartsWith(loop, StringComparison.OrdinalIgnoreCase), "실측 시험의 출력(NETPULSE_UITEST_OUT)은 .loop/ 아래여야 합니다");
        var outDir = Path.Combine(AppDriver.OutputDirectory, "live-report");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);

        var userData = AppDriver.NewUserData();
        try
        {
            string id;
            using var app = AppDriver.LaunchWith(userData, "--report-out", outDir);
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "firstrun", "첫 실행 안내");
            string status = app.WaitDiag(d => M5Fixtures.Str(d, "screenDiag", "status"), s => s is "Ok" or "NoNetwork" or "Unknown", "실제 탐지");
            Assert.True(status == "Ok", "이 PC 탐지 결과: " + status);
            app.WaitDiag(d => Sd(d).GetProperty("startEnabled").GetBoolean(), b => b, "시작 켜짐");
            app.Invoke("firstrun.start");
            app.WaitDiag(d => M5Fixtures.Str(d, "host"), s => s == "Measuring", "측정 시작");
            app.WaitDiag(d => d.GetProperty("rounds").GetInt32(), r => r >= 30, "30회차");
            app.Invoke("measure.stop");
            id = M5Fixtures.Str(app.WaitDiag(d => d.GetProperty("finished"), f => f.ValueKind == JsonValueKind.Object, "측정 끝"), "Id");
            app.Invoke("measure.result.records");
            app.WaitDiag(d => Sd(d).ValueKind == JsonValueKind.Object && Sd(d).TryGetProperty("detailComplete", out var c) && c.GetBoolean(), b => b, "기록 상세");
            app.Invoke("records.report");
            app.WaitDiag(d => Sd(d).ValueKind == JsonValueKind.Object && Sd(d).TryGetProperty("previewReady", out var p) && p.GetBoolean()
                && Sd(d).GetProperty("state").GetString() == "Ready" && Sd(d).GetProperty("exportEnabled").GetBoolean(), b => b, "리포트 미리 보기");
            Assert.Equal(id, Sd(app.Diag()).GetProperty("id").GetString());
            app.Invoke("report.export");
            app.WaitDiag(d => Sd(d).TryGetProperty("export", out var e) && e.ValueKind == JsonValueKind.Object && e.GetProperty("Ok").GetBoolean(), b => b, "내보냄");
            app.CaptureClient("live-report");
            Assert.Equal(0, app.Close());

            var html = File.ReadAllText(Assert.Single(Directory.GetFiles(outDir)));
            Assert.Empty(ReportHtml.Verify(html));
            var record = RecordReader.Read(RecordReader.FindSegments(id, [Path.Combine(userData, "records"), DiskRecordRoot.Defaults().Fallback.Path!]));
            var a = MeasurementAnalysis.Analyze(record);
            var fields = ReportHtml.FieldValues(html);
            string One(string name) => Assert.Single(fields, f => f.Field == name).Value;
            Assert.Equal(Sentences.GradeName(a.Grade.Grade), One("summary.grade"));
            Assert.Equal(a.Grade.Sentence, One("summary.sentence"));
            Assert.Equal(a.Episodes.Count == 0 ? ReportTexts.EpisodeCount(0, a.Coverage, a.Grade.Grade) : $"{a.Episodes.Count}번", One("summary.episodes"));
            // 이 PC의 실제 대상 주소와 측정 ID 꼬리는 어디에도 없다. 어댑터 별칭은 어댑터 칸에 없다 (유형 + 순번만)
            foreach (var t in record.Header!.Targets) Assert.DoesNotContain(t.Address.ToString(), html);
            Assert.DoesNotContain(id[(id.LastIndexOf('_') + 1)..], html);
            foreach (var alias in a.Links.Adapters.Keys)
                Assert.DoesNotContain(fields, f => f.Field == "link.adapter" && f.Value.Contains(alias, StringComparison.Ordinal));
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
