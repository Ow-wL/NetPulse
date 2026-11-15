using System.IO;
using System.Text.Json;
using NetPulse.Core.Diagnostics;

namespace NetPulse.UiTests;

/// <summary>
/// 실측 진단 (M6 설계 7.3, 로컬, 공개하지 않음): 데모 옵션 없이 첫 실행 → 측정 시작 → <b>측정 중 빠른 진단</b> → 사이트 점검(example.com, 화면 값으로만 확인)
/// → 측정 중지 → 다시 실행 → 진단 기록이 있고 measurementId = 그 측정, 홈 카드가 그 결과. 판정 값 자체는 통과 조건이 아니다.
/// DNS 캐시 비우기·설정·폴더 열기는 누르지 않는다. 이 PC의 주소가 화면·로그에 나오므로 출력은 <c>.loop/</c> 아래만, 게이트에서는 특성으로 뺀다:
/// <c>NETPULSE_UITEST_OUT=.loop/shots/m6-live dotnet test --project tests/NetPulse.UiTests -- --filter-trait Kind=Live</c>
/// </summary>
[Trait("Kind", "Live")]
public sealed class M6LiveRunTests
{
    private static string Screen(JsonElement d) => M5Fixtures.Str(d, "screen");
    private static JsonElement Sd(JsonElement d) => d.GetProperty("screenDiag");

    [Fact]
    public void RealDiagnosisDuringAMeasurementIsSavedWithThatMeasurement()
    {
        // 출력은 저장소의 .loop/ 아래로만 (앱을 띄우기 전에 검사, 구현 중 결정 "Live 특성")
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NetPulse.slnx"))) root = root.Parent;
        Assert.True(root is not null, "저장소 루트를 찾지 못했습니다");
        string loop = Path.GetFullPath(Path.Combine(root!.FullName, ".loop")) + Path.DirectorySeparatorChar;
        string output = Path.GetFullPath(AppDriver.OutputDirectory) + Path.DirectorySeparatorChar;
        Assert.True(output.StartsWith(loop, StringComparison.OrdinalIgnoreCase), "실측 시험의 출력(NETPULSE_UITEST_OUT)은 .loop/ 아래여야 합니다");

        var userData = AppDriver.NewUserData();
        string sessionId;
        int conclusion;
        try
        {
            using (var app = AppDriver.LaunchWith(userData))
            {
                app.WaitDiag(Screen, s => s == "firstrun", "첫 실행 안내");
                string status = app.WaitDiag(d => M5Fixtures.Str(d, "screenDiag", "status"), s => s is "Ok" or "NoNetwork" or "Unknown", "실제 탐지");
                Assert.True(status == "Ok", "이 PC 탐지 결과: " + status);
                app.WaitDiag(d => Sd(d).GetProperty("startEnabled").GetBoolean(), b => b, "시작 켜짐");
                app.Invoke("firstrun.start");
                app.WaitDiag(d => M5Fixtures.Str(d, "host"), s => s == "Measuring", "측정 시작");
                app.WaitDiag(d => d.GetProperty("rounds").GetInt32(), r => r >= 5, "5회차");
                sessionId = M5Fixtures.Str(app.Diag(), "session");

                // 측정 중 빠른 진단 (최대 약 6초)
                app.Select("nav.diagnose");
                app.WaitDiag(Screen, s => s == "diagnose", "진단 화면");
                app.Invoke("diagnose.start");
                var d = app.WaitDiag(x => x, x => M5Fixtures.Str(Sd(x), "operation") == "None" && Sd(x).GetProperty("conclusion").ValueKind == JsonValueKind.Number, "진단 끝");
                conclusion = Sd(d).GetProperty("conclusion").GetInt32();
                Assert.Equal(sessionId, M5Fixtures.Str(Sd(d), "measurementId"));
                Assert.True(M5Fixtures.Str(Sd(d), "saved").Length > 0, "진단 기록 저장: " + M5Fixtures.Str(Sd(d), "saveError"));
                app.CaptureClient("live-diagnose");

                // 사이트 점검 (기록하지 않음: 화면 값으로만)
                app.SetText("diagnose.site.input", "example.com");
                app.Invoke("diagnose.site.run");
                app.WaitDiag(x => M5Fixtures.Str(Sd(x), "site", "dns"), s => s.Length > 0, "사이트 점검 결과");
                app.CaptureClient("live-site");
                Assert.Empty(Sd(app.Diag()).GetProperty("actions").EnumerateArray());   // 실제 동작(비우기·설정·폴더)은 누르지 않았다

                app.Select("nav.measure");
                app.WaitDiag(Screen, s => s == "measure", "측정 화면");
                app.Invoke("measure.stop");
                app.WaitDiag(x => x.GetProperty("finished").ValueKind, k => k == JsonValueKind.Object, "측정 끝");
                File.WriteAllLines(Path.Combine(AppDriver.OutputDirectory, "live-diag-log-1.jsonl"), app.ReadLog().Select(e => e.GetRawText()));
                Assert.Equal(0, app.Close());
            }

            // 진단 기록: 그 측정 Id, 화면과 같은 결론
            var files = Directory.GetFiles(Path.Combine(userData, "diagnoses"), "*.json");
            var stored = DiagnosisRecord.Parse(File.ReadAllText(Assert.Single(files)));
            Assert.Equal(sessionId, stored.MeasurementId);
            Assert.Equal(conclusion, stored.MainConclusion);

            // 다시 실행: 홈 진단 카드가 그 결과
            using (var app = AppDriver.LaunchWith(userData))
            {
                app.WaitDiag(Screen, s => s == "home", "다시 실행하면 홈");
                AppDriver.WaitFor(() => app.Exists("home.diagnosis.last"), e => e, "홈 진단 카드");
                Assert.StartsWith(DiagnosisTexts.Head(conclusion).Head, app.Text("home.diagnosis.last"));
                app.CaptureClient("live-relaunch-home");
                Assert.Equal(0, app.Close());
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
