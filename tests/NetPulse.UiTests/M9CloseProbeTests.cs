using System.IO;
using System.Text.Json;

namespace NetPulse.UiTests;

/// <summary>
/// M7 이월 4 조사 (M9 설계 C2): 닫을 때 드문 0xC000027B가 UI 자동화의 닫기 요청(<c>WindowPattern.Close</c>) 때문인지 가린다.
/// 같은 작업 중 닫기 묶음(측정 중 기록 화면·진단 중·테마 전환 중)을 두 방식(<c>WindowPattern.Close</c>, <c>WM_CLOSE</c>)으로 각 50회 돌려
/// 비정상 종료 수와 증거 폴더를 남긴다. 판정 기준: 100회 중 0이면 "재현 없음, 계측 유지"(해결로 적지 않음).
/// 게이트 밖 조사 실행이라 게이트가 빼는 <c>Kind=Live</c>로 표시한다. 횟수는 <c>NETPULSE_CLOSE_RUNS</c>로 바꿀 수 있다.
/// </summary>
[Trait("Kind", "Live")]
public sealed class M9CloseProbeTests
{
    public static TheoryData<string> Ways => new() { "pattern", "message" };

    private static AppDriver Open(int run, string userData)
    {
        switch (run % 3)
        {
            case 0:   // 측정 중 기록 화면 (프레임 구독·목록 갱신)
                M5Fixtures.SaveProfile(userData, "steady");
                var records = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--screen", "records");
                records.WaitDiag(d => d.GetProperty("rounds").GetInt32(), r => r >= 5, "측정 중 기록 화면");
                return records;
            case 1:   // 진단 중
                M5Fixtures.SaveProfile(userData, "steady");
                var diag = AppDriver.LaunchWith(userData, [.. M5Fixtures.Args("--demo tests:diag-running --screen diagnose")]);
                diag.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
                diag.Invoke("diagnose.start");
                diag.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("partial").GetInt32(), n => n >= 1, "진단 중");
                return diag;
            default:  // 테마 전환(원형 공개) 중
                var theme = AppDriver.LaunchWith(userData, "--showcase", "--section", "overview", "--theme", "dark", "--reveal-hold", "0.5", "--freeze-motion");
                theme.WaitDiag(d => M5Fixtures.Str(d, "section"), s => s == "overview", "개요");
                theme.Select("toolbar.theme.light");
                theme.WaitDiag(d => d.GetProperty("inTransition").GetBoolean(), t => t, "전환 중");
                return theme;
        }
    }

    [Theory, MemberData(nameof(Ways))]
    public void ClosingRepeatedlyByEachRequestKindNeverCrashes(string way)
    {
        int runs = int.TryParse(Environment.GetEnvironmentVariable("NETPULSE_CLOSE_RUNS"), out var n) && n > 0 ? n : 50;
        var failures = new List<object>();
        var started = DateTime.UtcNow;
        for (int run = 0; run < runs; run++)
        {
            var userData = AppDriver.NewUserData();
            try
            {
                using var app = Open(run, userData);
                int code = way == "message" ? app.CloseByMessage() : app.Close();
                if (code != 0) failures.Add(new { run, kind = run % 3, error = "exit " + code });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // AppDriver가 비정상 종료·시간 초과면 증거 폴더(crash-*)를 남기고 예외를 던진다
                failures.Add(new { run, kind = run % 3, error = ex.GetType().Name + ": " + ex.Message });
            }
            finally { AppDriver.DeleteUserData(userData); }
        }
        var dir = Path.Combine(AppDriver.OutputDirectory, "g5");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"m9-close-probe-{way}.json"), JsonSerializer.Serialize(new
        {
            way, runs, failures = failures.Count, details = failures, minutes = Math.Round((DateTime.UtcNow - started).TotalMinutes, 1),
            kinds = new[] { "records-measuring", "diagnosing", "theme-reveal" },
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(failures.Count == 0, $"{way}: {runs}회 중 {failures.Count}회 실패 (g5/m9-close-probe-{way}.json)");
    }
}
