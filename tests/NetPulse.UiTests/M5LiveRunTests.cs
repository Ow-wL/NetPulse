using System.IO;
using System.Text.Json;
using NetPulse.Core.Records;

namespace NetPulse.UiTests;

/// <summary>
/// 실측 시나리오 (M5 설계 7.3, 로컬, 공개하지 않음): 데모 옵션 없이 빈 사용자 폴더로 첫 실행(실제 탐지) → 측정 시작 → 20초 뒤 증상 표시
/// → 중지 → 기록 저장 확인 → 다시 실행해 최근 측정에 있음. 응답 성공 여부는 통과 조건이 아니다.
/// 이 PC의 주소가 화면과 로그에 나오므로 게이트(<c>build/ui-test.ps1</c>)에서 빼고, 출력 폴더를 <c>.loop/</c> 아래로 두고 직접 돌린다:
/// <c>NETPULSE_UITEST_OUT=.loop/shots/m5-live dotnet run --project tests/NetPulse.UiTests -- -class NetPulse.UiTests.M5LiveRunTests</c>
/// </summary>
public sealed class M5LiveRunTests
{
    private static string Screen(JsonElement d) => M5Fixtures.Str(d, "screen");
    private static string Host(JsonElement d) => M5Fixtures.Str(d, "host");

    [Fact]
    public void RealDetectionMeasureMarkStopAndRelaunch()
    {
        var userData = AppDriver.NewUserData();
        string sessionId;
        try
        {
            using (var app = AppDriver.LaunchWith(userData))
            {
                app.WaitDiag(Screen, s => s == "firstrun", "첫 실행 안내");
                string status = app.WaitDiag(d => M5Fixtures.Str(d, "screenDiag", "status"), s => s is "Ok" or "NoNetwork" or "Unknown", "실제 탐지");
                app.CaptureClient("live-firstrun");
                Assert.True(status == "Ok", "이 PC 탐지 결과: " + status);
                app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("startEnabled").GetBoolean(), b => b, "시작 켜짐");
                app.Invoke("firstrun.start");
                app.WaitDiag(d => (Screen(d), Host(d)), s => s == ("measure", "Measuring"), "측정 시작");

                app.WaitDiag(d => d.GetProperty("rounds").GetInt32(), r => r >= 20, "20회차");
                app.Invoke("measure.mark");
                app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("memoOpen").GetBoolean(), o => o, "메모 패널");
                app.SetText("measure.memo.text", "실측 증상 표시");
                app.Invoke("measure.memo.save");
                app.WaitDiag(d => d.GetProperty("marks").EnumerateArray().Any(m => M5Fixtures.Str(m, "Memo") == "실측 증상 표시" && M5Fixtures.Str(m, "state") == "Saved"), b => b, "메모 저장");
                app.CaptureClient("live-measuring");

                app.Invoke("measure.stop");
                var finished = app.WaitDiag(d => d.GetProperty("finished"), f => f.ValueKind == JsonValueKind.Object, "측정 끝");
                Assert.Equal("Stopped", M5Fixtures.Str(finished, "end"));
                sessionId = M5Fixtures.Str(finished, "Id");
                app.CaptureClient("live-stopped");
                File.WriteAllLines(Path.Combine(AppDriver.OutputDirectory, "live-log-1.jsonl"), app.ReadLog().Select(e => e.GetRawText()));
                app.Close();
            }

            // 기록: 20회차 이상, 표시 하나와 메모
            var record = RecordReader.Read(RecordReader.FindSegments(sessionId, M5Fixtures.RecordRoots(userData)));
            Assert.True(record.Rounds.Count >= 20, $"회차 {record.Rounds.Count}");
            var marks = SymptomMarks.FromEvents(record.Events, record.NoteLines).Marks;
            Assert.Contains(marks, m => m.Memo == "실측 증상 표시");

            // 다시 실행: 첫 실행 없이 홈, 최근 측정에 그 기록
            using (var app = AppDriver.LaunchWith(userData))
            {
                app.WaitDiag(Screen, s => s == "home", "다시 실행하면 홈");
                app.WaitDiag(d => d.GetProperty("recent").GetProperty("items").EnumerateArray().Any(i => M5Fixtures.Str(i, "Id") == sessionId), b => b, "최근 측정에 있음");
                app.CaptureClient("live-relaunch-home");
                File.WriteAllLines(Path.Combine(AppDriver.OutputDirectory, "live-log-2.jsonl"), app.ReadLog().Select(e => e.GetRawText()));
                app.Close();
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
