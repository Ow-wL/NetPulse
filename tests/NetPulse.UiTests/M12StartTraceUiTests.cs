using System.IO;
using System.Text.Json;
using NetPulse.Core.Live;

namespace NetPulse.UiTests;

/// <summary>
/// M12 설계 B2: 시작 단계 기록. 정상 실행은 관리 코드 진입부터 첫 화면(준비 완료)과 정상 종료(코드 0)까지 남고,
/// 강제로 끝난 실행은 정상 종료 표시가 없어 다음 실행이 그 도달 지점을 앱 기록에 남긴다.
/// </summary>
public sealed class M12StartTraceUiTests
{
    private static string[] Lines(string userData) => File.ReadAllLines(Path.Combine(userData, StartTrace.FileName));

    private static void WaitHome(AppDriver app) => app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "home", "홈");

    [Fact]
    public void NormalRunIsTracedToExitAndAKilledRunIsReportedByTheNextRun()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using (var first = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "home"))
            {
                WaitHome(first);
                AppDriver.WaitFor(() => StartTrace.Runs(Lines(userData)), r => r.Count == 1 && r[0].Steps.Contains("ready"), "첫 실행 준비 완료 기록");
                Assert.Equal(0, first.Close());
            }
            var run1 = Assert.Single(StartTrace.Runs(Lines(userData)));
            Assert.True(run1.Exited, "정상 종료 표시");
            Assert.Equal(0, run1.ExitCode);
            foreach (var step in new[] { "main", "app", "launched", "consented", "host", "shell", "activated", "loaded", "ready" })
                Assert.Contains(step, run1.Steps);
            Assert.True(Array.IndexOf(run1.Steps.ToArray(), "main") == 0, "관리 코드 진입이 첫 단계");

            using (var second = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "home"))
            {
                WaitHome(second);
                AppDriver.WaitFor(() => StartTrace.Runs(Lines(userData)), r => r.Count == 2 && r[1].Steps.Contains("ready"), "두 번째 실행 준비 완료 기록");
                second.TerminateAfterSessionEnd();   // 정상 종료 경로 없이 끝냄 (충돌과 같은 모습: 종료 표시 없음)
            }
            var run2 = StartTrace.Runs(Lines(userData))[1];
            Assert.False(run2.Exited, "강제로 끝난 실행에는 정상 종료 표시가 없다");

            using (var third = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "home"))
            {
                WaitHome(third);
                var reported = third.ReadLog().Where(e => e.GetProperty("kind").GetString() == "previousRunInterrupted").ToArray();
                var entry = Assert.Single(reported);
                Assert.Equal(run2.Run, entry.GetProperty("data").GetProperty("run").GetString());
                Assert.Equal(run2.LastStep, entry.GetProperty("data").GetProperty("lastStep").GetString());
                Assert.DoesNotContain(third.ReadLog(), e => e.GetProperty("kind").GetString() == "startTraceFailed");
                Assert.Equal(0, third.Close());
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
