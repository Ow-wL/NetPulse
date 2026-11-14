using System.IO;
using System.Text.Json;
using NetPulse.Core.Analysis;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;

namespace NetPulse.UiTests;

/// <summary>
/// 실측 기록 다시 열기 (M7 설계 7.3, 로컬, 공개하지 않음): 데모 없이 첫 실행 → 측정 30회차 → 중지 → 기록 화면에서 그 측정 열기
/// → 화면 값(등급·끝 사유·끊김 수) = 시험이 그 기록 파일을 다시 분석한 값 → 재시작 → 다시 열기 같은 값. 판정 값 자체는 통과 조건이 아니다.
/// 이 PC의 주소가 화면·로그에 나오므로 출력은 <c>.loop/</c> 아래만, 게이트에서는 특성으로 뺀다:
/// <c>NETPULSE_UITEST_OUT=.loop/shots/m7-live dotnet test --project tests/NetPulse.UiTests -- --filter-trait Kind=Live</c>
/// </summary>
[Trait("Kind", "Live")]
public sealed class M7LiveRunTests
{
    private static string Screen(JsonElement d) => M5Fixtures.Str(d, "screen");
    private static JsonElement Sd(JsonElement d) => d.GetProperty("screenDiag");

    [Fact]
    public void RealMeasurementReopensFromRecordsWithTheSameValues()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NetPulse.slnx"))) root = root.Parent;
        Assert.True(root is not null, "저장소 루트를 찾지 못했습니다");
        string loop = Path.GetFullPath(Path.Combine(root!.FullName, ".loop")) + Path.DirectorySeparatorChar;
        string output = Path.GetFullPath(AppDriver.OutputDirectory) + Path.DirectorySeparatorChar;
        Assert.True(output.StartsWith(loop, StringComparison.OrdinalIgnoreCase), "실측 시험의 출력(NETPULSE_UITEST_OUT)은 .loop/ 아래여야 합니다");

        var userData = AppDriver.NewUserData();
        try
        {
            string id;
            string?[] first;
            using (var app = AppDriver.LaunchWith(userData))
            {
                app.WaitDiag(Screen, s => s == "firstrun", "첫 실행 안내");
                string status = app.WaitDiag(d => M5Fixtures.Str(d, "screenDiag", "status"), s => s is "Ok" or "NoNetwork" or "Unknown", "실제 탐지");
                Assert.True(status == "Ok", "이 PC 탐지 결과: " + status);
                app.WaitDiag(d => Sd(d).GetProperty("startEnabled").GetBoolean(), b => b, "시작 켜짐");
                app.Invoke("firstrun.start");
                app.WaitDiag(d => M5Fixtures.Str(d, "host"), s => s == "Measuring", "측정 시작");
                app.WaitDiag(d => d.GetProperty("rounds").GetInt32(), r => r >= 30, "30회차");
                app.Invoke("measure.stop");
                id = M5Fixtures.Str(app.WaitDiag(d => d.GetProperty("finished"), f => f.ValueKind == JsonValueKind.Object, "측정 끝"), "Id");
                app.Invoke("measure.result.records");
                first = Values(app, id);
                app.CaptureClient("live-records-detail");
                Assert.Equal(0, app.Close());
            }
            using (var app = AppDriver.LaunchWith(userData, "--screen", "records"))
            {
                app.WaitDiag(d => Sd(d).ValueKind == JsonValueKind.Object && Sd(d).TryGetProperty("listed", out var l) && l.GetBoolean() && Sd(d).GetProperty("analyzing").GetInt32() == 0, b => b, "기록 목록");
                int row = Sd(app.Diag()).GetProperty("rows").EnumerateArray().First(r => r.GetProperty("Id").GetString() == id).GetProperty("n").GetInt32();
                app.Invoke($"records.row.{row}");
                Assert.Equal(first, Values(app, id));
                app.CaptureClient("live-records-reopen");
                File.WriteAllLines(Path.Combine(AppDriver.OutputDirectory, "live-records-log.jsonl"), app.ReadLog().Select(e => e.GetRawText()));
                Assert.Equal(0, app.Close());
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>상세가 열리길 기다려 화면 값과 파일 재분석 값이 같은지 보고 그 값을 돌려준다.</summary>
    private static string?[] Values(AppDriver app, string id)
    {
        var d = app.WaitDiag(x => x, x => Sd(x).ValueKind == JsonValueKind.Object && Sd(x).TryGetProperty("detail", out var v) && v.ValueKind == JsonValueKind.Object
            && Sd(x).TryGetProperty("detailComplete", out var c) && c.GetBoolean(), "기록 상세");
        var detail = Sd(d).GetProperty("detail");
        Assert.Equal(id, detail.GetProperty("Id").GetString());
        var record = RecordReader.Read(RecordReader.FindSegments(id, RootsOf(app.UserData)));
        var a = MeasurementAnalysis.Analyze(record);
        Assert.Equal(a.Grade.Grade.ToString(), detail.GetProperty("grade").GetString());
        Assert.Equal(a.Episodes.Count, detail.GetProperty("episodes").GetInt32());
        Assert.Equal(RecordView.EndText(RecordLifecycle.Ended, record.Summary?.EndReason ?? record.EndReason), detail.GetProperty("end").GetString());
        return [detail.GetProperty("grade").GetString(), detail.GetProperty("episodes").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), detail.GetProperty("end").GetString()];
    }

    /// <summary>실측의 기록 위치: 사용자 폴더의 records와 M3 기본 대체 위치 (LiveAppWorld와 같음).</summary>
    private static IReadOnlyList<string> RootsOf(string userData) => [Path.Combine(userData, "records"), DiskRecordRoot.Defaults().Fallback.Path!];
}
